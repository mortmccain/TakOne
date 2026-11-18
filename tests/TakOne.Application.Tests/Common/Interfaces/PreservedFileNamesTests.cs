using FluentAssertions;
using TakOne.Application.Common.Interfaces;
using Xunit;

namespace TakOne.Application.Tests.Common.Interfaces;

/// <summary>
/// Unit tests for <see cref="PreservedFileNames"/> — the single canonical
/// rule shared by the bulk picture UPLOAD
/// (<c>LocalFileStorage.SaveWithOriginalNameAsync</c>) and the products
/// CSV IMPORT (<c>ImportProductsCommandHandler</c>).
///
/// WHY THESE PINS MATTER: the two sides used to implement the rule
/// independently — the upload stripped disallowed characters (spaces,
/// <c>&amp;</c>, …) while the import linked the CSV name verbatim, so
/// every product whose photo name contained a space rendered without its
/// picture (the on-disk file was <c>Pizzaflour500g.png</c>; the linked URL
/// was <c>/uploads/products/Pizza flour 500g.png</c>). These tests pin the
/// rule on BOTH entry points plus their SYMMETRY: for any uploadable
/// name, the import's normalized name must equal the upload's on-disk
/// name.
/// </summary>
public class PreservedFileNamesTests
{
    // ── TryBuildPreservedName (the storage/upload entry point) ─────────

    [Theory]
    [InlineData("ABC.png", "png", "ABC.png")]
    [InlineData("ab12cd34ef567890ab12cd34ef567890.jpg", "jpg", "ab12cd34ef567890ab12cd34ef567890.jpg")]
    [InlineData("Pizza flour 500g.png", "png", "Pizzaflour500g.png")]
    [InlineData("calcium  & Vitamins.png", "png", "calciumVitamins.png")]
    [InlineData("1.2 mm.png", "png", "1.2mm.png")]
    [InlineData("700 g 1.2 mm.png", "png", "700g1.2mm.png")]
    [InlineData("My Photo (1)!.jpg", "jpg", "MyPhoto1.jpg")]
    [InlineData("dir/evil.jpg", "jpg", "direvil.jpg")]
    [InlineData("..leading-and-trailing..", "png", "leading-and-trailing.png")]
    [InlineData("no-extension", "jpg", "no-extension.jpg")]
    [InlineData("kebab_case-name.v2", "png", "kebab_case-name.png")] // client ext (.v2) DISCARDED — forced ext wins
    public void TryBuildPreservedName_AllowlistPass_ProducesExpectedName(
        string rawFileName, string forcedExtension, string expected)
    {
        var outcome = PreservedFileNames.TryBuildPreservedName(
            rawFileName, forcedExtension, out var preservedName);

        outcome.Should().Be(PreservedNameBuildResult.Built);
        preservedName.Should().Be(expected);
    }

    [Fact]
    public void TryBuildPreservedName_FullyNonAsciiBase_IsEmptyBaseRejection()
    {
        // A Persian-only base has zero allowlist characters — nothing to
        // store under (such a file can never be uploaded, so the import
        // side must also treat it as "no picture").
        var outcome = PreservedFileNames.TryBuildPreservedName(
            "تصویر محصول.png", "png", out var preservedName);

        outcome.Should().Be(PreservedNameBuildResult.EmptyBase);
        preservedName.Should().BeNull();
    }

    [Fact]
    public void TryBuildPreservedName_OnlyPunctuationBase_IsEmptyBaseRejection()
    {
        var outcome = PreservedFileNames.TryBuildPreservedName(
            "(((.png", "png", out var preservedName);

        outcome.Should().Be(PreservedNameBuildResult.EmptyBase);
        preservedName.Should().BeNull();
    }

    [Fact]
    public void TryBuildPreservedName_ExactlyAtCap_IsBuilt()
    {
        // 124-char base + "." + "png" = exactly 128 chars.
        var rawName = new string('a', 124);

        var outcome = PreservedFileNames.TryBuildPreservedName(
            rawName, "png", out var preservedName);

        outcome.Should().Be(PreservedNameBuildResult.Built);
        preservedName.Should().Be($"{rawName}.png");
        preservedName!.Length.Should().Be(PreservedFileNames.MaxPreservedFileNameLength);
    }

