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
    SaleCancelled = 4
}
