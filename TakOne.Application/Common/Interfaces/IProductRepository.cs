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

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a product with the given name already exists.
    /// Used by validators to enforce name uniqueness at the application layer.
    /// </summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);

    Task AddAsync(Product product, CancellationToken cancellationToken = default);
}