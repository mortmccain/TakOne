using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

public interface ISaleRepository
{
    Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a Sale with its line items eagerly. Required for any operation
    /// that needs to inspect or modify the lines.
    /// </summary>
    Task<Sale?> GetByIdWithLineItemsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync
        (
        Specification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns ALL matching sales without pagination.
    /// Line items are NOT eagerly loaded.
    /// </summary>
    Task<List<Sale>> GetAllBySpecificationAsync
        (
        Specification<Sale> specification,
        CancellationToken cancellationToken = default
        );

    Task AddAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a Sale. ONLY valid for Sales in Draft status — the domain
    /// Sale.Cancel() throws for Drafts, so draft disposal goes through here.
    /// The repository implementation may add a defensive check that the Sale
    /// is actually a Draft before issuing the DELETE.
    /// </summary>
    Task DeleteAsync(Sale sale, CancellationToken cancellationToken = default);
}