    [Fact]
    public void TryBuildPreservedName_OneOverCap_IsTooLongRejection()
    {
        // 125-char base + "." + "png" = 129 chars — over the cap. Rejected,
        // never truncated (a silent truncation could collide two files).
        var rawName = new string('a', 125);

        var outcome = PreservedFileNames.TryBuildPreservedName(
            rawName, "png", out var preservedName);

        outcome.Should().Be(PreservedNameBuildResult.TooLong);
        preservedName.Should().BeNull();
    }

    // ── TryNormalizeClientSuppliedName (the import entry point) ────────

    [Theory]
    [InlineData("ABC.png", "ABC.png")]                       // already canonical — unchanged
    [InlineData("Pizza flour 500g.png", "Pizzaflour500g.png")] // THE incident shape
    [InlineData("calcium  & Vitamins.png", "calciumVitamins.png")]
    [InlineData("700 g 1.2 mm.png", "700g1.2mm.png")]
    [InlineData("My photo.jpeg", "Myphoto.jpg")]             // jpeg alias canonicalizes like the sniffer's map
    [InlineData("Photo.JPG", "Photo.jpg")]                   // case-insensitive extension, base case preserved
    [InlineData("Organic Whole Wheat Spaghetti.webp", "OrganicWholeWheatSpaghetti.webp")]
    [InlineData("photo", "photo")]                           // no extension — base only, best-effort
    [InlineData("photo.", "photo")]                          // trailing dot — no usable extension
    [InlineData("photo.gif", "photo.gif")]                   // never uploadable, but URL-safe passthrough
    [InlineData("kebab_case-name.v2", "kebab_case-name.v2")] // non-image client ext passes through (see remark)
    [InlineData("  Spaced Name.png  ", "SpacedName.png")]    // surrounding whitespace trimmed
    public void TryNormalizeClientSuppliedName_NormalizesLikeStorage(
        string rawFileName, string expected)
    {
        var normalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            rawFileName, out var normalizedName);

        normalized.Should().BeTrue();
        normalizedName.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(((.png")]
    [InlineData("تصویر.png")]
    public void TryNormalizeClientSuppliedName_UnusableName_ReturnsFalse(
        string? rawFileName)
    {
        var normalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            rawFileName, out var normalizedName);

        normalized.Should().BeFalse("an empty cell or a name with no allowlist characters links no picture");
        normalizedName.Should().BeNull();
    }

    [Fact]
    public void TryNormalizeClientSuppliedName_OverCapName_ReturnsFalse()
    {
        var rawName = new string('b', 125) + ".png"; // 129 chars total

        var normalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            rawName, out var normalizedName);

        normalized.Should().BeFalse("an over-cap name could never have been uploaded, so there is no file to link");
        normalizedName.Should().BeNull();
    }

    // ── The symmetry pin (upload on-disk name == import URL name) ──────

    [Fact]
    public void ImportNormalization_MatchesUploadNaming_ForSpacedSpecialCharacterNames()
    {
        // The exact scenario from the production catalog: a photo picked as
        // "Pizza flour 500g.png" (genuine PNG bytes) is stored by the
        // upload under the allowlist-stripped base + sniffed extension;
        // the import's normalization of the same CSV cell must produce the
        // IDENTICAL name — otherwise the linked URL can never resolve.
        const string csvCellName = "Pizza flour 500g.png";

        var uploadOutcome = PreservedFileNames.TryBuildPreservedName(
            csvCellName, "png", out var uploadDiskName);

        var importNormalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            csvCellName, out var importName);

        uploadOutcome.Should().Be(PreservedNameBuildResult.Built);
        importNormalized.Should().BeTrue();
        importName.Should().Be(uploadDiskName,
            "the import must link exactly the name the upload stored on disk");
    }

    [Fact]
    public void ImportNormalization_ForNonImageClientExtension_IsBestEffortPassthrough()
    {
        // A name like "kebab_case-name.v2" (image BYTES, non-image client
        // extension) can never complete the round trip: the upload would
        // have stored it as "kebab_case-name.png" (sniffed extension), and
        // the import cannot observe the sniffed type. This is UNREACHABLE
        // in practice — the upload's declared-vs-sniffed content-type
        // check rejects any file whose extension doesn't type it as
        // jpg/png/webp — so the normalizer simply passes the sanitized
        // name through (best-effort, like the pre-fix behavior for exotic
        // names). Pinned here so the limitation is a documented decision,
        // not an accident.
        var normalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            "kebab_case-name.v2", out var normalizedName);

        normalized.Should().BeTrue();
        normalizedName.Should().Be("kebab_case-name.v2");
    }
}
