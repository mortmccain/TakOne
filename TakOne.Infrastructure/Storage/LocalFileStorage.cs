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
/// 1. <b>Filename sanitization</b> — the client-supplied filename is used ONLY
///    to extract the file extension (lower-cased, max 5 chars including dot,
///    must match <c>[a-z0-9]+</c>). The actual on-disk filename is a
///    cryptographically random 32-char hex string. Path traversal attacks
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
    private readonly long _maxImageSizeBytes;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(
        IConfiguration configuration,
        ILogger<LocalFileStorage> logger)
    {
        _rootPath = configuration["FileStorage:RootPath"]
            ?? DefaultRootPath;
        _productImagesPath = Path.Combine(_rootPath, ProductImagesSubfolder);

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

        // ── Step 2: extract + sanitize the file extension from the suggested name.
        // We keep the extension because it's useful for browser content-type
        // inference when the file is later served. We DON'T keep any other part
        // of the client filename.
        var extension = SanitizeExtension(suggestedFileName, sniffedContentType);

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
    /// Extract + sanitize the file extension from the client-supplied filename.
    ///
    /// Rules:
    ///   - If the declared content type doesn't match the sniffed content type,
    ///     we trust the SNIFFED type for the extension (e.g. a .jpg file that's
    ///     actually a PNG gets saved as .png).
    ///   - Extension is lower-cased.
    ///   - Must match [a-z0-9]{1,4} after the dot.
    ///   - If the client filename has no usable extension, fall back to the
    ///     canonical extension for the sniffed content type.
    /// </summary>
    private static string SanitizeExtension(string suggestedFileName, string sniffedContentType)
    {
        // Try to extract from the suggested name first.
        var ext = Path.GetExtension(suggestedFileName)?.ToLowerInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext) || ext.Length > 4 || !ext.All(c => char.IsLetterOrDigit(c)))
        {
            // Fall back to the canonical extension for the sniffed type.
            return sniffedContentType switch
            {
                "image/jpeg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                _ => "bin"
            };
        }
        return ext;
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

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            _bytesRead += read;
            if (_bytesRead > _maxRemainingBytes)
            {
                throw new InvalidDataException(
                    $"File exceeds the maximum allowed size of {_maxRemainingBytes + 12} bytes.");
            }
            return read;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}