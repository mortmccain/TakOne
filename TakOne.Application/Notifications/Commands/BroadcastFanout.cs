using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands;

/// <summary>
/// Shared fanout helper that materializes a broadcast into ONE
/// <see cref="BroadcastNotification"/> audit row + N per-user
/// <see cref="Notification"/> rows, all in the caller's EF Core transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A SHARED HELPER (vs. duplicating fanout in two handlers)</b>:
/// Two callers need this logic:
/// <list type="number">
///   <item><c>SendBroadcastNotificationCommandHandler</c> — admin-authored
///         broadcast, SentByUserId = the admin's Id, FanoutKind = Broadcast.</item>
///   <item><c>EmitAppUpdateBroadcastCommandHandler</c> — system-emitted
///         broadcast at app startup, SentByUserId = Guid.Empty, FanoutKind = AppUpdate.</item>
/// </list>
/// Extracting the fanout here keeps the two handlers thin and ensures the
/// transactional invariant (audit row + N fanout rows persist atomically, or
/// all roll back) is enforced in exactly one place.
/// </para>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: the caller's Wolverine handler is
/// enrolled in an EF Core transaction by Wolverine's
/// <c>AutoApplyTransactions</c> middleware. This helper calls
/// <see cref="IUnitOfWork.SaveChangesAsync"/> at the end, which persists the
/// audit row + all N fanout rows in that single transaction. Wolverine's EF
/// Core domain-event scraper picks up the <c>NotificationCreatedDomainEvent</c>s
/// raised by each <see cref="Notification.CreateBroadcast"/> call at
/// SaveChangesAsync time and writes them to the <c>wolverine_messages</c>
/// outbox table atomically. If SaveChangesAsync fails, everything rolls
/// back — no partial broadcast, no false SignalR pings.
/// </para>
/// <para>
/// <b>SCOPE-TARGET CONSISTENCY</b>: the caller (the validator or the
/// system caller) is responsible for ensuring scope-target consistency
/// BEFORE calling this helper. The <see cref="BroadcastNotification.Create"/>
/// factory re-validates it as a defense-in-depth (throws DomainException if
/// violated), but the user-facing path should catch it earlier via
/// FluentValidation so a clean Result.Failure can be returned.
/// </para>
/// <para>
/// <b>RECIPIENT RESOLUTION</b>: the helper resolves recipient user Ids
/// according to <see cref="BroadcastScope"/> via the user repository. The
/// resolution is done INSIDE the handler's transaction so the recipient
/// list is consistent with the writes — if a user is deactivated mid-broadcast
/// (rare race), they won't be in the resolved list AND their fanout row
/// won't be created (no orphan notification).
/// </para>
/// <para>
/// <b>EMPTY RECIPIENT LIST IS VALID</b>: an admin might broadcast to an
/// empty customer group (e.g. a deactivated group with no active members).
/// The audit row is still created with RecipientCount=0 — this records
/// the admin's intent and surfaces in the audit list as "reached 0 users".
/// No fanout rows are created, no SignalR pings. This is not an error.
/// </para>
/// </remarks>
internal static class BroadcastFanout
{
    /// <summary>
    /// Executes the broadcast fanout. Returns the number of per-user
    /// Notification rows created (= recipient count).
    /// </summary>
    public static async Task<int> ExecuteAsync(
        Guid sentByUserId,
        BroadcastScope scope,
        string? targetRoleName,
        Guid? targetGroupId,
        Guid? targetUserId,
        string title,
        string message,
        NotificationKind fanoutKind,
        IUserRepository userRepository,
        IBroadcastNotificationRepository broadcastRepository,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // ── 1. Resolve recipient user Ids based on scope. ──
        // All four paths produce a List<Guid> of distinct, active user Ids.
        // The resolution happens INSIDE the handler's EF Core transaction
        // (Wolverine's AutoApplyTransactions) so the recipient list is
        // consistent with the writes — no orphan notifications if a user
        // is deactivated mid-broadcast.
        List<Guid> recipientIds = scope switch
        {
            BroadcastScope.All => await userRepository.GetAllActiveUserIdsAsync(cancellationToken),
            BroadcastScope.Role => await userRepository.GetActiveUserIdsInRoleAsync(targetRoleName!, cancellationToken),
            BroadcastScope.Group => await userRepository.GetActiveUserIdsInGroupAsync(targetGroupId!.Value, cancellationToken),
            BroadcastScope.User => await ResolveSingleUserAsync(userRepository, targetUserId!.Value, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown BroadcastScope: {scope}")
        };

        // ── 2. Create the parent BroadcastNotification audit row. ──
        // The factory enforces scope-target consistency + title/message
        // bounds as defense-in-depth. RecipientCount is the resolved count
        // (may be 0 — see class doc on empty recipient lists).
        var broadcast = BroadcastNotification.Create(
            sentByUserId: sentByUserId,
            scope: scope,
            targetRoleName: targetRoleName,
            targetGroupId: targetGroupId,
            targetUserId: targetUserId,
            title: title,
            message: message,
            fanoutKind: fanoutKind,
            recipientCount: recipientIds.Count);

        await broadcastRepository.AddAsync(broadcast, cancellationToken);

        // ── 3. Fan out per-user Notification rows (one per recipient). ──
        // Each fanout row carries Kind=Broadcast (or AppUpdate), the
        // admin-authored title/message verbatim, and a BroadcastId pointer
        // back to the audit row. Each CreateBroadcast call raises
        // NotificationCreatedDomainEvent, which Wolverine's EF Core scraper
        // picks up at SaveChangesAsync time and routes through the outbox
        // to NotificationCreatedBroadcastHandler → SignalR ping per user.
        foreach (var recipientId in recipientIds)
        {
            var notification = Notification.CreateBroadcast(
                userId: recipientId,
                kind: fanoutKind,
                broadcastId: broadcast.Id,
                title: title,
                message: message);

            await notificationRepository.AddAsync(notification, cancellationToken);
        }

        // ── 4. Persist — Wolverine's AutoApplyTransactions commits the
        //    surrounding EF Core transaction atomically after the handler
        //    returns. If SaveChangesAsync fails (rare — no DB-level FKs to
        //    violate), all INSERTs roll back: no audit row, no fanout rows,
        //    no outbox messages, no SignalR pings. Atomicity guaranteed. ──
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Broadcast {BroadcastId} ({Kind}, scope={Scope}, sentBy={SentByUserId}) fanned out to {Count} recipient(s).",
            broadcast.Id, fanoutKind, scope, sentByUserId, recipientIds.Count);

        return recipientIds.Count;
    }

