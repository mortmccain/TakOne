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
    /// Persists a binary upload UNDER ITS (SANITIZED) ORIGINAL FILE NAME and
    /// returns the public URL — the name-preserving counterpart of
    /// <see cref="SaveAsync"/>, used by the Settings page's bulk picture
    /// upload.
    ///
    /// WHY PRESERVE THE NAME (when SaveAsync deliberately randomizes it):
    /// the product CSV export records each product's picture FILE NAME, and
    /// the product import re-links products to pictures by that name. A
    /// restore therefore needs the files to land on disk under EXACTLY the
    /// names the CSV references — randomizing would break every link. For
    /// ordinary single-product uploads (CreateProduct / picture replace)
    /// the randomized <see cref="SaveAsync"/> remains the right choice.
    ///
    /// SAFETY (same defense-in-depth as SaveAsync, adapted to fixed names):
    ///   - <b>Content sniffing</b> — the magic bytes decide the TRUE
    ///     content type and the declared type must agree; unknown types are
    ///     rejected.
    ///   - <b>Extension forcing</b> — the on-disk EXTENSION is always the
    ///     sniffed type's canonical extension (jpg/png/webp), never the
    ///     client-supplied one. Only the BASE name is preserved.
    ///   - <b>Path-traversal-proof sanitization</b> — the name is stripped
    ///     to <c>[A-Za-z0-9._-]</c> by the CANONICAL shared rule
    ///     (<see cref="PreservedFileNames"/>); path separators, Unicode
    ///     tricks, and control characters are removed, so the final path
    ///     can never escape the products directory. A name that sanitizes
    ///     to nothing (or nothing but its extension) is rejected. The SAME
    ///     rule normalizes picture file names on the products-CSV IMPORT
    ///     side — keep the two in lockstep by always going through that
    ///     class (never re-implement the allowlist locally).
    ///   - <b>Length cap</b> — the sanitized name (including extension) is
    ///     capped; longer names are rejected (not truncated — a silent
    ///     truncation could collide two different files).
    ///   - <b>Size limit</b> — same configurable max as <see cref="SaveAsync"/>.
    ///   - <b>Atomic write</b> — temp file + rename (overwrite: true), so a
    ///     crash never leaves a half-written file under the original name.
    ///
    /// OVERWRITE SEMANTICS: if a file with the same (sanitized) name
    /// already exists it is REPLACED — re-running a bulk restore is
    /// idempotent, and the admin performing the restore wants the uploaded
    /// bytes to win. (With SaveAsync-randomized 128-bit names a genuine
    /// collision is practically impossible; a same-name upload is virtually
    /// always the same file coming back.)
    /// </summary>
    /// <param name="content">
    /// The binary content to store. The implementation does NOT take
    /// ownership of the stream's lifetime — caller disposes it.
    /// </param>
    /// <param name="fileName">
    /// The file name to preserve (e.g. <c>ab12cd34.jpg</c> — the name the
    /// product CSV export recorded, or any caller-chosen name). Sanitized
    /// as described above; never used verbatim on disk.
    /// </param>
    /// <param name="contentType">
    /// The MIME type as declared by the client (e.g. <c>image/jpeg</c>).
    /// Advisory only — the implementation verifies the actual content via
    /// magic-byte sniffing and rejects mismatches.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token. Cancellation aborts the write and
    /// cleans up any temp file.
    /// </param>
    /// <returns>
    /// The public URL the file can be retrieved from — always
    /// <c>/uploads/products/{sanitizedFileName}</c> in the current
    /// implementation.
    /// </returns>
    /// <exception cref="System.IO.InvalidDataException">
    /// Thrown when the content type is unrecognized, the declared type
    /// doesn't match the sniffed type, the sanitized name is empty/over
    /// the length cap, or the content exceeds the configured maximum size.
    /// </exception>
    Task<string> SaveWithOriginalNameAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams an upload to a SERVER-CONTROLLED STAGING FILE on disk and
    /// returns its absolute path — the first half of the Settings page's
    /// two-phase bulk-picture upload (pick → review → explicit Upload
    /// click).
    ///
    /// WHY STAGING EXISTS (instead of buffering the batch in memory):
    /// An <c>IBrowserFile</c> stream is only readable DURING the Blazor
    /// <c>InputFile</c> OnChange handler — the browser-side references die
    /// with the JS registry once the handler returns, so the bytes must be
    /// captured at pick time, before the admin clicks Upload. Capturing a
    /// 200-file batch as <c>byte[]</c> in circuit memory would spike the
    /// Blazor Server process by up to 1 GB (200 × 5 MB worst case; the app
    /// container runs with a 3 GB limit), so the capture target is a
    /// staging FILE: peak memory stays at the copy-buffer size regardless
    /// of batch size or file size.
    ///
    /// WHAT STAGING IS *NOT*:
    ///   - <b>Not durable storage</b> — a staged file is single-use input
    ///     for <see cref="SaveWithOriginalNameAsync"/> and is deleted once
    ///     consumed (or discarded by the caller).
    ///   - <b>Never web-reachable</b> — the staging root lives under the
    ///     OS temp directory, NOT under the (potentially wwwroot-backed)
    ///     <c>FileStorage:RootPath</c>, because staged bytes are
    ///     PRE-VALIDATION: the magic-byte sniff happens later, at
    ///     <see cref="SaveWithOriginalNameAsync"/> time. Staged content
    ///     must never be servable by any URL.
    ///   - <b>Not client-named</b> — the on-disk name is a
    ///     cryptographically random hex string; the caller's file name is
    ///     kept for logging only. The name the admin sees in the review
    ///     chips lives in the page's state, not on disk.
    ///
    /// SAFETY:
    ///   - <b>Size bound</b> — the write enforces the configured
    ///     <c>FileStorage:MaxImageSizeBytes</c> limit while streaming
    ///     (defense-in-depth on top of the page's client-side pre-filter,
    ///     which rejects oversize files before staging them). An over-bound
    ///     stream throws and the partial staging file is deleted.
    ///   - <b>Failure cleanup</b> — any write failure deletes the partial
    ///     staging file; no orphan is left behind on the error path.
    ///
    /// ORPHAN RECLAMATION:
    ///   A circuit that dies mid-batch (browser closed, server restart)
    ///     leaves staged files behind. The caller sweeps them
    ///     opportunistically via <see cref="SweepStagedFilesAsync"/>;
    ///     implementations must therefore tolerate deletion of files that no
    ///     live caller remembers.
    /// </summary>
    /// <param name="content">
    /// The binary content to stage. The implementation does NOT take
    /// ownership of the stream's lifetime — caller disposes it.
    /// </param>
    /// <param name="fileName">
    /// The caller's file name, used ONLY for log correlation — never for
    /// the on-disk name, which is server-generated.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token. Cancellation aborts the write and
    /// cleans up the partial staging file.
    /// </param>
    /// <returns>
    /// The ABSOLUTE path of the staging file. Opaque to the caller —
    /// pass it back verbatim to <see cref="OpenStagedFileAsync"/> /
    /// <see cref="DeleteStagedFileAsync"/>.
    /// </returns>
    /// <exception cref="System.IO.InvalidDataException">
    /// Thrown when the content exceeds the configured maximum size.
    /// </exception>
    Task<string> WriteStagedFileAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a previously-staged file for reading — the second half of the
    /// two-phase bulk upload (the Upload click streams staged file →
    /// durable storage via <see cref="SaveWithOriginalNameAsync"/>).
    ///
    /// The returned stream is owned by the CALLER: dispose it when done.
    /// Opening does not consume or lock the staged file against
    /// <see cref="DeleteStagedFileAsync"/> — the two-phase flow deletes
    /// each staged file only after its upload attempt finished.
    /// </summary>
    /// <param name="stagedFilePath">
    /// The path returned by <see cref="WriteStagedFileAsync"/>. Treated as
    /// untrusted: implementations MUST verify (via canonical-path
    /// resolution) that it resolves inside the staging root, so a tampered
    /// value can never open an arbitrary file on disk.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token.
    /// </param>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when the staged file no longer exists (already consumed,
    /// discarded, or swept).
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// Thrown when the path does not resolve inside the staging root.
    /// </exception>
    Task<Stream> OpenStagedFileAsync(
        string stagedFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort deletion of a staging file — called by the page when a
    /// staged file is consumed (uploaded, successfully or not), discarded
    /// (Clear button / per-file chip remove / re-pick), or the component
    /// is torn down (navigation away).
    ///
    /// ERROR SEMANTICS (mirrors <see cref="DeleteAsync"/>): I/O failures
    /// and invalid paths are logged and swallowed — cleanup must never
    /// break the user flow that triggered it. A missing file is a no-op
    /// (idempotent).
    /// </summary>
    /// <param name="stagedFilePath">
    /// The path returned by <see cref="WriteStagedFileAsync"/>. Paths that
    /// do not resolve inside the staging root are rejected (logged, not
    /// thrown).
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token.
    /// </param>
    Task DeleteStagedFileAsync(
        string stagedFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes staging files older than <paramref name="olderThan"/> and
    /// returns how many were deleted — the orphan-reclamation half of the
    /// staging contract. A circuit that dies mid-batch (browser closed,
    /// connection lost, app restart) leaves its staged files behind with
    /// nobody remembering them; the page triggers this sweep
    /// opportunistically when a NEW batch is picked, so the temp disk
    /// never accumulates dead batches.
    ///
    /// The age threshold must comfortably exceed any live circuit's batch
    /// lifetime (the caller uses 24 h) — a sweep must never delete a file
    /// some live circuit is still about to upload.
    ///
    /// ERROR SEMANTICS: per-file failures are logged and skipped; the
    /// sweep itself never throws.
    /// </summary>
    /// <param name="olderThan">
    /// Minimum age (by last-write time) for a staging file to be deleted.
    /// </param>
    /// <param name="cancellationToken">
    /// Standard cancellation token.
    /// </param>
    Task<int> SweepStagedFilesAsync(
        TimeSpan olderThan,
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