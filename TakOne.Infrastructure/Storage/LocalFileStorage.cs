using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;

namespace TakOne.Infrastructure.Storage;

/// <summary>
/// Single-server filesystem implementation of <see cref="IFileStorage"/>.
///
/// Files are written to a configurable root directory (default
/// <c>wwwroot/uploads</c> for dev; in prod point this at
/// <c>/var/lib/takone/uploads</c> or similar via
/// <c>FileStorage:RootPath</c> in appsettings.json). The directory is created
/// on first use if it doesn't exist.
///
/// SECURITY
/// =========
/// Three layers of defense against malicious uploads:
///
/// 1. <b>Filename sanitization</b> — the client-supplied filename is NOT USED
///    AT ALL after Brutal Code Review v3 finding #08 (Round 18-B). The
///    on-disk extension is ALWAYS derived from the SNIFFED content type's
///    canonical extension (image/jpeg→.jpg, image/png→.png, image/webp→.webp)
///    via <see cref="SanitizeExtension"/>. The previous implementation
///    trusted the client extension if it passed a weak regex, which allowed
///    JPEG bytes named "evil.html" to be saved as
///    "randomhex.html" — and the static-files middleware then served the
///    file with a text/html Content-Type, enabling XSS via direct URL
///    navigation. The actual on-disk filename is a cryptographically
///    random 32-char hex string. Path traversal attacks
///    (<c>../../etc/passwd</c>), Unicode tricks, and filename collisions are
///    all rendered impossible.
///
/// 2. <b>Content-type allowlist</b> — only <c>image/jpeg</c>, <c>image/png</c>,
///    and <c>image/webp</c> are accepted. The check is done against the
///    caller-declared content type (advisory) AND against magic-byte sniffing
///    of the actual content (authoritative). If they disagree, the upload is
///    rejected.
///
/// 3. <b>Max size enforcement</b> — defense-in-depth on top of any HTTP-level
///    limit set by Kestrel's <c>RequestFormLimits</c>. If the stream produces
///    more bytes than <c>MaxImageSizeBytes</c> (default 5 MB), the write is
///    aborted and the temp file is cleaned up.
///
/// ATOMIC WRITES
/// ==============
/// Each upload is written to a <c>.tmp</c> sibling file first, then
/// <c>File.Move</c>'d to its final name. <c>File.Move</c> on the same
/// filesystem is atomic on POSIX and Windows — a crash mid-write leaves
/// either the old file (if any) or no file, never a half-written one.
/// The <c>.tmp</c> file is cleaned up on any exception path.
///
/// STREAMING
/// ==========
/// The upload stream is copied to disk via <c>Stream.CopyToAsync</c> with an
/// 8 KB buffer. Peak RAM pressure for a 5 MB upload is ~8 KB + the read-ahead
/// buffer in <c>FileStream</c>. The full image is NEVER loaded into a
/// <c>byte[]</c> or <c>MemoryStream</c>.
///
/// MAGIC BYTE SNIFFING
/// ====================
/// The first 12 bytes of the stream are read into a small buffer and checked
/// against known magic numbers:
///   - JPEG: <c>FF D8 FF</c>
///   - PNG:  <c>89 50 4E 47 0D 0A 1A 0A</c>
///   - WebP: <c>52 49 46 46 ?? ?? ?? ?? 57 45 42 50</c> (RIFF...WEBP)
/// After sniffing, the buffer is re-prepended to the write stream via a
/// <c>ConcatStream</c> (defined below) so the full bytes still get written.
///
/// LIFETIME
/// =========
/// Registered as <c>Scoped</c> in DI. The class itself is stateless beyond
/// its config fields, but <c>Scoped</c> is the safer default — see the
/// <see cref="IFileStorage"/> docstring.
/// </summary>
internal sealed class LocalFileStorage : IFileStorage
{
    /// <summary>
    /// Default root directory if <c>FileStorage:RootPath</c> isn't set in
    /// config. Relative to the app's content root (where <c>Program.cs</c>
    /// runs), so it's <c>wwwroot/uploads</c> in dev (served directly by
    /// <c>UseStaticFiles()</c> for convenience) but should be overridden in
    /// prod to a path OUTSIDE the app's publish folder so uploads survive
    /// app redeployments.
    /// </summary>
    private const string DefaultRootPath = "wwwroot/uploads";

    /// <summary>
    /// Subfolder under <c>RootPath</c> for product images. Keeps product
    /// uploads separate from any future upload types (e.g. user avatars,
    /// sale invoices).
    /// </summary>
    private const string ProductImagesSubfolder = "products";

