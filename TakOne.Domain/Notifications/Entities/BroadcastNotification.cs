using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Notifications.Entities;

/// <summary>
/// Aggregate root for an admin-authored broadcast: a single source-of-truth
/// record of "who sent what, to whom, when, and how many people received it".
/// </summary>
/// <remarks>
/// <para>
/// <b>TWO-TABLE DESIGN</b> (this aggregate + per-user <see cref="Notification"/> rows):
/// the broadcast is one logical event, but each recipient needs their own
/// dismissible, persisted, SignalR-pinged <see cref="Notification"/> row
/// (the existing notification system is per-user — the bell badge, the
/// read-state, the broadcast-ping, all key off per-user rows). So a broadcast
/// fans out into N per-user <c>Notification</c> rows, each carrying
/// <c>Kind=Broadcast</c> (or <c>AppUpdate</c>), the <c>Title</c>/<c>Message</c>
/// verbatim, and a <c>BroadcastId</c> foreign-key pointer back to this
/// aggregate's <see cref="Id"/>.
/// </para>
/// <para>
/// This aggregate is the ADMIN'S view: a single row that records the
/// broadcast's metadata + recipient count. The user-facing rows live in
/// <see cref="Notification"/>. Splitting them keeps each aggregate
/// cohesive (one is admin-audit, the other is user-inbox) and lets the
/// admin's audit list query this table directly without scanning the
/// (potentially huge) per-user Notifications table.
/// </para>
/// <para>
/// <b>TRANSACTIONAL INVARIANT</b>: <see cref="Create"/> is called by
/// <c>SendBroadcastNotificationCommandHandler</c> in the SAME EF Core
/// transaction as the N per-user Notification fanout INSERTs. Wolverine's
/// <c>AutoApplyTransactions</c> middleware enrolls the handler in one
/// transaction; if SaveChangesAsync fails (e.g. one recipient Id is invalid
/// and a FK violation occurs — though we have no DB-level FK, so this is
/// a non-issue), the entire broadcast rolls back — no per-user Notification
/// row reaches anyone, and no audit row is left dangling. This is the
/// enterprise guarantee: a broadcast either reaches ALL intended recipients
/// or NONE.
/// </para>
/// <para>
/// <b>SCOPE-TARGET CONSISTENCY INVARIANT</b>: see
/// <see cref="BroadcastScope"/>'s doc — the factory enforces that exactly
/// one of <see cref="TargetRoleName"/>/<see cref="TargetGroupId"/>/<see cref="TargetUserId"/>
/// is set according to <see cref="Scope"/>, and all three are null when
/// <c>Scope=All</c>. A malformed command is rejected at the domain boundary.
/// </para>
/// <para>
/// <b>NO DOMAIN EVENT</b>: this aggregate does NOT raise a domain event on
/// creation. The fanout per-user <c>Notification</c> rows each raise their
/// own <c>NotificationCreatedDomainEvent</c>, which is what pings SignalR
/// for each recipient. Raising a separate <c>BroadcastCreatedDomainEvent</c>
/// would be redundant (no extra consumer needs it) and would add a
/// no-subscriber warning to Wolverine's logs. The audit row IS the audit
/// record — no event needed.
/// </para>
/// <para>
/// <b>RECIPIENT COUNT</b>: <see cref="RecipientCount"/> is set by the
/// application handler after resolving the recipient user Ids but BEFORE
/// constructing this aggregate (the count is an input to the factory). It
/// records how many per-user Notification rows were created in this
/// broadcast. The admin's audit list surfaces this so the admin can see
/// "your broadcast reached 42 users".
/// </para>
/// <para>
/// <b>IMMUTABLE</b>: there are no mutation methods on this aggregate. A
/// broadcast is a one-shot event — to "edit" or "recall" a broadcast,
/// future features would add a <c>RecalledAtUtc</c> field + a
/// <c>Recall()</c> method that hides all linked per-user Notifications
/// (UI filter on <c>Notification.BroadcastId = X AND BroadcastNotification.RecalledAtUtc IS NULL</c>).
/// Out of scope for now.
/// </para>
/// </remarks>
public sealed class BroadcastNotification : AggregateRoot
{
    // ── PROPERTIES ──────────────────────────────────────────────────────

    /// <summary>
    /// The user Id of the admin who authored this broadcast. Persisted for
    /// audit (the admin's audit list shows "sent by you" with their own Id).
    /// For auto-emitted <see cref="NotificationKind.AppUpdate"/> broadcasts,
    /// this is <see cref="System.Guid.Empty"/> (no human author — the
    /// <c>AppUpdateBroadcasterHostedService</c> sends on behalf of the system).
    /// </summary>
    public Guid SentByUserId { get; private set; }

    /// <summary>
    /// UTC timestamp the broadcast was sent. Indexed (the admin audit list
    /// is ordered by this DESC).
    /// </summary>
    public DateTime SentAtUtc { get; private set; }

