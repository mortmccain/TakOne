namespace TakOne.Domain.Notifications.Enums;

/// <summary>
/// Discriminator for the kind of activity a <see cref="Entities.Notification"/>
/// represents. Maps 1:1 to the four sale-lifecycle domain events
/// (<c>SaleSubmittedDomainEvent</c>, <c>SaleApprovedDomainEvent</c>,
/// <c>SaleInvoicedDomainEvent</c>, <c>SaleCancelledDomainEvent</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN ENUM (not a string)</b>: stable discriminator for DB indexing,
/// resilient to renaming the user-facing label, and the UI layer can map
/// each kind to a localized title + icon without a magic-string lookup.
/// </para>
/// <para>
/// <b>WHY NO SaleCreated KIND</b>: drafts are private carts. The customer
/// who created the cart already knows. No notification is emitted for
/// draft creation (would be pure noise).
/// </para>
/// <para>
/// <b>EXTENSIBILITY</b>: when non-sale notifications are added (e.g. a
/// "your account was created" notification), append to this enum — DO NOT
/// reuse existing values, since persisted <c>Notifications.Kind</c> column
/// references these integers.
/// </para>
/// </remarks>
public enum NotificationKind
{
    /// <summary>
    /// A sale transitioned Draft → Pending (the customer submitted their
    /// cart for staff review). Notifies the customer ("your order is
    /// awaiting approval") and the creator (if they're staff acting on
    /// behalf of a customer).
    /// </summary>
    SaleSubmitted = 1,

    /// <summary>
    /// A sale transitioned Pending → Approved. Notifies the customer
    /// ("your order was approved by {approver}") and the approver
    /// ("you approved order {number}").
    /// </summary>
    SaleApproved = 2,

    /// <summary>
    /// A sale transitioned Approved → Invoiced (physical handover
    /// complete). Notifies the customer ("your order is ready for
    /// pickup") and the invoicer ("you invoiced order {number}").
    /// </summary>
    SaleInvoiced = 3,

    /// <summary>
    /// A sale was cancelled (Pending|Approved → Cancelled). Notifies the
    /// customer ("your order was cancelled: {reason}") and the canceller
    /// ("you cancelled order {number}").
    /// </summary>
    SaleCancelled = 4,

    /// <summary>
    /// An admin-authored broadcast notification (a free-form Title + Message
    /// sent by an admin to a scoped audience: everyone, a role, a customer
    /// group, or a specific user). Created by
    /// <c>SendBroadcastNotificationCommandHandler</c> which:
    ///   1. Resolves the recipient user Ids for the chosen scope.
    ///   2. Creates ONE <c>BroadcastNotification</c> audit row (admin's
    ///      source-of-truth: who sent it, when, to whom, the title/message,
    ///      the recipient count).
    ///   3. Fans out N per-user <c>Notification</c> rows (one per recipient)
    ///      with Kind=Broadcast, Title/Message copied verbatim, and
    ///      BroadcastId pointing back to the audit row.
    /// All in the same EF Core transaction (Wolverine's AutoApplyTransactions).
    /// If the transaction rolls back, no Notification row reaches any user —
    /// no false broadcast.
    /// </summary>
    /// <remarks>
    /// <b>NOT SUBJECT TO THE DEDUP UNIQUE INDEX</b>: the
    /// <c>UX_Notifications_UserId_SaleId_Kind</c> index is filtered to
    /// <c>WHERE SaleId IS NOT NULL</c>. Broadcast notifications have
    /// <c>SaleId IS NULL</c>, so each fanout row is a distinct INSERT — no
    /// dedup collision even if the same broadcast somehow fans out twice
    /// (which it can't, because the audit row's Id is unique per send and
    /// each fanout row carries that BroadcastId).
    /// </remarks>
    Broadcast = 5,

    /// <summary>
    /// A system-emitted "app updated" notification, broadcast to every user
    /// by <c>AppUpdateBroadcasterHostedService</c> at app startup when the
    /// running assembly version differs from <c>SystemSettings.LastKnownAppVersion</c>.
    /// Reuses the same fanout pipeline as <see cref="Broadcast"/> (one
    /// <c>BroadcastNotification</c> audit row + N per-user Notification rows
    /// with Kind=AppUpdate). After broadcasting, the hosted service persists
    /// the new version so subsequent restarts don't re-broadcast.
    /// </summary>
    /// <remarks>
    /// <b>WHY A SEPARATE KIND (not just Broadcast)</b>: lets the UI render
    /// the app-update notification with a distinct icon (system_update), a
    /// distinct color, and a "Reload" action button (calling
    /// <c>location.reload()</c>) instead of the generic broadcast tap-to-dismiss.
    /// It also lets admins filter the audit list to see only auto-emitted
    /// app-update broadcasts.
    /// </remarks>
    AppUpdate = 6
}