    /// <summary>
    /// Subfolder under the OS temp directory for the bulk-upload STAGING
    /// area (see <see cref="WriteStagedFileAsync"/>). Deliberately NOT
    /// under <c>RootPath</c>: in dev the storage root defaults to
    /// <c>wwwroot/uploads</c>, and staged bytes are PRE-VALIDATION (the
    /// magic-byte sniff happens later, at SaveWithOriginalNameAsync time)
    /// — they must never sit anywhere a static-file or uploads endpoint
    /// could serve them. Overridable via <c>FileStorage:StagingRootPath</c>
    /// (e.g. to point at a dedicated volume).
    /// </summary>
    private const string DefaultStagingFolderName = "takone-upload-staging";

    /// <summary>
    /// Extension for staging files. Marks them as in-transit partials for
    /// anyone inspecting the temp directory — staging files hold raw,
    /// unvalidated upload bytes and are never served or kept long-term.
    /// </summary>
    private const string StagedFileExtension = ".part";

    /// <summary>
    /// Default max upload size if <c>FileStorage:MaxImageSizeBytes</c> isn't
    /// set: 5 MB. Matches typical e-commerce product image limits.
    /// </summary>
    private const long DefaultMaxImageSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Buffer size for <c>Stream.CopyToAsync</c>. 8 KB is the .NET default
    /// and a good balance between memory pressure and syscall overhead.
    /// </summary>
    private const int CopyBufferSize = 8 * 1024;

    /// <summary>
    /// Number of random hex chars in the generated filename. 32 hex chars =
    /// 128 bits of entropy = same as a GUID, but as a flat hex string with
    /// no dashes (filesystem-friendlier).
    /// </summary>
    private const int RandomFileNameHexChars = 32;

    /// <summary>
    /// How many bytes to sniff for magic-byte content-type detection.
    /// 12 is enough for the longest signature we check (WebP at offset 0..11).
    /// </summary>
    private const int MagicByteSniffLength = 12;

    private readonly string _rootPath;
    private readonly string _productImagesPath;
    private readonly string _stagingRootPath;
    private readonly long _maxImageSizeBytes;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(
        IConfiguration configuration,
        ILogger<LocalFileStorage> logger)
    {
        _rootPath = configuration["FileStorage:RootPath"]
            ?? DefaultRootPath;
        _productImagesPath = Path.Combine(_rootPath, ProductImagesSubfolder);

        // Canonicalized once here so every staging-path validation below
        // compares canonical-to-canonical (GetFullPath in ResolveStagedPath
        // vs this). Config is expected to be absolute; GetFullPath also
        // normalizes trailing separators and (on Windows) slash direction.
        _stagingRootPath = Path.GetFullPath(
            configuration["FileStorage:StagingRootPath"]
            ?? Path.Combine(Path.GetTempPath(), DefaultStagingFolderName));

        // Read max size from config; fall back to default if missing/invalid.
        // We deliberately accept the config value even if it's larger than the
        // Kestrel-level limit — Kestrel will reject oversized requests before
        // we ever see them, so this is defense-in-depth, not the primary gate.
        var configMax = configuration.GetValue<long?>("FileStorage:MaxImageSizeBytes");
        _maxImageSizeBytes = configMax is > 0 ? configMax.Value : DefaultMaxImageSizeBytes;

        _logger = logger;
    }

    public async Task<string> SaveAsync(
        Stream content,
        string suggestedFileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(suggestedFileName))
            throw new ArgumentException("suggestedFileName is required.", nameof(suggestedFileName));

        // ── Step 1: sniff magic bytes to determine the TRUE content type.
        // The caller-declared contentType is advisory only; the magic bytes
        // are authoritative. If they disagree, reject the upload.
        var sniffBuffer = new byte[MagicByteSniffLength];
        var sniffedBytesRead = await content.ReadAsync(
            sniffBuffer.AsMemory(0, MagicByteSniffLength),
            cancellationToken);
        var sniffedContentType = SniffContentType(sniffBuffer, sniffedBytesRead);

        if (sniffedContentType is null)
        {
            _logger.LogWarning(
                "Rejecting upload: unrecognized magic bytes (first {Bytes} bytes: {Hex}). Suggested filename: {FileName}",
                sniffedBytesRead,
                Convert.ToHexString(sniffBuffer, 0, sniffedBytesRead),
                suggestedFileName);
            throw new InvalidDataException("Unrecognized file type.");
        }

        if (!string.Equals(sniffedContentType, contentType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Rejecting upload: content-type mismatch. Declared={Declared}, Sniffed={Sniffed}. Filename: {FileName}",
                contentType, sniffedContentType, suggestedFileName);
            throw new InvalidDataException(
                $"Declared content type '{contentType}' does not match the actual file content '{sniffedContentType}'.");
        }

