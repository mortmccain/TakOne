using TakOne.Domain.Products.Events;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Domain.Products.ValueObjects;

namespace TakOne.Domain.Products.Entities;

/// <summary>
/// Aggregate root for products in the catalog.
///
/// RESPONSIBILITIES:
///   - Holds the product's identity, description, price, stock, category refs, picture.
///   - Owns a collection of <see cref="CustomerGroupPurchaseLimit"/> value objects
///     that define per-group purchase limits for THIS product.
///   - Enforces stock-related invariants (can't go negative, can't increase by 0 or negative).
///   - Tracks active/inactive state via <see cref="IsActive"/> (soft-delete).
///   - Raises domain events on every state change so the application layer
///     can react to product lifecycle changes (creation, stock adjustments,
///     activation/deactivation, detail updates) without polling.
///
/// DOES NOT ENFORCE:
///   - That a buyer's quantity respects their group's limit. That enforcement
///     happens in the Sale aggregate (step 5), because it requires knowing
///     the buyer's GroupName (which lives on the User aggregate). The
///     application layer loads the Product, looks up the limit via
///     <see cref="GetPurchaseLimitForGroup"/>, and passes it to the Sale.
///
/// DOES NOT ENFORCE:
///   - That SubCategoryId belongs to CategoryId, or that SubSubCategoryId
///     belongs to SubCategoryId. That hierarchy invariant lives in the
///     Category aggregate (step 4) and is checked by the application layer
///     when creating/updating a Product.
/// </summary>
public sealed class Product : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<CustomerGroupPurchaseLimit> _purchaseLimits = new();



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    public string Name { get; private set; }
    public string Description { get; private set; }

    /// <summary>
    /// URL or relative path to the product's picture. Nullable because a product
    /// may be created without a picture and have one added later.
    /// </summary>
    public string? PictureUrl { get; private set; }

    public Money Price { get; private set; }

    public int StockQuantity { get; private set; }

    /// <summary>
    /// Soft-delete flag. <c>true</c> by default (product is active and visible
    /// in shop listings). <c>false</c> after <see cref="Deactivate"/> is called.
    /// Inactive products are retained for audit but excluded from shop queries
    /// and cannot be added to carts. This aligns Product with the soft-delete
    /// pattern used by Category, User, CustomerGroup, SubCategory, SubSubCategory.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Required reference to the top-level Category aggregate.
    /// Every product must belong to at least a Category.
    /// </summary>
    public Guid CategoryId { get; private set; }

    /// <summary>
    /// Optional reference to a SubCategory. Null if the product is only
    /// categorized at the top level.
    /// </summary>
    public Guid? SubCategoryId { get; private set; }

    /// <summary>
    /// Optional reference to a SubSubCategory. Null if the product is only
    /// categorized at the Category or SubCategory level.
    /// </summary>
    public Guid? SubSubCategoryId { get; private set; }

    /// <summary>
    /// Read-only view of the per-group purchase limits for this product.
    /// External code cannot modify this collection directly — use
    /// <see cref="SetPurchaseLimit"/> / <see cref="RemovePurchaseLimit"/>.
    /// </summary>
    public IReadOnlyList<CustomerGroupPurchaseLimit> PurchaseLimits
        => _purchaseLimits.AsReadOnly();



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private Product() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// </summary>
    private Product(
        string name,
        string description,
        Money price,
        int stockQuantity,
        string? pictureUrl,
        Guid categoryId,
        Guid? subCategoryId,
        Guid? subSubCategoryId) : base(Guid.NewGuid())
    {
        EnsureNameValid(name);
        EnsureDescriptionValid(description);
        EnsurePriceValid(price);
        EnsureStockQuantityValid(stockQuantity);
        EnsureCategoryIdValid(categoryId);
        EnsureSubCategoryConsistency(subCategoryId, subSubCategoryId);

        Name = name;
        Description = description;
        Price = price;
        StockQuantity = stockQuantity;
        PictureUrl = pictureUrl;
        CategoryId = categoryId;
        SubCategoryId = subCategoryId;
        SubSubCategoryId = subSubCategoryId;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new Product. This is the ONLY way to construct a Product
    /// from application code.
    ///
    /// Category hierarchy validation (sub belongs to category, etc.) is NOT
    /// done here — it's a cross-aggregate invariant that the application layer
    /// enforces by loading the Category aggregate before calling this method.
    /// </summary>
    public static Product Create
        (
        string name,
        string description,
        Money price,
        int stockQuantity,
        Guid categoryId,
        string? pictureUrl = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null
        )
    {
        var product = new Product
            (
            name,
            description,
            price,
            stockQuantity,
            pictureUrl,
            categoryId,
            subCategoryId,
            subSubCategoryId
            );

        // Raise a ProductCreatedDomainEvent so the application layer can
        // invalidate catalog caches, push search-index entries, etc.
        product.AddDomainEvent(new ProductCreatedDomainEvent(
            product.Id,
            product.Name,
            product.CategoryId,
            product.Price,
            product.StockQuantity));

        return product;
    }



    // ==================================================================================================================================
    //                                                          DETAIL / CATEGORY UPDATES
    // ==================================================================================================================================



    /// <summary>
    /// Updates the product's basic descriptive fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>VALUE OBJECT IMMUTABILITY — REFERENCE REPLACEMENT IS CORRECT:</b>
    /// Money is an immutable value object mapped as a
    /// <c>ComplexProperty</c> (not <c>OwnsOne</c>) on
    /// <c>Product.Price</c>. <c>ComplexProperty</c> has value semantics:
    /// EF Core compares instances by value (via
    /// <c>GetEqualityComponents</c>), not by reference identity. Replacing
    /// the reference is the idiomatic mutation pattern — EF detects the
    /// value change and generates a clean UPDATE against the parent row.
    /// </para>
    /// <para>
    /// The previous mapping (<c>OwnsOne</c>) tracked Money by reference
    /// identity, so replacing the reference produced the dreaded
    /// <c>DbUpdateConcurrencyException: expected to affect 1 row(s), but
    /// actually affected 0 row(s)</c>. The migration to
    /// <c>ComplexProperty</c> fixes this at the EF Core mapping level —
    /// no domain mutation hack required.
    /// </para>
    /// </remarks>
    public void UpdateDetails(string name, string description, Money price, string? pictureUrl)
    {
        EnsureNameValid(name);
        EnsureDescriptionValid(description);
        EnsurePriceValid(price);

        // Capture the BEFORE state for the event BEFORE mutating.
        var previousName = Name;
        var previousPrice = Price;

        Name = name;
        Description = description;
        Price = price;
        PictureUrl = pictureUrl;

        AddDomainEvent(new ProductDetailsUpdatedDomainEvent(
            Id,
            previousName,
            Name,
            previousPrice,
            Price));
    }

    /// <summary>
    /// Updates the product's category assignment.
    /// Pass null for subCategoryId / subSubCategoryId to clear them.
    /// Cross-aggregate hierarchy validation is done by the application layer.
    /// </summary>
    public void UpdateCategory
        (
        Guid categoryId,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null
        )
    {
        EnsureCategoryIdValid(categoryId);
        EnsureSubCategoryConsistency(subCategoryId, subSubCategoryId);

        CategoryId = categoryId;
        SubCategoryId = subCategoryId;
        SubSubCategoryId = subSubCategoryId;
    }



    // ==================================================================================================================================
    //                                                          STOCK MANAGEMENT
    // ==================================================================================================================================



    /// <summary>
    /// Increases the stock by the given quantity. Used when restocking.
    /// </summary>
    public void IncreaseStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity to increase must be greater than zero.");

        var previous = StockQuantity;
        StockQuantity += quantity;
        AddDomainEvent(new ProductStockAdjustedDomainEvent(
            Id, previous, StockQuantity, reason: "restock"));
    }

    /// <summary>
    /// Decreases the stock by the given quantity. Used when a sale is approved.
    /// Throws if the resulting stock would be negative.
    /// </summary>
    public void DecreaseStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity to decrease must be greater than zero.");

        if (quantity > StockQuantity)
            throw new DomainException("Insufficient stock to remove the specified quantity.");

        var previous = StockQuantity;
        StockQuantity -= quantity;
        AddDomainEvent(new ProductStockAdjustedDomainEvent(
            Id, previous, StockQuantity, reason: "sale approved"));
    }

    /// <summary>
    /// Sets the stock to the given quantity. Used for manual adjustments
    /// AND by <see cref="Deactivate"/> to zero out stock as part of the
    /// soft-delete flow. Accepts 0 (the deactivation flow requires it);
    /// for the staff "Set stock" UI feature that must reject 0, use
    /// <see cref="AdjustStockTo"/> instead.
    /// </summary>
    public void SetStock(int quantity)
    {
        EnsureStockQuantityValid(quantity);
        var previous = StockQuantity;
        StockQuantity = quantity;
        AddDomainEvent(new ProductStockAdjustedDomainEvent(
            Id, previous, StockQuantity, reason: "manual set"));
    }

    /// <summary>
    /// Sets the stock to the EXACT given quantity, with a stricter guard
    /// than <see cref="SetStock"/>: quantity must be strictly positive
    /// (≥ 1). Used by the staff "Set stock" UI on ProductDetail.razor,
    /// which lets staff set stock to an absolute number rather than
    /// adding to it.
    ///
    /// WHY THIS EXISTS (separate from <see cref="SetStock"/>):
    ///   <see cref="SetStock"/> accepts 0 because the DEACTIVATION flow
    ///   (DeactivateProductCommandHandler) calls Deactivate() which
    ///   internally zeros out stock via SetStock(0). The "Set stock" UI
    ///   feature, however, must NOT allow 0 — per the user spec, "setting
    ///   to negative or zero is not possible; to make it zero they should
    ///   deactivate the product". This method enforces that invariant at
    ///   the domain level (defense-in-depth: the command validator ALSO
    ///   rejects ≤ 0, but the domain never trusts the caller).
    /// </summary>
    public void AdjustStockTo(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");

        var previous = StockQuantity;
        StockQuantity = quantity;
        AddDomainEvent(new ProductStockAdjustedDomainEvent(
            Id, previous, StockQuantity, reason: "manual adjust"));
    }



    // ==================================================================================================================================
    //                                                          ACTIVATION LIFECYCLE
    // ==================================================================================================================================



    /// <summary>
    /// Deactivates the product (soft-delete). Sets <see cref="IsActive"/>
    /// to false and zeros out the stock (since an inactive product is
    /// not for sale, holding stock against it is meaningless).
    ///
    /// Idempotent — calling Deactivate on an already-inactive product is
    /// a no-op (does not raise events, does not emit a stock-adjusted
    /// event with previous=0, new=0). This prevents spurious audit
    /// entries when an admin double-clicks the deactivate button.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive)
            return;

        var stockBefore = StockQuantity;

        // Zero out stock — an inactive product cannot hold inventory.
        // Uses SetStock(0) (which has its own guard that allows 0) and
        // captures the previous quantity for the audit event.
        if (stockBefore > 0)
        {
            StockQuantity = 0;
            AddDomainEvent(new ProductStockAdjustedDomainEvent(
                Id, stockBefore, StockQuantity, reason: "deactivation"));
        }

        IsActive = false;
        AddDomainEvent(new ProductDeactivatedDomainEvent(Id, stockBefore));
    }

    /// <summary>
    /// Reactivates a previously-deactivated product. Sets
    /// <see cref="IsActive"/> to true. Does NOT restore stock — the
    /// admin must explicitly restock via <see cref="IncreaseStock"/>
    /// or <see cref="SetStock"/> after reactivation. This makes the
    /// reactivation flow auditable: there is no implicit "guess the
    /// previous stock level" behavior.
    ///
    /// Idempotent — calling Activate on an already-active product is a no-op.
    /// </summary>
    public void Activate()
    {
        if (IsActive)
            return;

        IsActive = true;
        AddDomainEvent(new ProductActivatedDomainEvent(Id, StockQuantity));
    }



    // ==================================================================================================================================
    //                                                          PURCHASE LIMIT MANAGEMENT
    // ==================================================================================================================================



    /// <summary>
    /// Sets (adds or replaces) the purchase limit for the given group.
    /// Because <see cref="CustomerGroupPurchaseLimit"/> is a value object (immutable),
    /// "changing" a limit means replacing the old instance with a new one.
    /// </summary>
    public void SetPurchaseLimit(Guid groupId, int limit)
    {
        if (groupId == Guid.Empty)
            throw new DomainException("Group Id is required to set a purchase limit.");

        var newLimit = CustomerGroupPurchaseLimit.Create(groupId, limit);

        // Remove any existing limit for the same group, then add the new one.
        // (Equality is by GroupId + Limit, so we filter on GroupId only.)
        var existing = _purchaseLimits.FirstOrDefault(l => l.GroupId == groupId);
        if (existing is not null)
        {
            _purchaseLimits.Remove(existing);
        }

        _purchaseLimits.Add(newLimit);
    }

    /// <summary>
    /// Removes the purchase limit for the given group, if any.
    /// Idempotent — no-op if the limit doesn't exist.
    /// </summary>
    public void RemovePurchaseLimit(Guid groupId)
    {
        if (groupId == Guid.Empty)
            throw new DomainException("Group Id is required to remove a purchase limit.");

        var existing = _purchaseLimits.FirstOrDefault(l => l.GroupId == groupId);
        if (existing is not null)
        {
            _purchaseLimits.Remove(existing);
        }
    }

    /// <summary>
    /// Returns the purchase limit for the given group, or null if no limit
    /// is defined for that group on this product.
    /// Called by the application layer (via <c>IPurchaseLimitPolicy</c>)
    /// when checking a purchase.
    /// </summary>
    public CustomerGroupPurchaseLimit? GetPurchaseLimitForGroup(Guid groupId)
    {
        if (groupId == Guid.Empty)
            return null;

        return _purchaseLimits.FirstOrDefault(l => l.GroupId == groupId);
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureNameValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("name is required.");

        if (name.Length > 200)
            throw new DomainException("name cannot exceed 200 characters.");
    }

    private static void EnsureDescriptionValid(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException("Product description is required.");

        if (description.Length > 2000)
            throw new DomainException("Product description cannot exceed 2000 characters.");
    }

    private static void EnsurePriceValid(Money price)
    {
        // Defense-in-depth check: the Money ctor already rejects negative
        // amounts (throws ArgumentOutOfRangeException), so for newly-
        // constructed Money this branch is dead. But EF Core's
        // ComplexProperty materialization uses the parameterless ctor
        // and can populate an invalid state from corrupted DB rows —
        // this guard catches that path.
        if (price.Amount < 0)
            throw new DomainException("Product price cannot be negative.");
    }

    private static void EnsureStockQuantityValid(int stockQuantity)
    {
        if (stockQuantity < 0)
            throw new DomainException("Stock quantity cannot be negative.");
    }

    private static void EnsureCategoryIdValid(Guid categoryId)
    {
        if (categoryId == Guid.Empty)
            throw new DomainException("Category ID is required.");
    }

    /// <summary>
    /// Lightweight intra-product consistency check: you can't have a SubSubCategory
    /// without a SubCategory. (Cross-aggregate hierarchy validation is done by
    /// the application layer via the Category aggregate.)
    /// </summary>
    private static void EnsureSubCategoryConsistency(Guid? subCategoryId, Guid? subSubCategoryId)
    {
        if (subSubCategoryId is not null && subCategoryId is null)
        {
            throw new DomainException("Cannot assign a SubSubCategory without a SubCategory.");
        }
    }
}
