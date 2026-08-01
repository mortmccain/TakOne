using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads multiple products by Id in a SINGLE round-trip. Used by query
    /// handlers that need to enrich a list of items with product data —
    /// e.g. <c>GetActiveCartForUserQueryHandler</c> needs the live
    /// <see cref="Product.StockQuantity"/> for each line in the user's cart.
    ///
    /// Returns tracked entities (same policy as <see cref="GetByIdAsync"/>).
    /// Ids that don't exist in the database are simply absent from the
    /// returned list — the caller should handle missing products defensively
    /// (log + treat stock as 0, or skip the line).
    ///
    /// Empty input returns an empty list without hitting the DB.
    /// </summary>
    Task<List<Product>> GetByIdsAsync
        (
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns a paginated list of products, optionally filtered by category.
    /// Used by the customer-facing shop view.
    /// </summary>
    Task<PaginatedResult<Product>> GetPaginatedAsync
        (
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null,
        string? searchTerm = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Loads a single Product by Id for READ-ONLY use — the returned entity
    /// is NOT tracked by EF Core's change tracker (<c>AsNoTracking()</c>).
    ///
    /// USE THIS when the handler only READS from the Product (name, price,
    /// stock, purchase limits) and never mutates it. The classic case is
    /// <c>CreateOrAppendSaleCommandHandler</c>: it loads the Product to
    /// snapshot the price into a SaleLineItem, then never modifies the
    /// Product. Tracking the Product in that handler is harmful because:
    ///
    ///   - EF Core tracks Product's owned <c>Money</c> Price as
    ///     <c>Product.Price#Money</c>. The SAME <c>Money</c> CLR type is
    ///     also used as the owned type on <c>SaleLineItem.UnitPrice</c> and
    ///     <c>Sale.Total</c>. Having multiple tracked Money instances across
    ///     different owners in the same DbContext can confuse the change
    ///     tracker and produce <c>DbUpdateConcurrencyException</c> at
    ///     SaveChanges ("expected to affect 1 row(s), but actually affected
    ///     0 row(s)").
    ///   - AsNoTracking reads are also slightly faster (no snapshotting,
    ///     no change detection) — small win, but the correctness motivation
    ///     above is the real reason.
    ///
    /// USE <see cref="GetByIdAsync"/> INSTEAD when the handler will MUTATE
    /// the Product (e.g. <c>IncreaseProductStockCommandHandler</c>,
    /// <c>UpdateProductDetailsCommandHandler</c>,
    /// <c>SetProductPurchaseLimitCommandHandler</c>). Tracked entities are
    /// required for EF Core to detect the mutations and generate UPDATEs.
    /// </summary>
    Task<Product?> GetByIdReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a product with the given name already exists.
    /// Used by validators to enforce name uniqueness at the application layer.
    /// </summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of products with <c>StockQuantity &gt; 0</c>.
    /// Used by the shop page's "تعداد اجناس موجود" (in-stock product count)
    /// stat card. Single round-trip <c>SELECT COUNT(*) WHERE StockQuantity &gt; 0</c>.
    /// </summary>
    Task<int> CountInStockAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Product product, CancellationToken cancellationToken = default);
}