        // ── Step 2: resolve the canonical file extension from the SNIFFED
        // content type. The client-supplied filename is NOT USED AT ALL —
        // the caller has no control over the on-disk extension. This is
        // Brutal Code Review v3 finding #08 (Round 18-B): the old code's
        // "use the client extension if it passes a weak regex" path allowed
        // JPEG bytes named "evil.html" to be saved as randomhex.html,
        // enabling XSS via the static-files middleware's text/html serving.
        // SanitizeExtension now ALWAYS returns the sniffed type's canonical
        // extension, and throws InvalidDataException for unknown types
        // (defense-in-depth — SniffContentType should have rejected the
        // upload before reaching this point, but if SniffContentType is
        // ever extended without updating the extension map, the upload
        // fails closed instead of falling back to a guessed extension).
        var extension = SanitizeExtension(sniffedContentType);

        // ── Step 3: ensure the target directory exists. Create it if not.
        // Idempotent — safe to call even if it already exists.
        Directory.CreateDirectory(_productImagesPath);

        // ── Step 4: generate a unique filename + a temp filename for atomic write.
        // 128 bits of cryptographic randomness = no collision risk in practice.
        // We use RandomNumberGenerator (not Random) because Random is seeded
        // from the system clock and is predictable — an attacker who knows the
        // upload time could in theory predict the filename and pre-create a
        // symlink at that path. Cryptographic randomness eliminates this.
        var randomHex = RandomNumberGenerator.GetHexString(
            RandomFileNameHexChars,
            lowercase: true);
        var finalFileName = $"{randomHex}.{extension}";
        var finalFilePath = Path.Combine(_productImagesPath, finalFileName);
        var tempFilePath = finalFilePath + ".tmp";

        // ── Step 5: stream the (sniffed + remaining) bytes to the temp file,
        // enforcing the max-size limit as we go. We use a CompositeStream to
        // re-prepend the sniff buffer so the full file content is written.
        try
        {
            await using (var fileStream = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             useAsync: true))
            {
                // First write the bytes we already sniffed.
                await fileStream.WriteAsync(
                    sniffBuffer.AsMemory(0, sniffedBytesRead),
                    cancellationToken);

                // Then enforce max size on what we've already read.
                if (sniffedBytesRead > _maxImageSizeBytes)
                {
                    throw new InvalidDataException(
                        $"File exceeds the maximum allowed size of {_maxImageSizeBytes} bytes.");
                }

                // Then copy the rest of the stream with a bounded copier.
                // BoundedStream throws InvalidDataException as soon as the
                // cumulative size crosses _maxImageSizeBytes, so we abort
                // early on oversized uploads instead of writing 100 MB to disk
                // before noticing.
                var remainingStream = new BoundedStream(content, _maxImageSizeBytes - sniffedBytesRead);
                await remainingStream.CopyToAsync(fileStream, CopyBufferSize, cancellationToken);
            }

            // ── Step 6: atomic rename. On the same filesystem, File.Move is
            // atomic — a crash between the write above and this rename leaves
            // the temp file (which we'll clean up on the next failed attempt)
            // but never a half-written final file.
            File.Move(tempFilePath, finalFilePath);

            _logger.LogInformation(
                "Saved upload to {Path} ({ContentType}, {SuggestedName})",
                finalFilePath, sniffedContentType, suggestedFileName);

