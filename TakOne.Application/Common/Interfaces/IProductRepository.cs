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
    /// READ-ONLY batch load — same as <see cref="GetByIdsAsync"/> but returns
    /// <c>AsNoTracking()</c> entities. USE THIS in handlers that ALSO load or
    /// mutate a <c>Sale</c> (with its owned <c>Money</c> Total) in the same
    /// DbContext — tracking Products alongside SaleLineItems that share the
    /// same <c>Money</c> CLR type as an owned value object causes EF Core's
    /// change tracker to confuse the owned instances and throw
    /// <c>DbUpdateConcurrencyException</c> at SaveChanges.
    ///
    /// The canonical caller is <c>QuickReorderLastSaleCommandHandler</c>:
    /// it loads multiple Products (to snapshot prices + check stock + look up
    /// purchase limits) AND loads/creates the user's Draft Sale (which gets
    /// new SaleLineItems added). Loading the Products AsNoTracking keeps
    /// them out of the change tracker entirely, leaving only the Sale
    /// (which we DO want to mutate + save) in the tracking equation.
    ///
    /// Same defensive semantics as <see cref="GetByIdsAsync"/>: empty input
    /// returns an empty list; missing Ids are absent from the result.
    /// </summary>
    Task<List<Product>> GetByIdsReadOnlyAsync
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

    /// <summary>
    /// Returns the Id of every Product in the catalog (lightweight —
    /// single round-trip, no entity materialization, no tracking).
    ///
    /// USED BY:
    ///   <c>CreateCustomerGroupCommandHandler</c> (Step 5 wiring) — when a
    ///   new CustomerGroup is created, the handler iterates all existing
    ///   product IDs in batches, loads tracked products, calls
    ///   <see cref="Product.SetPurchaseLimit"/> on each, and SaveChanges
    ///   per batch — bulk-applying the default limit (1) to every product
    ///   for the new group.
    ///
    /// WHY NOT A REPO-LEVEL BULK METHOD:
    ///   The batching loop needs to call <see cref="IUnitOfWork.SaveChangesAsync"/>
    ///   and <see cref="IUnitOfWork.ClearChangeTracker"/> between batches —
    ///   both of which live on IUnitOfWork, not on the repository. Putting
    ///   the loop in the handler keeps the repository focused on queries and
    ///   lets the handler orchestrate the per-batch save+clear cycle.
    ///
    /// SCALE CONSIDERATIONS:
    ///   This returns a <c>List&lt;Guid&gt;</c> — for a catalog with ~10K
    ///   products, that's ~160KB of Guids (16 bytes each × 10K). Acceptable.
    ///   For 100K+ products, switch to a streaming <c>IAsyncEnumerable&lt;Guid&gt;</c>
    ///   and process in smaller batches.
    /// </summary>
    Task<List<Guid>> GetAllProductIdsAsync(CancellationToken cancellationToken = default);
}