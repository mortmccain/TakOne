using TakOne.Domain.Notifications.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Repository abstraction for the <see cref="BroadcastNotification"/> aggregate
/// (the admin's audit-record view of an admin-authored broadcast or the
/// auto-emitted app-update broadcast).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN INTERFACE IN APPLICATION</b>: same rationale as
/// <see cref="INotificationRepository"/> — keeps the Application layer
/// persistence-agnostic (clean architecture / dependency inversion). The EF
/// Core implementation lives in Infrastructure.
/// </para>
/// <para>
/// <b>SCOPE</b>: all read methods are intended for the admin audit page
/// (list past broadcasts, paginated, newest-first). The handler resolves
/// the admin's identity via <c>ICurrentUserService</c> and the command is
/// gated by <c>[RequireRoles(Admin)]</c>, so only admins reach this repo's
/// methods. No per-user filtering here — the aggregate is admin-audit-only.
/// </para>
/// <para>
/// <b>NO PER-USER SCOPE GUARD</b>: unlike <see cref="INotificationRepository"/>,
/// the methods here do NOT filter by user Id. A <c>BroadcastNotification</c>
/// is a system-level audit record, not a user-targeted inbox row — every
/// admin can see every broadcast (including auto-emitted app-update
/// broadcasts and broadcasts authored by other admins). Per-user inbox
/// rows are the <see cref="Notification"/> aggregate's concern.
/// </para>
/// </remarks>
public interface IBroadcastNotificationRepository
{
    /// <summary>
    /// Persists a new broadcast audit row. Called by
    /// <c>SendBroadcastNotificationCommandHandler</c> AFTER resolving
    /// recipients and creating the per-user fanout Notification rows, all
    /// in the same EF Core transaction (Wolverine's AutoApplyTransactions).
    /// </summary>
    Task AddAsync(BroadcastNotification broadcast, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent existing broadcast audit row matching the
    /// given (title, kind) tuple, or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// <b>IDEMPOTENCY DEDUP FOR APP-UPDATE REDISPATCH</b>: the
    /// <c>EmitAppUpdateBroadcastCommandHandler</c> calls this BEFORE
    /// fanning out. Wolverine's durable outbox MAY redeliver an
    /// unacked <c>EmitAppUpdateBroadcastCommand</c> if the process crashes
    /// between the SaveChanges commit and the worker ack. Without this
    /// dedup, a redelivery would create a SECOND audit row + a SECOND set
    /// of per-user fanout rows → every user would see duplicate
    /// "TakOne updated to vX.Y.Z" notifications.
    /// <para>
    /// The title is a safe dedup key for AppUpdate broadcasts because the
    /// hosted service composes it deterministically from
    /// <c>AssemblyInformationalVersion</c> (<c>"TakOne updated to
    /// v{newVersion}"</c>): a redelivered message has the SAME title, while
    /// a legitimately-new broadcast has a DIFFERENT title (different
    /// newVersion → different title).
    /// </para>
    /// <para>
    /// Returns the FULL entity (not just a bool) so the handler can read
    /// the original <c>RecipientCount</c> and return it as the success
    /// value — the caller (the hosted service) doesn't inspect the return
    /// value, but returning the correct count keeps the audit log honest.
    /// </para>
    /// </remarks>
    Task<BroadcastNotification?> GetByTitleAndKindAsync(
        string title,
        TakOne.Domain.Notifications.Enums.NotificationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated slice of past broadcasts, newest-first. Used by
    /// the admin audit page on the new <c>/Admin/Notifications</c> route.
    /// </summary>
    Task<PaginatedResult<BroadcastNotification>> GetPaginatedAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
