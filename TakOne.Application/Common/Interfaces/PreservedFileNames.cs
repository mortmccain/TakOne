namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Outcome of <see cref="PreservedFileNames.TryBuildPreservedName"/> —
/// distinguishes the two rejection causes so the STORAGE caller
/// (<c>LocalFileStorage.SaveWithOriginalNameAsync</c>) can surface its
/// historical, distinct error messages unchanged.
/// </summary>
public enum PreservedNameBuildResult
{
    /// <summary>The name was built successfully.</summary>
    Built,

    /// <summary>
    /// The base name had no characters left after the allowlist pass
    /// (e.g. <c>"(((.jpg"</c> or a fully non-ASCII name) — nothing to
    /// store under.
    /// </summary>
    EmptyBase,

    /// <summary>
    /// The final name (base + extension) exceeded
    /// <see cref="PreservedFileNames.MaxPreservedFileNameLength"/> — a
    /// silent truncation could collide two different files, so the caller
    /// must reject instead.
    /// </summary>
    TooLong
}

/// <summary>
/// THE single canonical rule for product-picture file names that must
/// survive a round trip through storage — implemented ONCE and used from
/// BOTH sides of that round trip:
///
/// <list type="bullet">
///   <item>
///     <b>Upload side</b> — <c>LocalFileStorage.SaveWithOriginalNameAsync</c>
///     (the Settings page's bulk picture upload) names every stored file
///     with <see cref="TryBuildPreservedName"/>, forcing the extension from
///     the SNIFFED content type.
///   </item>
///   <item>
///     <b>Import side</b> — <c>ImportProductsCommandHandler</c> normalizes
///     the CSV's picture file-name cell with
///     <see cref="TryNormalizeClientSuppliedName"/> before building the
///     product's <c>PictureUrl</c>.
///   </item>
/// </list>
///
/// WHY THIS CLASS EXISTS (the incident it prevents):
/// the two sides used to implement the naming rule INDEPENDENTLY — the
/// upload sanitized the name (stripping spaces, <c>&amp;</c>, parentheses,
/// … to the <c>[A-Za-z0-9._-]</c> allowlist) while the import linked the
/// CSV's name VERBATIM. A catalog whose photos are named
/// <c>"Pizza flour 500g.png"</c> therefore landed on disk as
/// <c>Pizzaflour500g.png</c> but was linked as
/// <c>/uploads/products/Pizza flour 500g.png</c> — a URL that can never
/// resolve, so every such product rendered without its picture. Only the
/// photos whose names were already allowlist-clean (e.g. <c>ABC.png</c>)
/// displayed. With the rule centralized here, the upload's on-disk name
/// and the import's URL can never drift apart again.
///
/// THE RULE (identical to the historical storage behavior — no stored
/// name changes):
/// <list type="number">
///   <item>
///     Split the raw name at its LAST dot: everything before is the base,
///     everything after is the client extension (which the STORAGE caller
///     ignores — it forces the sniffed type's extension instead).
///   </item>
///   <item>
///     Keep only <c>[A-Za-z0-9._-]</c> in the base. This removes path
///     separators, whitespace, Unicode bi-di tricks, control characters,
///     and everything else dangerous — the surviving name can never
///     escape the products directory and needs no URL encoding.
///   </item>
///   <item>
///     Trim leading/trailing dots from the sanitized base; an empty
///     result is a rejection (<see cref="PreservedNameBuildResult.EmptyBase"/>).
///   </item>
///   <item>
///     Append <c>.{extension}</c> (when non-empty) and enforce the
///     <see cref="MaxPreservedFileNameLength"/> cap — over-length is a
///     rejection (<see cref="PreservedNameBuildResult.TooLong"/>), never a
///     truncation.
///   </item>
/// </list>
///
/// WHY STRIP RATHER THAN ENCODE: the names that matter most here are
/// <c>SaveAsync</c>'s own 32-hex-char outputs (pure <c>[0-9a-f]</c>),
/// which pass through untouched. Anything exotic from a hand-picked
/// upload set degrades to its safe subset — <c>"My Photo (1)!.jpg"</c>
/// → <c>MyPhoto1.jpg</c> — which keeps the rule total (never throws for
/// weird-but-harmless names) while remaining collision-auditable by the
/// caller.
/// </summary>
public static class PreservedFileNames
{
    /// <summary>
    /// Hard cap on a preserved file name (base + dot + extension). A name
    /// at or under this length is built; anything longer is rejected — a
    /// silent truncation could collide two different files.
    /// </summary>
    public const int MaxPreservedFileNameLength = 128;

