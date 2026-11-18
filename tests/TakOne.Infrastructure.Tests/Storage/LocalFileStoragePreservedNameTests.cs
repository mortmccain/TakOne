using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TakOne.Application.Common.Interfaces;
using TakOne.Infrastructure.Storage;
using Xunit;

namespace TakOne.Infrastructure.Tests.Storage;

/// <summary>
/// Unit tests pinning the ON-DISK NAMING half of the name-preserving
/// upload (<see cref="LocalFileStorage.SaveWithOriginalNameAsync"/>) —
/// the counterpart of the import-side pins in
/// <c>ImportProductsCommandHandlerTests</c>.
///
/// WHY THESE PINS MATTER: the products CSV import links each product's
/// picture URL by NORMALIZING the CSV's file-name cell through the SAME
/// canonical rule (<see cref="PreservedFileNames"/>) this storage applies
/// when naming the file on disk. The production incident these tests
/// prevent: photos named <c>"Pizza flour 500g.png"</c> were stored as
/// <c>Pizzaflour500g.png</c> while the import linked the verbatim
/// spaced name — a URL that can never resolve, so every such product
/// rendered without its picture.
///
/// COVERAGE APPROACH: pure filesystem tests against per-test temp
/// directories with REAL magic-byte payloads (the sniffing gate runs for
/// real — it decides the on-disk extension). No DB, no circuit.
///
/// SUT LOCATION:
///   TakOne.Infrastructure/Storage/LocalFileStorage.cs (naming logic now
///   delegated to TakOne.Application/Common/Interfaces/PreservedFileNames.cs)
/// </summary>
public sealed class LocalFileStoragePreservedNameTests : IDisposable
{
    /// <summary>PNG magic bytes (89 50 4E 47 0D 0A 1A 0A) + padding — passes the sniff gate.</summary>
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x7A, 0x7A];

    /// <summary>JPEG magic bytes (FF D8 FF) + padding — passes the sniff gate.</summary>
    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x7A, 0x7A];

    private readonly string _storageRoot;
    private readonly LocalFileStorage _sut;

    public LocalFileStoragePreservedNameTests()
    {
        _storageRoot = Path.Combine(
            Path.GetTempPath(),
            "takone-preserved-name-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);

        _sut = new LocalFileStorage(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = _storageRoot,
                })
                .Build(),
            NullLogger<LocalFileStorage>.Instance);
    }

    public void Dispose()
    {
        // Best-effort cleanup — a locked temp file must never fail a test.
        try { Directory.Delete(_storageRoot, recursive: true); }
        catch { /* swallowed */ }
    }

    // ── The on-disk names the import must be able to predict ───────────

    [Fact]
    public async Task SaveWithOriginalNameAsync_SpacedName_IsStoredAllowlistStripped()
    {
        // THE incident shape: "Pizza flour 500g.png" (genuine PNG) lands
        // on disk as Pizzaflour500g.png — the import links exactly this.
        var url = await _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), "Pizza flour 500g.png", "image/png");

        url.Should().Be("/uploads/products/Pizzaflour500g.png");
        File.Exists(Path.Combine(_storageRoot, "products", "Pizzaflour500g.png"))
            .Should().BeTrue("the returned URL and the on-disk name must agree");
    }

    [Fact]
    public async Task SaveWithOriginalNameAsync_SpecialCharacters_AreStrippedFromDiskName()
    {
        // A real name from the production CSV: double space + ampersand.
        var url = await _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), "calcium  & Vitamins.png", "image/png");

        url.Should().Be("/uploads/products/calciumVitamins.png");
        File.Exists(Path.Combine(_storageRoot, "products", "calciumVitamins.png"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task SaveWithOriginalNameAsync_AlreadyCanonicalName_PassesThroughUnchanged()
    {
        // "ABC.png" is allowlist-clean — the one product that displayed
        // during the incident. Its storage name must stay exactly as picked.
        var url = await _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), "ABC.png", "image/png");

        url.Should().Be("/uploads/products/ABC.png");
    }

    [Fact]
    public async Task SaveWithOriginalNameAsync_JpegWithAliasExtension_ForcesSniffedCanonicalExtension()
    {
        // "My photo.jpeg" contains JPEG bytes: the on-disk EXTENSION is
        // forced from the sniffed type (jpg), never the client's alias —
        // and the import's canonicalization produces the same name.
        var url = await _sut.SaveWithOriginalNameAsync(
            new MemoryStream(JpegBytes), "My photo.jpeg", "image/jpeg");

        url.Should().Be("/uploads/products/Myphoto.jpg");
        File.Exists(Path.Combine(_storageRoot, "products", "Myphoto.jpg"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task SaveWithOriginalNameAsync_ReUploadOfSameName_ReplacesFile()
    {
        // Overwrite semantics (bulk-restore idempotency): the same
        // sanitized name may be uploaded twice; the new bytes win.
        var payload1 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01 };
        var payload2 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x02 };

        await _sut.SaveWithOriginalNameAsync(new MemoryStream(payload1), "ABC.png", "image/png");
        await _sut.SaveWithOriginalNameAsync(new MemoryStream(payload2), "ABC.png", "image/png");

        var onDisk = await File.ReadAllBytesAsync(
            Path.Combine(_storageRoot, "products", "ABC.png"));
        onDisk.Should().BeEquivalentTo(payload2, "a re-uploaded file replaces its own earlier copy");
    }

    // ── Rejection messages stay byte-identical (admin-facing ledger) ────

    [Fact]
    public async Task SaveWithOriginalNameAsync_NoAllowlistCharacters_RejectedWithClearMessage()
    {
        var act = () => _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), "(((.png", "image/png");

        await act.Should().ThrowAsync<System.IO.InvalidDataException>()
            .WithMessage("*no usable base name after sanitization*");
    }

    [Fact]
    public async Task SaveWithOriginalNameAsync_OverLengthCap_RejectedWithClearMessage()
    {
        var rawName = new string('a', 125) + ".png"; // 129 chars total

        var act = () => _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), rawName, "image/png");

        await act.Should().ThrowAsync<System.IO.InvalidDataException>()
            .WithMessage("*exceeds the 128-character limit after sanitization*");
    }

    // ── The cross-layer symmetry pin ────────────────────────────────────

    [Fact]
    public async Task SaveWithOriginalNameAsync_ReturnedName_MatchesImportNormalization()
    {
        // The contract that repairs the catalog: for any uploadable file,
        // the URL the UPLOAD returns must be identical to the URL the
        // IMPORT builds from the same CSV cell. Pinned here with the
        // storage running for real (sniff + sanitize + rename) and the
        // import's normalizer applied to the same raw name.
        const string rawName = "700 g 1.2 mm.png";

        var uploadUrl = await _sut.SaveWithOriginalNameAsync(
            new MemoryStream(PngBytes), rawName, "image/png");

        var importNormalized = PreservedFileNames.TryNormalizeClientSuppliedName(
            rawName, out var normalizedName);

        importNormalized.Should().BeTrue();
        uploadUrl.Should().Be($"/uploads/products/{normalizedName}",
            "the import must link exactly the URL the upload returned");
    }
}
