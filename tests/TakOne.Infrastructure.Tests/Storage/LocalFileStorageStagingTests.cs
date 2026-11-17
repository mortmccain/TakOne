using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TakOne.Infrastructure.Storage;
using Xunit;

namespace TakOne.Infrastructure.Tests.Storage;

/// <summary>
/// Unit tests for the STAGING half of <see cref="LocalFileStorage"/> —
/// the disk-backed capture area behind the Settings page's bulk-picture
/// upload (write-staged → review → open-staged → SaveWithOriginalNameAsync
/// → delete-staged).
///
/// COVERAGE APPROACH:
///   Pure filesystem tests against per-test temp directories — no DB, no
///   circuit, no InputFile (the Blazor round-trip is explicitly out of
///   scope for bUnit, same as the other Settings-area tests). The staging
///   API is exactly the part of the two-phase flow that CAN be unit
///   tested end-to-end, including the 200-file batch scale requirement.
///
///   Tests cover:
///     • WriteStagedFileAsync: exact byte round-trip, path lands under the
///       staging root, unique server-generated names for every write
///     • WriteStagedFileAsync: oversize stream rejected (InvalidDataException)
///       and the partial staging file CLEANED UP (no .part orphan)
///     • OpenStagedFileAsync: returns the staged content; missing file
///       throws FileNotFoundException; a path OUTSIDE the staging root
///       throws InvalidDataException (canonical-path defense — component
///       state is treated as untrusted)
///     • DeleteStagedFileAsync: deletes + idempotent double-delete;
///       outside-root path is logged and swallowed (cleanup must never
///       break the flow that triggered it)
///     • SweepStagedFilesAsync: deletes ONLY files older than the cutoff,
///       returns the count, tolerates a nonexistent staging root
///     • The full two-phase round trip at the REAL batch scale: 200 files
///       staged → opened → saved under their (sanitized) original names →
///       staging root empty afterwards. This is the "at least 200 files
///       at a time" requirement, proven against the production code path.
///
/// SUT LOCATION:
///   TakOne.Infrastructure/Storage/LocalFileStorage.cs
/// </summary>
public sealed class LocalFileStorageStagingTests : IDisposable
{
    /// <summary>JPEG magic bytes (FF D8 FF) + padding — passes the sniff gate.</summary>
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x7A, 0x7A];

    private readonly string _storageRoot;
    private readonly string _stagingRoot;
    private readonly LocalFileStorage _sut;

    public LocalFileStorageStagingTests()
    {
        // Two INDEPENDENT temp roots per test — staging must never share a
        // directory with durable storage (the whole point of the split).
        _storageRoot = CreateTempDir();
        _stagingRoot = Path.Combine(CreateTempDir(), "staging");

        _sut = new LocalFileStorage(
            BuildConfiguration(_storageRoot, _stagingRoot),
            NullLogger<LocalFileStorage>.Instance);
    }

    public void Dispose()
    {
        // Best-effort cleanup — a locked temp file must never fail a test.
        TryDeleteDirectory(_storageRoot);
        TryDeleteDirectory(Path.GetDirectoryName(_stagingRoot)!);
    }

    // ── WriteStagedFileAsync ───────────────────────────────────────────

    [Fact]
    public async Task WriteStagedFileAsync_WritesExactBytes_UnderStagingRoot()
    {
        // Arrange — arbitrary bytes: staging does NOT sniff (validation
        // happens later, at SaveWithOriginalNameAsync time).
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        // Act
        var stagedPath = await _sut.WriteStagedFileAsync(new MemoryStream(payload), "pic.jpg");

        // Assert — the path is inside the staging root, the file exists,
        // and the bytes round-trip EXACTLY (staging is a faithful capture,
        // not a transformation).
        Path.GetFullPath(stagedPath).StartsWith(Path.GetFullPath(_stagingRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Should().BeTrue("a staged file must live under the staging root");
        File.Exists(stagedPath).Should().BeTrue();

        var stagedBytes = await File.ReadAllBytesAsync(stagedPath);
        stagedBytes.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task WriteStagedFileAsync_TwoWrites_DistinctPaths()
    {
        // Arrange / Act — the same content twice: staging names are
        // server-generated random hex, so two circuits (or two files in
        // one batch) can never collide on disk.
        var path1 = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "a.jpg");
        var path2 = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "a.jpg");

        // Assert
        path1.Should().NotBe(path2, "every staged write gets a fresh random name");
        File.Exists(path1).Should().BeTrue();
        File.Exists(path2).Should().BeTrue();
    }

    [Fact]
    public async Task WriteStagedFileAsync_OversizeStream_ThrowsAndCleansUpPartialFile()
    {
        // Arrange — a stream larger than the default 5 MB cap. The page's
        // pre-filter normally rejects oversize files BEFORE staging; this
        // is the server-side bound catching a browser mis-reporting
        // IBrowserFile.Size.
        var oversize = new MemoryStream(new byte[5 * 1024 * 1024 + 1]);

        // Act
        var act = () => _sut.WriteStagedFileAsync(oversize, "huge.jpg");

        // Assert — rejected, and no partial .part file is left behind on
        // the failure path.
        await act.Should().ThrowAsync<System.IO.InvalidDataException>();
        Directory.EnumerateFiles(_stagingRoot).Should().BeEmpty(
            "the failed staged write must clean up its partial file");
    }

    // ── OpenStagedFileAsync ────────────────────────────────────────────

    [Fact]
    public async Task OpenStagedFileAsync_ReturnsStagedContent()
    {
        // Arrange
        var stagedPath = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "pic.jpg");

        // Act
        await using var stream = await _sut.OpenStagedFileAsync(stagedPath);
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);

        // Assert
        reader.ToArray().Should().BeEquivalentTo(JpegBytes);
    }

    [Fact]
    public async Task OpenStagedFileAsync_MissingFile_ThrowsFileNotFoundException()
    {
        // Arrange — stage then delete (simulates "already consumed").
        var stagedPath = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "pic.jpg");
        await _sut.DeleteStagedFileAsync(stagedPath);

        // Act
        var act = () => _sut.OpenStagedFileAsync(stagedPath);

        // Assert — a precise, loggable failure, not a bare FileNotFound.
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task OpenStagedFileAsync_PathOutsideStagingRoot_ThrowsInvalidDataException()
    {
        // Arrange — a path in the DURABLE storage area, not staging.
        // Component state is treated as untrusted: a tampered or corrupted
        // staged path must never open an arbitrary file on disk.
        var outsidePath = Path.Combine(_storageRoot, "products", "innocent.jpg");

        // Act
        var act = () => _sut.OpenStagedFileAsync(outsidePath);

        // Assert
        await act.Should().ThrowAsync<System.IO.InvalidDataException>(
            "a staged path outside the staging root must be rejected");
    }

    // ── DeleteStagedFileAsync ──────────────────────────────────────────

    [Fact]
    public async Task DeleteStagedFileAsync_DeletesFile_AndSecondDeleteIsNoOp()
    {
        // Arrange
        var stagedPath = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "pic.jpg");

        // Act — delete twice (upload consumed it, then navigation cleanup
        // runs again): the second delete is a normal part of the flow.
        await _sut.DeleteStagedFileAsync(stagedPath);
        await _sut.DeleteStagedFileAsync(stagedPath);

        // Assert
        File.Exists(stagedPath).Should().BeFalse();
        Directory.EnumerateFiles(_stagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteStagedFileAsync_OutsideRootPath_IsLoggedAndSwallowed()
    {
        // Arrange — a nonsense path. Cleanup must NEVER throw: it runs in
        // finally blocks and component disposal, where an exception would
        // break the user flow it was supposed to tidy up after.
        var outsidePath = Path.Combine(_storageRoot, "elsewhere", "not-staged.part");

        // Act
        var act = () => _sut.DeleteStagedFileAsync(outsidePath);

        // Assert
        await act.Should().NotThrowAsync(
            "cleanup is best-effort by contract — invalid paths are logged, not thrown");
    }

    // ── SweepStagedFilesAsync ──────────────────────────────────────────

    [Fact]
    public async Task SweepStagedFilesAsync_DeletesOnlyFilesOlderThanCutoff()
    {
        // Arrange — one orphan (backdated, simulating a circuit that died
        // yesterday) and one file a LIVE admin staged seconds ago.
        var orphanPath = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "orphan.jpg");
        var livePath = await _sut.WriteStagedFileAsync(new MemoryStream(JpegBytes), "live.jpg");
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow - TimeSpan.FromHours(25));

        // Act
        var swept = await _sut.SweepStagedFilesAsync(TimeSpan.FromHours(24));

        // Assert — the orphan goes, the live batch stays: a sweep must
        // never delete a file some live circuit is still about to upload.
        swept.Should().Be(1);
        File.Exists(orphanPath).Should().BeFalse();
        File.Exists(livePath).Should().BeTrue();
    }

    [Fact]
    public async Task SweepStagedFilesAsync_NonexistentRoot_ReturnsZero()
    {
        // Arrange — fresh instance whose staging root was never created.
        var freshStaging = Path.Combine(CreateTempDir(), "never-created");
        var sut = new LocalFileStorage(
            BuildConfiguration(_storageRoot, freshStaging),
            NullLogger<LocalFileStorage>.Instance);

        // Act
        var swept = await sut.SweepStagedFilesAsync(TimeSpan.FromHours(24));

        // Assert
        swept.Should().Be(0, "a sweep of an empty staging area is a quiet zero, not an error");
    }

    // ── The full two-phase round trip at batch scale ───────────────────

    [Fact]
    public async Task StagedBatch_ToDurableStorage_TwoHundredFiles_AllLandUnderOriginalNames()
    {
        // Arrange — 200 files, the exact scale requirement for the bulk
        // restore. Content is valid JPEG bytes with a per-file payload so
        // byte-level integrity can be spot-checked after the round trip.
        const int batchSize = 200;
        var batch = new List<(string FileName, byte[] Content)>(batchSize);
        for (var i = 0; i < batchSize; i++)
        {
            var content = new byte[2_000]; // ~2 KB each — 200 files, fast test, same code path as 5 MB files
            JpegBytes.AsSpan().CopyTo(content);
            content[^1] = (byte)(i % 251); // per-file marker in the last byte
            batch.Add(($"restore-pic-{i:D3}.jpg", content));
        }

        var stagedPaths = new List<string>(batchSize);

        // Act — phase 1: pick (stage every file, mirroring the Settings
        // page's OnPicturesSelectedAsync loop).
        foreach (var (fileName, content) in batch)
        {
            stagedPaths.Add(await _sut.WriteStagedFileAsync(new MemoryStream(content), fileName));
        }

        // Act — phase 2: upload (open each staged file, save under the
        // original name, delete the staged copy — the UploadPendingPicturesAsync loop).
        foreach (var ((fileName, content), stagedPath) in batch.Zip(stagedPaths))
        {
            await using var stream = await _sut.OpenStagedFileAsync(stagedPath);
            var url = await _sut.SaveWithOriginalNameAsync(stream, fileName, "image/jpeg");
            url.Should().Be($"/uploads/products/{fileName}", "name preservation is the restore contract");
            await _sut.DeleteStagedFileAsync(stagedPath);
        }

        // Assert — every file landed in durable storage under its own
        // name, with its own bytes; the staging area is empty afterwards.
        var productsDir = Path.Combine(_storageRoot, "products");
        var savedFiles = Directory.GetFiles(productsDir);
        savedFiles.Should().HaveCount(batchSize);

        for (var i = 0; i < batchSize; i++)
        {
            var saved = await File.ReadAllBytesAsync(Path.Combine(productsDir, $"restore-pic-{i:D3}.jpg"));
            saved.Length.Should().Be(2_000, $"file {i} kept its full size");
            saved[^1].Should().Be((byte)(i % 251), $"file {i} kept its own payload");
        }

        Directory.EnumerateFiles(_stagingRoot).Should().BeEmpty(
            "a consumed batch must leave nothing in staging");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(string storageRoot, string stagingRoot) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = storageRoot,
                ["FileStorage:StagingRootPath"] = stagingRoot,
            })
            .Build();

    private static string CreateTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "takone-staging-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort — temp dirs are OS-cleaned; a locked file must
            // never fail an unrelated test.
        }
    }
}