    /// <summary>
    /// Builds the on-disk name for a name-preserving upload — the rule
    /// <c>LocalFileStorage.SaveWithOriginalNameAsync</c> applies to every
    /// bulk-uploaded picture.
    /// </summary>
    /// <param name="rawFileName">
    /// The caller-supplied file name, exactly as picked (e.g.
    /// <c>"Pizza flour 500g.png"</c>). Untrusted input — never used
    /// verbatim.
    /// </param>
    /// <param name="forcedExtension">
    /// The canonical extension for the SNIFFED content type
    /// (<c>jpg</c>/<c>png</c>/<c>webp</c>, WITHOUT the leading dot) — it
    /// always wins over any client-supplied extension, so a JPEG named
    /// <c>"evil.png"</c> lands as <c>evil*.jpg</c>-family names only. May
    /// be empty for callers that have no sniffed type (the name then
    /// carries no extension).
    /// </param>
    /// <param name="preservedName">
    /// The final name (<c>{sanitizedBase}.{extension}</c>) when the
    /// result is <see cref="PreservedNameBuildResult.Built"/>; otherwise
    /// <c>null</c>.
    /// </param>
    public static PreservedNameBuildResult TryBuildPreservedName(
        string rawFileName,
        string forcedExtension,
        out string? preservedName)
    {
        // Base name: strip everything from the LAST dot on. If there's no
        // dot, the whole string is the base. Path separators are stripped
        // by the allowlist below, so a "dir/evil.jpg" input degrades to
        // "direvil" + forced extension — it can never escape the products
        // directory.
        var lastDot = rawFileName.LastIndexOf('.');
        var baseName = lastDot > 0 ? rawFileName[..lastDot] : rawFileName;

        // Allowlist pass: keep only ASCII letters, digits, dots, dashes,
        // underscores. This removes separators, whitespace, Unicode
        // bi-di tricks, control chars, and everything else dangerous.
        var sb = new System.Text.StringBuilder(baseName.Length);
        foreach (var ch in baseName)
        {
            if ((ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '.' || ch == '-' || ch == '_')
            {
                sb.Append(ch);
            }
        }

        var sanitizedBase = sb.ToString().Trim('.');

        if (sanitizedBase.Length == 0)
        {
            preservedName = null;
            return PreservedNameBuildResult.EmptyBase;
        }

        var finalName = forcedExtension.Length > 0
            ? $"{sanitizedBase}.{forcedExtension}"
            : sanitizedBase;

        if (finalName.Length > MaxPreservedFileNameLength)
        {
            preservedName = null;
            return PreservedNameBuildResult.TooLong;
        }

        preservedName = finalName;
        return PreservedNameBuildResult.Built;
    }

    /// <summary>
    /// Normalizes a CSV-supplied picture file name to the name the bulk
    /// upload WOULD have stored it under — the rule
    /// <c>ImportProductsCommandHandler</c> applies before building a
    /// product's <c>PictureUrl</c>, so the URL always matches the on-disk
    /// file regardless of how the name was written in the CSV.
    /// </summary>
    /// <param name="rawFileName">
    /// The picture file-name cell from the import CSV (e.g.
    /// <c>"Pizza flour 500g.png"</c> — the original photo name, not
    /// necessarily what a prior export recorded). <c>null</c>/empty means
    /// "no picture".
    /// </param>
    /// <param name="normalizedName">
    /// The normalized name (e.g. <c>"Pizzaflour500g.png"</c>) when the
    /// method returns <c>true</c>; otherwise <c>null</c>.
    /// </param>
    /// <remarks>
    /// EXTENSION HANDLING: the storage side forces the extension from the
    /// SNIFFED content type, which the import cannot observe. The client
    /// name's own extension is used as the faithful proxy, canonicalized
    /// exactly the way the sniffer's map does: <c>jpg/jpeg</c> (any case)
    /// → <c>jpg</c>, <c>png</c> → <c>png</c>, <c>webp</c> → <c>webp</c>.
    /// A name whose genuine bytes were uploadable always round-trips:
    /// a real JPEG named <c>"My photo.jpeg"</c> was stored as
    /// <c>Myphoto.jpg</c>, and this method normalizes the CSV cell to
    /// exactly that. Unknown extensions (e.g. <c>.gif</c> — never
    /// uploadable) pass through sanitized, so such a row still gets a
    /// well-formed URL that simply resolves to nothing (best-effort,
    /// same as an empty picture cell).
    /// </remarks>
    public static bool TryNormalizeClientSuppliedName(
        string? rawFileName,
        out string? normalizedName)
    {
        normalizedName = null;

        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return false;
        }

        var trimmed = rawFileName.Trim();

        var outcome = TryBuildPreservedName(
            trimmed,
            ClientCanonicalExtension(trimmed),
            out var built);

        if (outcome != PreservedNameBuildResult.Built)
        {
            return false;
        }

        normalizedName = built;
        return true;
    }

    /// <summary>
    /// Derives the canonical extension from the client name's own
    /// extension: the sniffing map's aliases collapse (jpeg→jpg, any case
    /// folds to lower), genuine image extensions map to their canonical
    /// form, and anything else passes through the same allowlist as the
    /// base name (lowercased) so the final name is always URL-safe.
    /// Returns an empty string for names with no usable extension.
    /// </summary>
    private static string ClientCanonicalExtension(string fileName)
    {
        var lastDot = fileName.LastIndexOf('.');

        // No dot, or a TRAILING dot ("photo.") → no usable extension.
        if (lastDot < 0 || lastDot == fileName.Length - 1)
        {
            return string.Empty;
        }

        var lowered = fileName[(lastDot + 1)..].ToLowerInvariant();

        return lowered switch
        {
            "jpg" or "jpeg" => "jpg",
            "png" => "png",
            "webp" => "webp",
            _ => AllowlistSegment(lowered)
        };
    }

    /// <summary>
    /// Applies the <c>[A-Za-z0-9._-]</c> allowlist to a short lowercase
    /// segment (an exotic client extension). Inputs here are already
    /// lowercase; only the character filter matters.
    /// </summary>
    private static string AllowlistSegment(string segment)
    {
        var sb = new System.Text.StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if ((ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch == '.' || ch == '-' || ch == '_')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