    /// <summary>
    /// The audience selector. See <see cref="BroadcastScope"/> for the
    /// semantics of each value and the scope-target consistency invariant.
    /// </summary>
    public BroadcastScope Scope { get; private set; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.Role"/>.
    /// The ASP.NET Identity role name (e.g. "Customer", "Employee"). Null
    /// for other scopes. Persisted as the role NAME (not a Guid) because
    /// roles in ASP.NET Identity are name-keyed in our seeder, and the
    /// admin's audit list displays the role name directly (no extra join
    /// needed to render "sent to role: Employee").
    /// </summary>
    public string? TargetRoleName { get; private set; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.Group"/>.
    /// The customer group's Id. Null for other scopes.
    /// </summary>
    public Guid? TargetGroupId { get; private set; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.User"/>.
    /// The recipient user's Id. Null for other scopes.
    /// </summary>
    public Guid? TargetUserId { get; private set; }

    /// <summary>
    /// The admin-authored title (subject line). Required, max 200 chars.
    /// Copied verbatim into each fanout <see cref="Notification.Title"/>.
    /// </summary>
    public string Title { get; private set; } = null!;

    /// <summary>
    /// The admin-authored message body. Required, max 1000 chars.
    /// Copied verbatim into each fanout <see cref="Notification.Message"/>.
    /// </summary>
    public string Message { get; private set; } = null!;

    /// <summary>
    /// The <see cref="NotificationKind"/> of the fanout rows
    /// (<see cref="NotificationKind.Broadcast"/> for admin-authored, or
    /// <see cref="NotificationKind.AppUpdate"/> for the auto-emitted
    /// app-update broadcast). Persisted so the admin audit list can filter
    /// "only app-update broadcasts" vs "only admin-authored broadcasts"
    /// without a separate boolean.
    /// </summary>
    public NotificationKind FanoutKind { get; private set; }

    /// <summary>
    /// How many per-user <see cref="Notification"/> rows were created in
    /// this broadcast. Set by the application handler after resolving
    /// recipients, before constructing this aggregate. Surfaces in the
    /// admin's audit list as "reached N users".
    /// </summary>
    public int RecipientCount { get; private set; }

    // ── CONSTRUCTORS ────────────────────────────────────────────────────

#pragma warning disable CS8618
    /// <summary>
    /// Parameterless ctor for EF Core. DO NOT use from application code.
    /// </summary>
    private BroadcastNotification() : base(Guid.Empty) { }
#pragma warning restore CS8618

    private BroadcastNotification(
        Guid sentByUserId,
        BroadcastScope scope,
        string? targetRoleName,
        Guid? targetGroupId,
        Guid? targetUserId,
        string title,
        string message,
        NotificationKind fanoutKind,
        int recipientCount)
        : base(Guid.NewGuid())
    {
        EnsureScopeValid(scope);
        EnsureScopeTargetConsistency(scope, targetRoleName, targetGroupId, targetUserId);
        EnsureTitleValid(title);
        EnsureMessageValid(message);
        EnsureFanoutKindValid(fanoutKind);
        EnsureRecipientCountValid(recipientCount);

        // SentByUserId may be Guid.Empty for system-emitted broadcasts
        // (AppUpdate). Don't guard against empty here — the application
        // handler decides whether to pass a real admin Id or Guid.Empty.

        SentByUserId = sentByUserId;
        SentAtUtc = DateTime.UtcNow;
        Scope = scope;
        TargetRoleName = targetRoleName;
        TargetGroupId = targetGroupId;
        TargetUserId = targetUserId;
        Title = title;
        Message = message;
        FanoutKind = fanoutKind;
        RecipientCount = recipientCount;
    }

    // ── FACTORY ─────────────────────────────────────────────────────────

    /// <summary>
    /// The ONLY way to construct a <see cref="BroadcastNotification"/> from
    /// application code. Validates the scope-target consistency invariant
    /// and the title/message/recipientCount bounds.
    /// </summary>
    /// <param name="sentByUserId">
    /// The admin's user Id, or <see cref="Guid.Empty"/> for system-emitted
    /// broadcasts (AppUpdate).
    /// </param>
    /// <param name="scope">The audience selector.</param>
    /// <param name="targetRoleName">
    /// Required iff <paramref name="scope"/> == <see cref="BroadcastScope.Role"/>.
    /// </param>
    /// <param name="targetGroupId">
    /// Required iff <paramref name="scope"/> == <see cref="BroadcastScope.Group"/>.
    /// </param>
    /// <param name="targetUserId">
    /// Required iff <paramref name="scope"/> == <see cref="BroadcastScope.User"/>.
    /// </param>
    /// <param name="title">The broadcast's subject line (1–200 chars).</param>
    /// <param name="message">The broadcast's body (1–1000 chars).</param>
    /// <param name="fanoutKind">
    /// <see cref="NotificationKind.Broadcast"/> for admin-authored, or
    /// <see cref="NotificationKind.AppUpdate"/> for the auto-emitted
    /// app-update broadcast.
    /// </param>
    /// <param name="recipientCount">
    /// The number of per-user Notification rows that will be (or have been)
    /// created in the same transaction. Set by the application handler after
    /// resolving recipients.
    /// </param>
    public static BroadcastNotification Create(
        Guid sentByUserId,
        BroadcastScope scope,
        string? targetRoleName,
        Guid? targetGroupId,
        Guid? targetUserId,
        string title,
        string message,
        NotificationKind fanoutKind,
        int recipientCount)
    {
        return new BroadcastNotification(
            sentByUserId,
            scope,
            targetRoleName,
            targetGroupId,
            targetUserId,
            title,
            message,
            fanoutKind,
            recipientCount);
    }

    // ── GUARDS ──────────────────────────────────────────────────────────

    private static void EnsureScopeValid(BroadcastScope scope)
    {
        if (!Enum.IsDefined(typeof(BroadcastScope), scope))
        {
            throw new DomainException(
                $"BroadcastScope must be one of: {string.Join(", ", Enum.GetNames(typeof(BroadcastScope)))}.");
        }

        // Reject the implicit-zero value (0). Our enum starts at 1, so a
        // default-uninitialized BroadcastScope field would be 0, which is
        // invalid. Same pattern as SystemSettings.EnsureLimitModeValid.
        if ((int)scope == 0)
        {
            throw new DomainException(
                "BroadcastScope cannot be 0 (Uninitialized). Use one of: All, Role, Group, User.");
        }
    }

    private static void EnsureScopeTargetConsistency(
        BroadcastScope scope,
        string? targetRoleName,
        Guid? targetGroupId,
        Guid? targetUserId)
    {
        switch (scope)
        {
            case BroadcastScope.All:
                if (!string.IsNullOrEmpty(targetRoleName) || targetGroupId.HasValue || targetUserId.HasValue)
                {
                    throw new DomainException(
                        "Scope=All must have null TargetRoleName, TargetGroupId, and TargetUserId.");
                }
                break;

            case BroadcastScope.Role:
                if (string.IsNullOrWhiteSpace(targetRoleName))
                {
                    throw new DomainException("Scope=Role requires a non-empty TargetRoleName.");
                }
                if (targetGroupId.HasValue || targetUserId.HasValue)
                {
                    throw new DomainException(
                        "Scope=Role must have null TargetGroupId and TargetUserId.");
                }
                break;

            case BroadcastScope.Group:
                if (!targetGroupId.HasValue || targetGroupId.Value == Guid.Empty)
                {
                    throw new DomainException("Scope=Group requires a non-empty TargetGroupId.");
                }
                if (!string.IsNullOrEmpty(targetRoleName) || targetUserId.HasValue)
                {
                    throw new DomainException(
                        "Scope=Group must have null TargetRoleName and TargetUserId.");
                }
                break;

            case BroadcastScope.User:
                if (!targetUserId.HasValue || targetUserId.Value == Guid.Empty)
                {
                    throw new DomainException("Scope=User requires a non-empty TargetUserId.");
                }
                if (!string.IsNullOrEmpty(targetRoleName) || targetGroupId.HasValue)
                {
                    throw new DomainException(
                        "Scope=User must have null TargetRoleName and TargetGroupId.");
                }
                break;
        }
    }

    private static void EnsureTitleValid(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("Broadcast Title is required.");
        }

        var trimmed = title.Trim();
        if (trimmed.Length > 200)
        {
            throw new DomainException(
                $"Broadcast Title must be 200 characters or fewer (got {trimmed.Length}).");
        }
    }

    private static void EnsureMessageValid(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new DomainException("Broadcast Message is required.");
        }

        var trimmed = message.Trim();
        if (trimmed.Length > 1000)
        {
            throw new DomainException(
                $"Broadcast Message must be 1000 characters or fewer (got {trimmed.Length}).");
        }
    }

    private static void EnsureFanoutKindValid(NotificationKind kind)
    {
        // Only Broadcast and AppUpdate are valid fanout kinds for a
        // BroadcastNotification. The sale-lifecycle kinds (Submitted,
        // Approved, Invoiced, Cancelled) are emitted by the per-sale
        // domain-event handlers, NOT by the broadcast pipeline.
        if (kind != NotificationKind.Broadcast && kind != NotificationKind.AppUpdate)
        {
            throw new DomainException(
                $"FanoutKind must be Broadcast or AppUpdate (got {kind}). Sale-lifecycle kinds are not valid here.");
        }
    }

    private static void EnsureRecipientCountValid(int recipientCount)
    {
        // 0 is valid — an admin might broadcast to an empty customer group
        // (e.g. a deactivated group with no active members). The audit row
        // is still created; the RecipientCount simply reads 0. Negative is
        // impossible by construction but defended against here.
        if (recipientCount < 0)
        {
            throw new DomainException(
                $"RecipientCount cannot be negative (got {recipientCount}).");
        }
    }
}