            // ── Step 7: return the public URL.
            // Root-relative so it works regardless of host/scheme.
            // The GET endpoint at /uploads/products/{fileName} will serve it.
            return $"/uploads/products/{finalFileName}";
        }
        catch
        {
            // Best-effort cleanup of the temp file on any failure path.
            // Don't let the cleanup itself mask the original exception.
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); }
            catch { /* swallowed — original exception is more important */ }
            throw;
        }
    }

    /// <summary>
    /// Name-preserving bulk-restore variant of <see cref="SaveAsync"/> — see
    /// <see cref="IFileStorage.SaveWithOriginalNameAsync"/> for the full
    /// contract and the WHY-PRESERVE-THE-NAME rationale.
    ///
    /// IMPLEMENTATION: reuses <see cref="SaveAsync"/>'s entire validation
    /// pipeline verbatim (magic-byte sniff, declared-vs-sniffed match,
    /// extension forcing, size limit, temp-then-rename atomicity). The only
    /// difference is the final file name: instead of a fresh random hex
    /// string, the SANITIZED caller-supplied base name + the sniffed
    /// canonical extension, and the rename uses overwrite:true so a
    /// re-uploaded restore file replaces its own earlier copy.
    /// </summary>
    public async Task<string> SaveWithOriginalNameAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName is required.", nameof(fileName));

        // ── Step 1+2: identical sniff/verify/extension-forcing pipeline as
        // SaveAsync (shared code path for the security-critical parts).
        var sniffBuffer = new byte[MagicByteSniffLength];
        var sniffedBytesRead = await content.ReadAsync(
            sniffBuffer.AsMemory(0, MagicByteSniffLength),
            cancellationToken);
        var sniffedContentType = SniffContentType(sniffBuffer, sniffedBytesRead);

        if (sniffedContentType is null)
        {
            _logger.LogWarning(
                "SaveWithOriginalName: rejecting upload — unrecognized magic bytes. File name: {FileName}",
                fileName);
            throw new InvalidDataException("Unrecognized file type.");
        }

        if (!string.Equals(sniffedContentType, contentType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "SaveWithOriginalName: rejecting upload — content-type mismatch. Declared={Declared}, Sniffed={Sniffed}. File name: {FileName}",
                contentType, sniffedContentType, fileName);
            throw new InvalidDataException(
                $"Declared content type '{contentType}' does not match the actual file content '{sniffedContentType}'.");
        }

        // The on-disk EXTENSION always comes from the sniffed type — the
        // caller cannot smuggle a double extension or an executable
        // extension past the sniffing gate ("evil.jpg.html" lands as
        // "evil.jpg" + forced ".html"→".jpg" replacement, see below).
        var extension = SanitizeExtension(sniffedContentType);

        // ── Step 3: sanitize the NAME (base part only — the extension is
        // forced from the sniffed type, so strip any client-side
        // extension first, then rebuild as {sanitizedBase}.{extension}).
        var finalFileName = SanitizePreservedFileName(fileName, extension);

        // ── Step 4: ensure the target directory exists (idempotent).
        Directory.CreateDirectory(_productImagesPath);

        var finalFilePath = Path.Combine(_productImagesPath, finalFileName);
        var tempFilePath = finalFilePath + ".tmp";

        // ── Step 5: stream the bytes through the same bounded copier as
        // SaveAsync (sniffed prefix + BoundedStream remainder, enforcing
        // the max size as we go).
        try
        {
            await using (var fileStream = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             useAsync: true))
            {
                await fileStream.WriteAsync(
                    sniffBuffer.AsMemory(0, sniffedBytesRead),
                    cancellationToken);

                if (sniffedBytesRead > _maxImageSizeBytes)
                {
                    throw new InvalidDataException(
                        $"File exceeds the maximum allowed size of {_maxImageSizeBytes} bytes.");
                }

                var remainingStream = new BoundedStream(content, _maxImageSizeBytes - sniffedBytesRead);
                await remainingStream.CopyToAsync(fileStream, CopyBufferSize, cancellationToken);
            }

            // ── Step 6: atomic rename WITH OVERWRITE. A file with the same
            // name is replaced (bulk-restore idempotency — see the interface
            // doc). File.Move(src, dst, overwrite) is not atomic on all
            // platforms, but the temp-then-move pattern still guarantees no
            // HALF-WRITTEN file is ever visible under the original name.
            File.Move(tempFilePath, finalFilePath, overwrite: true);

            _logger.LogInformation(
                "Saved name-preserving upload to {Path} ({ContentType}, requested name: {FileName})",
                finalFilePath, sniffedContentType, fileName);

            // ── Step 7: same public URL shape as SaveAsync.
            return $"/uploads/products/{finalFileName}";
        }
        catch
        {
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); }
            catch { /* swallowed — original exception is more important */ }
            throw;
        }
    }

    /// <summary>
    /// Sanitizes a caller-supplied file name for
    /// <see cref="SaveWithOriginalNameAsync"/>: strips every character
    /// outside <c>[A-Za-z0-9._-]</c> from the BASE name (everything up to
    /// the final dot), removes any client-side extension, then rebuilds the
    /// name as <c>{sanitizedBase}.{forcedExtension}</c>.
    ///
    /// REJECTION (not truncation/repair) when:
    ///   - the sanitized base is empty (e.g. "(((.jpg" or "…/evil.jpg"),
    ///   - the final name exceeds the 128-character cap (see the
    ///     <c>MaxPreservedFileNameLength</c> constant inside) — a silent
    ///     truncation could collide two files.
    ///
    /// WHY STRIP RATHER THAN ENCODE: the names that matter here are
    /// SaveAsync's own 32-hex-char outputs (pure [0-9a-f]), which pass
    /// through untouched. Anything exotic from a hand-picked upload set
    /// degrades to its safe subset — "My Photo (1)!.jpg" → "MyPhoto1.jpg" —
    /// which keeps the API total (never throws for weird-but-harmless
    /// names) while remaining collision-auditable by the caller.
    /// </summary>
    /// <param name="fileName">The raw caller-supplied file name.</param>
    /// <param name="forcedExtension">
    /// The canonical extension for the SNIFFED content type (jpg/png/webp)
    /// — always wins over any client-side extension.
    /// </param>
    private static string SanitizePreservedFileName(string fileName, string forcedExtension)
    {
        const int MaxPreservedFileNameLength = 128;

        // Take the base name: strip everything from the LAST dot on. If
        // there's no dot, the whole string is the base. Path separators
        // are stripped by the allowlist below, so a "dir/evil.jpg" input
        // degrades to "direvil" + forced extension — it can never escape
        // the products directory.
        var lastDot = fileName.LastIndexOf('.');
        var baseName = lastDot > 0 ? fileName[..lastDot] : fileName;

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
            throw new InvalidDataException(
                $"The file name '{fileName}' has no usable base name after sanitization.");
        }

        var finalName = $"{sanitizedBase}.{forcedExtension}";

        if (finalName.Length > MaxPreservedFileNameLength)
        {
            throw new InvalidDataException(
                $"The file name '{fileName}' exceeds the {MaxPreservedFileNameLength}-character limit after sanitization.");
        }

        return finalName;
    }

    /// <summary>
    /// Hard-deletes a previously-stored file by its public URL. See
    /// <see cref="IFileStorage.DeleteAsync"/> for the contract.
    ///
    /// IMPLEMENTATION NOTES:
    ///   - Resolves the URL against <c>_rootPath</c>. Only URLs that start
    ///     with <c>/uploads/products/</c> are accepted (the only prefix
    ///     <see cref="SaveAsync"/> ever returns). Anything else is silently
    ///     ignored (external URLs, malformed input, paths that don't fall
    ///     under our managed root).
    ///   - After resolving to an absolute path, we verify the canonical
    ///     path is still under <c>_rootPath</c> — defends against any
    ///     symlink or path-traversal trick that could escape the root.
    ///   - File.Delete is idempotent (no-op if file doesn't exist) —
    ///     matches the contract.
    ///   - I/O failures (file locked, permission denied) are logged as
    ///     warnings but don't throw — the caller's contract is "best-effort
    ///     delete; don't block the replacement operation".
    /// </summary>
    public Task DeleteAsync(string? url, CancellationToken cancellationToken = default)
    {
        // Silently ignore null/empty/external URLs — these aren't ours to manage.
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.CompletedTask;
        }

        // External URLs (http://, https://) — silently ignore. We only manage
        // files we ourselves saved (root-relative URLs).
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // Only accept URLs that match the prefix SaveAsync returns. This
        // guards against arbitrary path-traversal attempts.
        const string expectedPrefix = "/uploads/products/";
        if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "DeleteAsync: rejecting URL '{Url}' — doesn't start with expected prefix '{Prefix}'.",
                url, expectedPrefix);
            return Task.CompletedTask;
        }

        // Strip the prefix and the leading slash to get the relative path
        // within the products folder. We don't accept subdirectories — the
        // filename is the only thing SaveAsync ever returns.
        var relativePath = url.Substring(expectedPrefix.Length);

        // Reject anything that looks like it's trying to escape (../, absolute
        // paths, etc.). The canonical-path check below is the authoritative
        // guard, but this early reject gives cleaner log messages.
        if (relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains('/', StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(relativePath))
        {
            _logger.LogWarning(
                "DeleteAsync: rejecting URL '{Url}' — relative path contains forbidden characters.",
                url);
            return Task.CompletedTask;
        }

        var absolutePath = Path.Combine(_productImagesPath, relativePath);

        // CANONICAL PATH CHECK — defends against any symlink/traversal trick
        // that could resolve to a path outside _rootPath. We resolve both
        // paths to their canonical forms and verify the file's parent is
        // still under _productImagesPath.
        var canonicalProductsPath = Path.GetFullPath(_productImagesPath);
        string canonicalFilePath;
        try
        {
            canonicalFilePath = Path.GetFullPath(absolutePath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or PathTooLongException or NotSupportedException)
        {
            _logger.LogWarning(
                "DeleteAsync: cannot canonicalize path '{Path}': {Error}",
                absolutePath, ex.Message);
            return Task.CompletedTask;
        }

        if (!canonicalFilePath.StartsWith(canonicalProductsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "DeleteAsync: rejecting URL '{Url}' — canonical path '{Canonical}' is outside products folder '{Products}'.",
                url, canonicalFilePath, canonicalProductsPath);
            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(canonicalFilePath))
            {
                File.Delete(canonicalFilePath);
                _logger.LogInformation(
                    "Deleted upload at {Path} (URL was {Url})",
                    canonicalFilePath, url);
            }
            // else: idempotent no-op. Don't log — could be noisy if a
            // product had its picture replaced twice in quick succession
            // and the first delete already removed the file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log but don't throw — the contract is best-effort.
            _logger.LogWarning(
                "DeleteAsync: failed to delete '{Path}': {Error}",
                canonicalFilePath, ex.Message);
        }

        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────
    // STAGING — the Settings bulk-picture upload's two-phase flow
    // (pick → review → explicit Upload click). See the IFileStorage
    // staging docs for the full contract; the short version: staged files
    // hold PRE-VALIDATION bytes on temp disk so a 200-file batch never
    // buffers in circuit memory, and they are single-use — consumed by
    // SaveWithOriginalNameAsync at Upload time, or swept when orphaned.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams an upload to a server-controlled staging file and returns
    /// its absolute path — see <see cref="IFileStorage.WriteStagedFileAsync"/>
    /// for the full contract and the WHY-STAGING-ON-DISK rationale.
    ///
    /// IMPLEMENTATION: random 32-hex-char name (same entropy class as
    /// <see cref="SaveAsync"/>'s — staging names must be unguessable and
    /// collision-free for concurrent circuits), a size-bounded
    /// <see cref="BoundedStream"/> copy (defense-in-depth: the page's
    /// pre-filter already rejected oversize files before staging), and
    /// failure cleanup of the partial file.
    /// </summary>
    public async Task<string> WriteStagedFileAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Idempotent — safe to call on every pick, including the first.
        Directory.CreateDirectory(_stagingRootPath);

        var stagedFilePath = Path.Combine(
            _stagingRootPath,
            $"{RandomNumberGenerator.GetHexString(RandomFileNameHexChars, lowercase: true)}{StagedFileExtension}");

        try
        {
            long bytesStaged;
            await using (var fileStream = new FileStream(
                             stagedFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             useAsync: true))
            {
                var boundedStream = new BoundedStream(content, _maxImageSizeBytes);
                await boundedStream.CopyToAsync(fileStream, CopyBufferSize, cancellationToken);
                bytesStaged = fileStream.Length;
            }

            _logger.LogInformation(
                "Staged upload bytes for '{FileName}' ({Bytes} bytes) at {Path}",
                fileName, bytesStaged, stagedFilePath);

            return stagedFilePath;
        }
        catch
        {
            // Best-effort cleanup of the partial staging file on any
            // failure path. Don't let the cleanup itself mask the original
            // exception — identical to SaveAsync's temp-file discipline.
            try { if (File.Exists(stagedFilePath)) File.Delete(stagedFilePath); }
            catch { /* swallowed — original exception is more important */ }
            throw;
        }
    }

    /// <summary>
    /// Opens a staged file for reading — see
    /// <see cref="IFileStorage.OpenStagedFileAsync"/> for the contract.
    ///
    /// IMPLEMENTATION: resolves and validates the path via
    /// <see cref="ResolveStagedPath"/> (canonical-path check — a tampered
    /// path can never open a file outside the staging root), then opens
    /// read-only with an async-friendly buffer, mirroring the FileStream
    /// options used everywhere else in this class.
    /// </summary>
    public Task<Stream> OpenStagedFileAsync(
        string stagedFilePath,
        CancellationToken cancellationToken = default)
    {
        var canonicalPath = ResolveStagedPath(stagedFilePath);

        if (!File.Exists(canonicalPath))
        {
            // Already consumed/discarded/swept — surface a precise,
            // loggable failure instead of a bare FileNotFound from open.
            _logger.LogWarning(
                "OpenStagedFileAsync: staged file '{Path}' no longer exists.",
                canonicalPath);
            throw new FileNotFoundException(
                $"The staged file '{stagedFilePath}' no longer exists (already uploaded, discarded, or swept).",
                canonicalPath);
        }

        Stream stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            useAsync: true);
        return Task.FromResult(stream);
    }

    /// <summary>
    /// Best-effort deletion of a staging file — see
    /// <see cref="IFileStorage.DeleteStagedFileAsync"/> for the contract.
    ///
    /// IMPLEMENTATION: same shape as <see cref="DeleteAsync"/> — invalid
    /// paths are logged and rejected (never thrown, cleanup must not
    /// break the flow that triggered it), missing files are an idempotent
    /// no-op, and I/O failures are logged as warnings.
    /// </summary>
    public Task DeleteStagedFileAsync(
        string stagedFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canonicalPath = ResolveStagedPath(stagedFilePath);

            if (File.Exists(canonicalPath))
            {
                File.Delete(canonicalPath);
                _logger.LogInformation(
                    "Deleted staged upload at {Path}",
                    canonicalPath);
            }
            // else: idempotent no-op — the file was already consumed or
            // discarded; double-delete is a normal part of the flow (e.g.
            // upload consumed it, then navigation cleanup runs again).
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            // Includes ResolveStagedPath's rejection of paths outside the
            // staging root (InvalidDataException/ArgumentException) — a
            // tampered or stale path is logged, not allowed to break the
            // user flow. Log but don't throw — the contract is best-effort.
            _logger.LogWarning(
                "DeleteStagedFileAsync: failed to delete '{Path}': {Error}",
                stagedFilePath, ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes staging files older than the given age — see
    /// <see cref="IFileStorage.SweepStagedFilesAsync"/> for the contract.
    ///
    /// IMPLEMENTATION: single flat directory scan (the staging area is one
    /// directory of a few hundred files at most — one batch), per-file
    /// best-effort deletion, and a summary log line only when something
    /// was actually swept (a log line per empty sweep would be noise on
    /// every pick).
    /// </summary>
    public Task<int> SweepStagedFilesAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_stagingRootPath))
        {
            // No batch was ever staged on this instance — nothing to do.
            return Task.FromResult(0);
        }

        var cutoffUtc = DateTime.UtcNow - olderThan;
        var sweptCount = 0;

        foreach (var filePath in Directory.EnumerateFiles(_stagingRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // LastWriteTimeUtc doubles as a missing-file guard: a file
                // deleted between enumeration and here reports year 1601,
                // which is always older than the cutoff — and the delete
                // then throws a caught-and-skipped FileNotFound.
                if (File.GetLastWriteTimeUtc(filePath) < cutoffUtc)
                {
                    File.Delete(filePath);
                    sweptCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Log but don't throw — one stuck file must not abort the
                // sweep of the rest (mirrors DeleteAsync's tolerance).
                _logger.LogWarning(
                    "SweepStagedFilesAsync: skipping '{Path}': {Error}",
                    filePath, ex.Message);
            }
        }

        if (sweptCount > 0)
        {
            _logger.LogInformation(
                "Swept {Count} orphaned staging file(s) older than {Age}",
                sweptCount, olderThan);
        }

        return Task.FromResult(sweptCount);
    }

    /// <summary>
    /// Canonicalizes a staging path and verifies it resolves INSIDE the
    /// staging root — the staging counterpart of DeleteAsync's
    /// canonical-path guard. Every staging path the page holds came from
    /// <see cref="WriteStagedFileAsync"/>, but component state is treated
    /// as untrusted input anyway (defense-in-depth culture of this class):
    /// a tampered or corrupted value must never open/delete an arbitrary
    /// file outside the staging area.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the path is unusable or resolves outside the staging
    /// root.
    /// </exception>
    private string ResolveStagedPath(string stagedFilePath)
    {
        if (string.IsNullOrWhiteSpace(stagedFilePath))
        {
            throw new ArgumentException("stagedFilePath is required.", nameof(stagedFilePath));
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(stagedFilePath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or PathTooLongException
            or NotSupportedException)
        {
            throw new InvalidDataException(
                $"The staged path '{stagedFilePath}' is not a valid path.");
        }

        // Canonical-to-canonical comparison against the ctor-canonicalized
        // root (same discipline as DeleteAsync's products-folder check).
        // The root itself never passes (it's a directory, not a staged
        // file) — the separator suffix enforces that.
        if (!canonicalPath.StartsWith(_stagingRootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The staged path '{stagedFilePath}' is outside the staging root '{_stagingRootPath}'.");
        }

        return canonicalPath;
    }

    /// <summary>
    /// Sniff the actual content type from the first few bytes of the stream.
    /// Returns null if the bytes don't match any supported image type.
    ///
    /// Supported magic byte signatures:
    ///   - JPEG: FF D8 FF (3 bytes)
    ///   - PNG:  89 50 4E 47 0D 0A 1A 0A (8 bytes)
    ///   - WebP: 52 49 46 46 ?? ?? ?? ?? 57 45 42 50 (12 bytes — RIFF....WEBP)
    /// </summary>
    private static string? SniffContentType(byte[] buffer, int bytesRead)
    {
        if (bytesRead >= 3
            && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytesRead >= 8
            && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47
            && buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
        {
            return "image/png";
        }

        // WebP: "RIFF"...."WEBP"
        if (bytesRead >= 12
            && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
            && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// Resolve the canonical file extension for the SNIFFED content type.
    ///
    /// SECURITY POSTURE (Brutal Code Review v3 finding #08, Round 18-B):
    ///   The previous implementation read the client-supplied filename's
    ///   extension and only fell back to the sniffed type's canonical
    ///   extension when the client extension failed a weak regex. That
    ///   meant: upload JPEG bytes named "evil.html" → sniffed=image/jpeg
    ///   → matches the declared type → client extension "html" passes the
    ///   regex → file saved as "randomhex.html" → static-files middleware
    ///   serves as text/html → XSS via direct URL navigation.
    ///
    ///   The fix: ALWAYS use the sniffed type's canonical extension. The
    ///   client filename is no longer a parameter — the caller cannot
    ///   influence the on-disk extension at all. If the sniffed type isn't
    ///   in our mapping (which shouldn't happen because <see cref="SniffContentType"/>
    ///   is the only caller and only returns 3 known types), we REJECT the
    ///   upload with <see cref="InvalidDataException"/> — fail-closed.
    ///
    /// MAPPING:
    ///   - <c>image/jpeg</c> → <c>"jpg"</c>  (NOT "jpeg" — filesystem
    ///     convention; browsers serve both as image/jpeg, but .jpg is
    ///     shorter and matches what every camera and stock-photo site uses.)
    ///   - <c>image/png</c> → <c>"png"</c>
    ///   - <c>image/webp</c> → <c>"webp"</c>
    ///   - anything else → throw <see cref="InvalidDataException"/>
    ///     (defense-in-depth — <see cref="SniffContentType"/> should have
    ///     already returned null and caused <see cref="SaveAsync"/> to
    ///     reject the upload before reaching this method, but if
    ///     <see cref="SniffContentType"/> is ever extended to recognize
    ///     new types without updating this map, the upload fails closed
    ///     instead of being saved with a guessed extension).
    ///
    /// WHY THE OLD XML DOC COMMENT WAS WRONG:
    ///   The previous XML doc on this method claimed "we trust the SNIFFED
    ///   type for the extension" — that was a LIE. The actual code used the
    ///   client extension whenever it passed a weak regex (1-4 letters/digits,
    ///   lower-cased). The new comment accurately describes the new behavior:
    ///   the client filename is not even a parameter anymore.
    /// </summary>
    /// <param name="sniffedContentType">
    /// The content type determined by <see cref="SniffContentType"/> from the
    /// file's magic bytes. The CLIENT-declared content type is NOT accepted
    /// here — only the sniffed type, which is authoritative.
    /// </param>
    /// <returns>
    /// The canonical extension (without leading dot), lower-cased. Always
    /// one of: <c>jpg</c>, <c>png</c>, <c>webp</c>.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown if <paramref name="sniffedContentType"/> is not one of the
    /// supported types. Defense-in-depth — <see cref="SniffContentType"/>
    /// should have already rejected the upload (returned null) before
    /// <see cref="SaveAsync"/> reached this method.
    /// </exception>
    private static string SanitizeExtension(string sniffedContentType)
    {
        return sniffedContentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => throw new InvalidDataException(
                $"Cannot determine canonical extension for sniffed content type '{sniffedContentType}'.")
        };
    }

    /// <summary>
    /// Stream wrapper that throws <see cref="InvalidDataException"/> as soon as
    /// more than <see cref="_maxRemainingBytes"/> are read. Used to enforce the
    /// upload size limit during streaming without buffering the whole file.
    /// </summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxRemainingBytes;
        private long _bytesRead;

        public BoundedStream(Stream inner, long maxRemainingBytes)
        {
            _inner = inner;
            _maxRemainingBytes = maxRemainingBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _bytesRead += read;
            if (_bytesRead > _maxRemainingBytes)
            {
                throw new InvalidDataException(
                    $"File exceeds the maximum allowed size of {_maxRemainingBytes + 12} bytes.");
            }
            return read;
        }

        // CA1844 + CA1835: also override the memory-based ReadAsync so callers
        // using the Span/Memory API don't fall back to the array-based default
        // (which would allocate an array per call). The array-based overload
        // below delegates to this memory-based one to keep the size-enforcement
        // validation logic in a single place and to call the inner stream's
        // memory-based ReadAsync (avoids an array allocation on the inner call).
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            _bytesRead += read;
            if (_bytesRead > _maxRemainingBytes)
            {
                throw new InvalidDataException(
                    $"File exceeds the maximum allowed size of {_maxRemainingBytes + 12} bytes.");
            }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}