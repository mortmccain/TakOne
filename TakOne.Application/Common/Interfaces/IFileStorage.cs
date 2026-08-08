namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Abstracts the storage of binary file uploads (product images, etc.) so the
/// Application layer never touches <c>System.IO</c> directly.
///
/// DESIGN RATIONALE
/// =================
/// TakOne is deployed to a single server. The current (and only) implementation
/// is <c>LocalFileStorage</c> in <c>TakOne.Infrastructure</c>, which writes
/// files to a configurable directory on disk. The interface still exists for
/// three reasons:
///
/// 1. <b>Clean Architecture hygiene</b> — the Application layer must not depend
///    on <c>System.IO</c> or any concrete storage technology. By depending on
///    <see cref="IFileStorage"/>, command handlers stay pure and testable.
///
/// 2. <b>Testability</b> — unit tests for handlers that consume uploaded files
///    can inject a mock <see cref="IFileStorage"/> that returns canned URLs
///    without touching the filesystem.
///
/// 3. <b>Single-implementation future-proofing</b> — if TakOne ever moves to
///    a multi-server setup or a CDN-backed object store, only the DI
///    registration in <c>ServiceCollectionExtensions</c> needs to change;
///    every command handler and endpoint stays untouched.
///
/// LIFETIME
/// =========
/// Registered as <c>Scoped</c> in DI. <c>LocalFileStorage</c> is stateless
/// beyond its config, so it could be <c>Singleton</c>, but <c>Scoped</c> is
/// the safer default — a future implementation might need per-request state
/// (e.g. a tenant-scoped storage root).
///
/// STREAMING
/// ==========
/// Every method takes a <see cref="Stream"/> for content and never a
/// <c>byte[]</c>. This forces callers to stream bytes from end to end
/// (HTTP request body → storage) without ever loading the full file into RAM.
/// For a 5 MB product image, this keeps peak memory pressure at the chunk
/// size (~84 KB) rather than 5 MB.
///
/// ERROR SEMANTICS
/// ================
/// Implementations should throw <c>IOException</c> for I/O failures. They
/// should NOT return null or empty strings on failure — that would let a
/// caller mistake a failed write for a successful one. The caller's
/// responsibility is to wrap calls in try/catch and surface a localized error.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Persists a binary upload to durable storage and returns the public URL
    /// the file can later be retrieved from.
    ///
    /// The implementation is responsible for:
    ///   - Generating a unique filename (caller-supplied <paramref name="suggestedFileName"/>
    ///     is treated as a hint only — never trusted verbatim).
    ///   - Writing atomically (write-to-temp-then-rename, so a crash mid-write
    ///     never leaves a partial file visible to readers).
    ///   - Validating content type against an allowlist (callers can't be
    ///     trusted to send honest <c>Content-Type</c> headers — implementations
    ///     should sniff magic bytes if possible).
    ///   - Enforcing max size (defense-in-depth on top of any HTTP-level limit).
    /// </summary>
    /// <param name="content">
    /// The binary content to store. Must be readable. The implementation does
    /// NOT take ownership of the stream's lifetime — caller disposes it.
    /// </param>
    /// <param name="suggestedFileName">
    /// The original filename as sent by the client (e.g. <c>"photo.jpg"</c>).
    /// Used only to extract the file extension. The implementation MUST NOT
    /// use this string verbatim — it's user input and could contain path
    /// traversal attempts (<c>../../etc/passwd</c>), weird Unicode, or
    /// collisions with existing files.
    /// </param>
    /// <param name="contentType">
    /// The MIME type as declared by the client (e.g. <c>image/jpeg</c>).
    /// Implementations should treat this as advisory only and verify the
    /// actual content via magic-byte sniffing.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token. Cancellation should abort the write and
    /// clean up any temp file.
    /// </param>
    /// <returns>
    /// The public URL the file can be retrieved from (e.g.
    /// <c>"/uploads/products/abc123.jpg"</c>). Suitable for storing in a
    /// <c>Product.PictureUrl</c> column. Always starts with <c>"/"</c> (root-
    /// relative), so it works regardless of the host or scheme.
    /// </returns>
    Task<string> SaveAsync(
        Stream content,
        string suggestedFileName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a previously-stored file by its public URL.
    ///
    /// Used when a product picture is being REPLACED — the old file at
    /// <c>product.PictureUrl</c> is deleted before (or after) the new file
    /// is saved, so the upload directory doesn't accumulate orphaned image
    /// files over time.
    ///
    /// SAFETY:
    ///   - Idempotent: returns silently if the file doesn't exist (the
    ///     product may have had its picture deleted in a prior operation,
    ///     or the URL may point to an external host we don't manage).
    ///   - Path-traversal safe: implementations MUST reject any URL that
    ///     would resolve to a path outside the configured storage root.
    ///     Only root-relative URLs returned by <see cref="SaveAsync"/>
    ///     (e.g. <c>/uploads/products/abc.jpg</c>) are accepted.
    ///   - External URLs (http/https) are silently ignored — we don't
    ///     manage files on third-party CDNs.
    ///
    /// ERROR SEMANTICS:
    ///   Implementations should log a warning but NOT throw on I/O
    ///   failures (file locked, permission denied, etc.). The caller's
    ///   interest is "the old file is gone if possible" — a failed delete
    ///   shouldn't block the replacement operation.
    /// </summary>
    /// <param name="url">
    /// The public URL previously returned by <see cref="SaveAsync"/>.
    /// External URLs (http/https) are silently ignored. Null or empty
    /// values are silently ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token.
    /// </param>
    Task DeleteAsync(string? url, CancellationToken cancellationToken = default);
}