    /// <summary>
    /// Resolves a single recipient for Scope=User. Returns an empty list
    /// (not an error) if the target user doesn't exist or is inactive —
    /// the audit row will record RecipientCount=0. This is the same
    /// "empty recipient list is valid" semantics as the other scopes.
    /// </summary>
    private static async Task<List<Guid>> ResolveSingleUserAsync(
        IUserRepository userRepository,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        // Defensive: the validator should have already verified the user
        // exists. If somehow the user was deleted between validation and
        // fanout, we treat it as an empty recipient list (audit row
        // records RecipientCount=0, no fanout row created). This is the
        // safest non-throwing behavior — the admin sees in the audit list
        // "your broadcast to user X reached 0 users (user not found)" and
        // can investigate.
        var user = await userRepository.GetByIdAsync(targetUserId, cancellationToken);
        if (user is null)
        {
            return new List<Guid>(0);
        }

        // The user exists — fan out to just them. Note: we don't check
        // IsActive here because (a) the validator should have, and (b)
        // GetAllActiveUserIdsAsync (the All-scope path) already filters
        // active — for consistency, single-user should too. So we filter
        // here as well.
        // Domain.User.IsActive is the soft-delete flag. Skip inactive users
        // (same rule as the All scope).
        return user.IsActive
            ? new List<Guid> { targetUserId }
            : new List<Guid>(0);
    }
}
