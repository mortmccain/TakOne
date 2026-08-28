using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.Events;

namespace TakOne.Domain.Sales.Entities;

/// <summary>
/// Aggregate root for a Sale (customer order).
///
/// LIFECYCLE:
///   Draft    — cart state; customer is adding/removing items.
///   Pending  — submitted; awaiting staff approval.
///   Approved — staff signed off.
///   Invoiced — physical handover complete; terminal.
///   Cancelled— terminal; can be reached from Pending or Approved.
///
/// DRAFT DELETION (hard delete):
///   A Sale in Draft state is NOT cancelled — it is hard-deleted via the
///   repository (ISaleRepository.DeleteAsync) by the application layer.
///   We don't keep draft carts around for audit purposes; only submitted
///   sales are persisted as historical records.
///   The Sale.Cancel() method therefore throws if called from Draft.
///
/// AUDIT FIELDS CONVENTION:
///   Every state transition has a corresponding "<state>ByUserId" Guid column.
///   These are NON-NULLABLE Guids (default Guid.Empty) — the convention is
///   that a consumer reads the field only when the Sale is in (or past) the
///   corresponding state. E.g., read ApprovedByUserId only when
///   Status == Approved || Status == Invoiced.
///
///   Note: Submit() has no SubmittedByUserId field — the submitter is always
///   the sale's creator (CreatedByUserId), since a sale cannot be submitted
///   by anyone other than the customer who created it.
///
///   We snapshot only CustomerName and CreatedByName (because a sales employee
///   may create a sale on behalf of a customer, and we want both names available
///   without joining to the Users table). For other transitions, we store only
///   the user Id — investigation joins to Users by Id to get the name.
///
/// PURCHASE LIMIT ENFORCEMENT:
///   When a customer adds a line item, the application layer looks up the
///   per-group purchase limit on the Product (via Product.GetPurchaseLimitForGroup)
///   using the buyer's User.GroupName. If a limit exists, it is passed to
///   <see cref="AddLineItem"/> as <c>purchaseLimit</c>. The Sale aggregate
///   then enforces: total quantity of this Product on this Sale ≤ purchaseLimit.
///   If <c>purchaseLimit</c> is null, no limit is enforced.
///
/// WHAT THE SALE DOES NOT DO:
///   - It does NOT load the Product aggregate. The application layer loads
///     Products and passes their data into AddLineItem.
///   - It does NOT decrease Product stock. Stock decrement is the application
///     layer's job, on Approve().
/// </summary>
public sealed class Sale : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<SaleLineItem> _lineItems = new();



    // ==================================================================================================================================
    //                                                          PROPERTIES — IDENTITY & SNAPSHOT
    // ==================================================================================================================================



    /// <summary>
    /// The sale's permanent identifier. NULL while the sale is in Draft status
    /// — the number is allocated only when the sale is submitted (see Submit()).
    /// Once assigned (on submission), the number is immutable.
    ///
    /// RATIONALE (B2 design — deferred allocation):
    ///   Drafts are disposable carts; many are created and abandoned. Allocating
    ///   a permanent sequence number at draft creation would burn numbers on
    ///   drafts that never get submitted, producing gaps in the audit trail of
    ///   POSTED sales. By deferring allocation to Submit(), the permanent
    ///   sequence stays clean — gaps only come from voided/cancelled submitted
    ///   sales, which is the correct, auditable ERP behavior.
    ///
    /// NULL HANDLING:
    ///   - In Draft: SaleNumber is null. UI shows a pseudo-id derived from
    ///     the sale's Guid Id (e.g. "DRAFT-ABC12345") — see read-side DTOs.
    ///   - In Pending/Approved/Invoiced/Cancelled: SaleNumber is non-null and
    ///     contains the permanent INT-{Year}-{Seq:D8} identifier.
    /// </summary>
    public SaleNumber? SaleNumber { get; private set; }

    /// <summary>
    /// The user who placed the sale. Immutable after creation.
    /// (Refers to a User aggregate, since customers are just Users with the
    /// Customer role and a non-null GroupName.)
    /// </summary>
    public Guid CustomerId { get; private set; }

    /// <summary>
    /// Snapshot of the customer's full name at sale time.
    /// </summary>
    public string CustomerName { get; private set; }

    /// <summary>
    /// The user who created the sale. Usually the customer, but in special
    /// cases a sales employee may start a sale on behalf of a customer.
    /// </summary>
    public Guid CreatedByUserId { get; private set; }

    /// <summary>
    /// Snapshot of the creator's full name at sale time.
    /// </summary>
    public string CreatedByName { get; private set; }



    // ==================================================================================================================================
    //                                                          PROPERTIES — AUDIT IDs (one per state transition)
    // ==================================================================================================================================



    /// <summary>
    /// The user who approved the sale (Pending → Approved).
    /// Guid.Empty until Approve() is called. Read only when Status ≥ Approved.
    /// </summary>
    public Guid ApprovedByUserId { get; private set; }

    /// <summary>
    /// The user who marked the sale as invoiced (Approved → Invoiced).
    /// Guid.Empty until MarkAsInvoiced() is called. Read only when Status == Invoiced.
    /// </summary>
    public Guid InvoicedByUserId { get; private set; }

    /// <summary>
    /// The user who cancelled the sale (Pending|Approved → Cancelled).
    /// Guid.Empty until Cancel() is called. Read only when Status == Cancelled.
    /// </summary>
    public Guid CancelledByUserId { get; private set; }



    // ==================================================================================================================================
    //                                                          PROPERTIES — STATUS & FINANCIALS
    // ==================================================================================================================================



    public SaleStatus Status { get; private set; }
    public Money Total { get; private set; }



    // ==================================================================================================================================
    //                                                          PROPERTIES — TIMESTAMPS
    // ==================================================================================================================================



    public DateTime CreatedAtUtc { get; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public DateTime? InvoicedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }



    // ==================================================================================================================================
    //                                                          LINE ITEMS
    // ==================================================================================================================================



    /// <summary>
    /// Exposes line items as a read-only collection.
    /// External code CANNOT modify this collection directly.
    /// </summary>
    public IReadOnlyList<SaleLineItem> LineItems => _lineItems.AsReadOnly();



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private Sale() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// Creates a Sale in <see cref="SaleStatus.Draft"/> status with NO sale
    /// number (the number is allocated later, on Submit() — see the B2
    /// deferred-allocation rationale on the <see cref="SaleNumber"/>
    /// property doc above). The constructor does NOT accept a SaleNumber
    /// parameter: drafts never have a number, and accepting one would
    /// silently burn the pre-allocated number if the customer abandons
    /// the draft — a defect flagged in v4 of the brutal review.
    /// </summary>
    private Sale
        (
        Guid customerId,
        string customerName,
        Guid createdByUserId,
        string createdByName
        ) : base(Guid.NewGuid())
    {
        EnsureCustomerIdValid(customerId);
        EnsureCustomerNameValid(customerName);
        EnsureCreatedByUserValid(createdByUserId);
        EnsureCreatedByNameValid(createdByName);

        CustomerId = customerId;
        CustomerName = customerName;
        CreatedByUserId = createdByUserId;
        CreatedByName = createdByName;
        // SaleNumber intentionally NOT set here — it's allocated only on
        // Submit(). The property remains null until then.

        Status = SaleStatus.Draft;
        Total = Money.Zero("IRR");
        CreatedAtUtc = DateTime.UtcNow;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new Sale in <see cref="SaleStatus.Draft"/> status, with NO
    /// sale number assigned yet. The sale number is allocated later, when the
    /// customer submits the cart (see Submit()).
    ///
    /// This is the ONLY way to create a Sale from application code. The
    /// factory deliberately does NOT accept a SaleNumber parameter —
    /// drafts have no number (B2 deferred-allocation design). Allowing a
    /// caller to pass one would create a foot-gun: the number would be
    /// silently burned if the customer abandoned the draft.
    ///
    /// If a pre-allocated SaleNumber must be associated with a sale (e.g.
    /// for an admin recovery flow), call Submit() with the allocated
    /// number — Submit is the only path that assigns SaleNumber.
    /// </summary>
    public static Sale Create
        (
        Guid customerId,
        string customerName,
        Guid createdByUserId,
        string createdByName
        )
    {
        var sale = new Sale
            (
            customerId,
            customerName,
            createdByUserId,
            createdByName
            );

        sale.AddDomainEvent
            (
            new SaleCreatedDomainEvent
                (
                    sale.Id,
                    sale.CustomerId,
                    sale.CustomerName,
                    sale.SaleNumber,
                    sale.CreatedAtUtc,
                    sale.CreatedByUserId,
                    sale.CreatedByName
            )
                );

        return sale;
    }



    // ==================================================================================================================================
    //                                                          LINE ITEM MANAGEMENT (Draft only)
    // ==================================================================================================================================



    /// <summary>
    /// Adds a line item to the sale, or — if a line for the same Product already
    /// exists — increments that line's quantity by <paramref name="quantity"/>.
    ///
    /// Only callable while the Sale is in <see cref="SaleStatus.Draft"/>.
    ///
    /// PURCHASE LIMIT:
    ///   If <paramref name="purchaseLimit"/> is non-null, the resulting total
    ///   quantity for this Product on this Sale must not exceed it. The
    ///   application layer is responsible for looking up the correct limit
    ///   (via Product.GetPurchaseLimitForGroup, using the buyer's GroupName)
    ///   and passing it here. Null means "no limit for this buyer/product".
    /// </summary>
    public void AddLineItem
        (
        Guid productId,
        string productName,
        int quantity,
        Money unitPrice,
        int? purchaseLimit = null
        )
    {
        EnsureDraft();
        EnsureQuantityValid(quantity);
        EnsureUnitPriceValid(unitPrice);

        var existingLine = _lineItems.FirstOrDefault(li => li.ProductId == productId);

        if (existingLine is not null)
        {
            var newQuantity = existingLine.Quantity + quantity;
            EnsurePurchaseLimitRespected(productId, newQuantity, purchaseLimit);

            existingLine.UpdateQuantity(newQuantity);
            RecalculateTotal();

            // Use the STORED snapshot (existingLine.ProductName) — NOT the
            // input productName — for consistency with UpdateLineItemQuantity
            // (which also uses the stored snapshot). The input parameter
            // reflects what the caller happened to pass THIS time; the
            // stored snapshot is the authoritative value the line was
            // created with. Using the input would let a caller overwrite
            // the snapshot via the event without going through any
            // rename flow — a subtle consistency bug.
            AddDomainEvent
                (
                new SaleLineItemUpdatedDomainEvent
                (
                Id,
                existingLine.Id,
                existingLine.ProductId,
                existingLine.ProductName,
                newQuantity,
                existingLine.UnitPrice,
                existingLine.LineNumber
                )
                );
        }
        else
        {
            EnsurePurchaseLimitRespected(productId, quantity, purchaseLimit);

            var lineNumber = GetNextLineNumber();

            var lineItem = new SaleLineItem
                (
                productId,
                productName,
                quantity,
                unitPrice,
                lineNumber
                );

            _lineItems.Add(lineItem);
            RecalculateTotal();

            AddDomainEvent
                (
                new SaleLineItemAddedDomainEvent
                (
                Id,
                lineItem.Id,
                productId,
                productName,
                quantity,
                unitPrice,
                lineNumber
                )
                );
        }
    }

    /// <summary>
    /// Updates the quantity of an existing line item.
    /// Only callable while the Sale is in <see cref="SaleStatus.Draft"/>.
    /// </summary>
    public void UpdateLineItemQuantity(Guid lineItemId, int newQuantity, int? purchaseLimit = null)
    {
        EnsureDraft();
        EnsureQuantityValid(newQuantity);

        var lineItem = EnsureLineItemExists(lineItemId);

        EnsurePurchaseLimitRespected(lineItem.ProductId, newQuantity, purchaseLimit);

        lineItem.UpdateQuantity(newQuantity);
        RecalculateTotal();

        AddDomainEvent
            (
            new SaleLineItemUpdatedDomainEvent
            (
            Id,
            lineItem.Id,
            lineItem.ProductId,
            lineItem.ProductName,
            newQuantity,
            lineItem.UnitPrice,
            lineItem.LineNumber
            )
            );
    }

    /// <summary>
    /// Removes a line item from the sale.
    /// Only callable while the Sale is in <see cref="SaleStatus.Draft"/>.
    /// </summary>
    public void RemoveLineItem(Guid lineItemId)
    {
        EnsureDraft();

        var lineItem = EnsureLineItemExists(lineItemId);
        var removedLineNumber = lineItem.LineNumber;

        _lineItems.Remove(lineItem);
        RecalculateTotal();

        AddDomainEvent
            (
            new SaleLineItemRemovedDomainEvent
            (
            Id,
            lineItem.Id,
            lineItem.ProductId,
            removedLineNumber
            )
            );
    }



    // ==================================================================================================================================
    //                                                          STATE TRANSITIONS
    // ==================================================================================================================================



    /// <summary>
    /// Transitions the Sale from <see cref="SaleStatus.Draft"/> to
    /// <see cref="SaleStatus.Pending"/>. This is the customer "submit cart" action.
    /// The sale must have at least one line item and a positive total.
    /// After submission, line items can no longer be modified.
    ///
    /// SALE NUMBER ALLOCATION (B2 design):
    ///   The permanent SaleNumber is allocated by the application layer (via
    ///   ISaleNumberGenerator) IMMEDIATELY BEFORE calling Submit(), and is
    ///   passed in here. This is the only moment a SaleNumber gets assigned
    ///   to a Sale — drafts have no number, and the number is immutable after
    ///   submission. Allocating here (not at draft creation) keeps the
    ///   permanent sequence clean: drafts that are abandoned never burn a
    ///   number, so the audit trail of POSTED sales has gaps only from
    ///   voided/cancelled sales (which is the correct, auditable ERP behavior).
    /// </summary>
    /// <param name="saleNumber">
    /// The newly-allocated SaleNumber (non-null). The caller is responsible
    /// for obtaining it from <c>ISaleNumberGenerator.NextAsync</c> immediately
    /// before calling this method, so the allocation is as close as possible
    /// to the actual state transition.
    /// </param>
    /// <exception cref="DomainException">
    /// Thrown if the sale is not in Draft status, has no line items, has a
    /// zero/negative total, or if <paramref name="saleNumber"/> is null.
    /// </exception>
    public void Submit(SaleNumber saleNumber)
    {
        EnsureDraft();
        EnsureHasLineItems();
        EnsureTotalIsPositive();
        EnsureSaleNumberNotNull(saleNumber);

        SaleNumber = saleNumber;
        Status = SaleStatus.Pending;
        SubmittedAtUtc = DateTime.UtcNow;

        AddDomainEvent(new SaleSubmittedDomainEvent
            (
                Id,
                CustomerId,
                Total,
                saleNumber,
                CreatedByUserId
            ));
    }

    /// <summary>
    /// Approves the sale. Transitions from Pending to Approved.
    /// Role-based authorization (Employee / Manager) is done in the application
    /// layer — the Domain does not know about Identity roles.
    /// </summary>
    public void Approve(Guid approvedByUserId)
    {
        EnsurePending();
        EnsureTotalIsPositive();
        EnsureUserIdValid(approvedByUserId, nameof(approvedByUserId));

        Status = SaleStatus.Approved;
        ApprovedAtUtc = DateTime.UtcNow;
        ApprovedByUserId = approvedByUserId;

        AddDomainEvent(new SaleApprovedDomainEvent(Id, CustomerId, Total, approvedByUserId));
    }

    /// <summary>
    /// Marks the sale as invoiced. Transitions from Approved to Invoiced.
    /// "Invoiced" = physical handover complete. Invoiced is terminal — the sale
    /// cannot be cancelled after this point.
    /// </summary>
    public void MarkAsInvoiced(Guid invoicedByUserId)
    {
        EnsureApproved();
        EnsureUserIdValid(invoicedByUserId, nameof(invoicedByUserId));

        Status = SaleStatus.Invoiced;
        InvoicedAtUtc = DateTime.UtcNow;
        InvoicedByUserId = invoicedByUserId;

        AddDomainEvent(new SaleInvoicedDomainEvent(Id, invoicedByUserId));
    }

    /// <summary>
    /// Cancels the sale. Can be called from Pending or Approved status.
    /// Cannot cancel a Draft sale — drafts are hard-deleted via the repository.
    /// Cannot cancel an Invoiced sale (issue a credit note in a separate flow).
    /// Cannot cancel an already-cancelled sale.
    /// </summary>
    public void Cancel(Guid cancelledByUserId, string reason)
    {
        EnsureCancellable();
        EnsureReasonProvided(reason);
        EnsureUserIdValid(cancelledByUserId, nameof(cancelledByUserId));

        Status = SaleStatus.Cancelled;
        CancelledAtUtc = DateTime.UtcNow;
        CancelledByUserId = cancelledByUserId;
        CancellationReason = reason;

        AddDomainEvent(new SaleCancelledDomainEvent(Id, cancelledByUserId, reason));
    }



    // ==================================================================================================================================
    //                                                          PRIVATE HELPERS
    // ==================================================================================================================================



    /// <summary>
    /// Returns the next line number for a new line item.
    /// Line numbers are stable (deleting line 2 does NOT renumber line 3),
    /// so we compute the next number as max(existing line numbers) + 1.
    /// On an empty sale, the first line number is 1.
    /// </summary>
    private int GetNextLineNumber()
    {
        return _lineItems.Count == 0
            ? 1
            : _lineItems.Max(li => li.LineNumber) + 1;
    }

    /// <summary>
    /// Recalculates the Sale Total from line items.
    /// Called after every line item change.
    ///
    /// VALUE OBJECT IMMUTABILITY — REFERENCE REPLACEMENT IS CORRECT:
    ///   Money is an immutable value object. The correct way to "change"
    ///   a Money value is to construct a new instance and assign it —
    ///   which is exactly what this method does. The <c>+</c> operator
    ///   on Money returns a brand-new instance; we replace <c>Total</c>
    ///   with that new instance.
    ///
    ///   This works correctly because Money is mapped as a
    ///   <c>ComplexProperty</c> (not <c>OwnsOne</c>) on <c>Sale.Total</c>.
    ///   <c>ComplexProperty</c> has VALUE SEMANTICS: EF Core compares
    ///   complex type instances by value (via
    ///   <see cref="BaseValueObject.GetEqualityComponents"/>), not by
    ///   reference identity. Replacing the reference is the idiomatic
    ///   mutation pattern — EF detects the value change and generates a
    ///   clean UPDATE against the parent row's columns.
    ///
    ///   The previous mapping (<c>OwnsOne</c>) tracked Money by reference
    ///   identity, so replacing the reference produced a confused change
    ///   tracker and an UPDATE that affected 0 rows:
    ///     <c>DbUpdateConcurrencyException: expected to affect 1 row(s),
    ///     but actually affected 0 row(s)</c>
    ///   The migration to <c>ComplexProperty</c> fixes this at the EF
    ///   Core mapping level — no domain mutation hack required.
    /// </summary>
    private void RecalculateTotal()
    {
        if (_lineItems.Count == 0)
        {
            Total = Money.Zero("IRR");
            return;
        }

        var currency = _lineItems[0].UnitPrice.Currency;
        Total = _lineItems.Aggregate
            (
                Money.Zero(currency),
                (sum, item) => sum + item.GrossTotal
            );
    }



    // ==================================================================================================================================
    //                                                          GUARD METHODS
    // ==================================================================================================================================



    private void EnsureDraft()
    {
        if (Status != SaleStatus.Draft)
            throw new DomainException(
                $"Cannot modify line items of a sale that is not in Draft. Current status: '{Status}'.");
    }

    private void EnsurePending()
    {
        if (Status != SaleStatus.Pending)
            throw new DomainException(
                $"Only Pending sales can be approved. Current status: '{Status}'.");
    }

    private void EnsureApproved()
    {
        if (Status != SaleStatus.Approved)
            throw new DomainException(
                $"Only Approved sales can be marked as invoiced. Current status: '{Status}'.");
    }

    private void EnsureCancellable()
    {
        // Draft sales are NOT cancellable — they are hard-deleted via the repository.
        if (Status == SaleStatus.Draft)
            throw new DomainException(
                "Cannot cancel a Draft sale. Delete it via the repository instead.");

        if (Status == SaleStatus.Invoiced)
            throw new DomainException(
                "Cannot cancel a sale that has already been invoiced. Issue a credit note instead.");

        if (Status == SaleStatus.Cancelled)
            throw new DomainException("This sale is already cancelled.");
    }

    private void EnsureHasLineItems()
    {
        if (_lineItems.Count == 0)
            throw new DomainException("Cannot submit a sale with no line items.");
    }

    private void EnsureTotalIsPositive()
    {
        if (Total.Amount <= 0)
            throw new DomainException("Cannot submit a sale with zero or negative total.");
    }

    private static void EnsurePurchaseLimitRespected(Guid productId, int requestedQuantity, int? purchaseLimit)
    {
        if (purchaseLimit is null)
            return; // No limit for this buyer/product combination.

        if (requestedQuantity > purchaseLimit.Value)
            throw new DomainException(
                $"Purchase limit exceeded for product '{productId}'. " +
                $"Limit: {purchaseLimit.Value}, requested: {requestedQuantity}.");
    }

    private SaleLineItem EnsureLineItemExists(Guid lineItemId)
    {
        var lineItem = _lineItems.FirstOrDefault(li => li.Id == lineItemId);
        if (lineItem is null)
            throw new DomainException($"Line item with Id '{lineItemId}' was not found on this sale.");

        return lineItem;
    }

    private static void EnsureQuantityValid(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be a positive integer.");
    }

    private static void EnsureUnitPriceValid(Money unitPrice)
    {
        if (unitPrice.Amount < 0)
            throw new DomainException("Unit price cannot be negative.");
    }

    private static void EnsureReasonProvided(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A cancellation reason is required.");
    }

    private static void EnsureUserIdValid(Guid userId, string paramName)
    {
        if (userId == Guid.Empty)
            throw new DomainException($"A valid user Id is required ({paramName}).");
    }

    private static void EnsureCustomerIdValid(Guid customerId)
    {
        if (customerId == Guid.Empty)
            throw new DomainException("Customer ID is required.");
    }

    private static void EnsureCustomerNameValid(string customerName)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new DomainException("Customer name is required.");
    }

    private static void EnsureCreatedByUserValid(Guid createdByUserId)
    {
        if (createdByUserId == Guid.Empty)
            throw new DomainException("Created-by user ID is required.");
    }

    private static void EnsureSaleNumberNotNull(SaleNumber saleNumber)
    {
        // This guard is on Submit(), not on Create(). At draft creation the
        // sale number is intentionally null (B2 deferred-allocation design).
        // At submission, the application layer must allocate a fresh
        // SaleNumber via ISaleNumberGenerator and pass it in — a null here
        // means the application layer forgot to allocate, which is a
        // programmer error, not a user-facing condition.
        if (saleNumber is null)
            throw new DomainException(
                "A SaleNumber must be allocated and passed to Submit(). " +
                "The application layer should call ISaleNumberGenerator.NextAsync " +
                "immediately before calling Sale.Submit().");
    }

    private static void EnsureCreatedByNameValid(string createdByName)
    {
        if (string.IsNullOrWhiteSpace(createdByName))
            throw new DomainException("Created-by user name is required.");
    }
}