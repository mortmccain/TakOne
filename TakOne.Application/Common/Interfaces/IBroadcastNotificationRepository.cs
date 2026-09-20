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
    /// "TakOne updated" notifications.
    /// <para>
    /// <b>TIME-WINDOWED DEDUP (the <paramref name="sentAfterUtc"/>
    /// parameter)</b>: when non-null, only broadcasts sent STRICTLY AFTER
    /// this UTC timestamp are considered for dedup. This is essential for
    /// the app-update flow because the user-facing title is now a constant
    /// "TakOne updated" (no version embedded — per the launch directive
    /// to not show commit/IDs). Without a time window, a NEW deploy's
    /// broadcast would incorrectly dedup against an OLD broadcast with
    /// the same title and skip the fanout — users wouldn't be notified
    /// of subsequent updates. The app-update handler passes
    /// <c>DateTime.UtcNow.AddMinutes(-5)</c>: Wolverine redelivery happens
    /// within seconds, so 5 minutes is generous headroom; a real new
    /// deploy hours/days later finds no recent broadcast and fans out
    /// correctly.
    /// </para>
    /// <para>
    /// <b>ADMIN-AUTHORED BROADCASTS</b>: the
    /// <c>SendBroadcastNotificationCommandHandler</c> calls this with
    /// <paramref name="sentAfterUtc"/> = null (no time filter). Admin-
    /// authored broadcast titles are unique per call (admin-chosen
    /// free-form text), so the original dedup semantics are preserved.
    /// </para>
    /// <para>
    /// Returns the FULL entity (not just a bool) so the handler can read
    /// the original <c>RecipientCount</c> and return it as the success
    /// value — the caller (the hosted service) doesn't inspect the return
    /// value, but returning the correct count keeps the audit log honest.
    /// </para>
    /// </remarks>
    /// <param name="title">
    /// The title to match EXACTLY (case-sensitive, ordinal).
    /// </param>
    /// <param name="kind">
    /// The <see cref="TakOne.Domain.Notifications.Enums.NotificationKind"/>
    /// to match (typically <see cref="TakOne.Domain.Notifications.Enums.NotificationKind.AppUpdate"/>
    /// for the system-emitted app-update flow).
    /// </param>
    /// <param name="sentAfterUtc">
    /// Optional: when non-null, restricts the lookup to broadcasts sent
    /// strictly after this UTC timestamp. Pass
    /// <c>DateTime.UtcNow.AddMinutes(-5)</c> for time-windowed dedup
    /// (used by the app-update handler). Pass <c>null</c> for unbounded
    /// dedup (used by the admin-broadcast handler).
    /// </param>
    Task<BroadcastNotification?> GetByTitleAndKindAsync(
        string title,
        TakOne.Domain.Notifications.Enums.NotificationKind kind,
        DateTime? sentAfterUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated slice of past broadcasts, newest-first. Used by
    /// the admin audit page on the new <c>/Admin/Notifications</c> route.
    /// </summary>
    Task<PaginatedResult<BroadcastNotification>> GetPaginatedAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HARD-DELETES every <c>BroadcastNotification</c> audit row whose
    /// <see cref="BroadcastNotification.SentAtUtc"/> is older than
    /// <paramref name="olderThanUtc"/>. Used by the
    /// <c>NotificationRetentionCleanupHostedService</c> alongside the
    /// per-user <c>Notifications</c> cleanup.
    /// </summary>
    /// <remarks>
    /// <b>WHY DELETE BROADCAST AUDIT ROWS AT ALL</b>: broadcasts fan out
    /// to per-user <c>Notification</c> rows at creation time. Once those
    /// per-user rows are gone (per the 60-day per-user retention policy),
    /// the broadcast audit row has no remaining business value — it's
    /// just a header with no fanout left. Keeping stale audit rows
    /// indefinitely would make the admin broadcast-list page paginated-
    /// scroll longer and longer for no signal.
    /// <para>
    /// Same 60-day retention as per-user notifications: this keeps the
    /// audit window aligned with the inbox window so an admin browsing
    /// the audit page sees only broadcasts that AT LEAST ONE user could
    /// still have in their inbox.
    /// </para>
    /// </remarks>
    /// <param name="olderThanUtc">
    /// The UTC cutoff. Any broadcast whose <c>SentAtUtc</c> is STRICTLY
    /// LESS THAN this value is deleted.
    /// </param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteOlderThanAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);
}
