using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

public interface ISaleRepository
{
    Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync(
        Specification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns ALL matching sales without pagination.
    /// Line items are NOT eagerly loaded — only use where Sale-level fields
    /// (Total, Status, CreatedAtUtc, CreatedByUserId) are sufficient.
    /// </summary>
    Task<List<Sale>> GetAllBySpecificationAsync(
        Specification<Sale> specification,
        CancellationToken cancellationToken = default);

    Task AddAsync(Sale sale, CancellationToken cancellationToken = default);
    Task RemoveAsync(Sale sale, CancellationToken cancellationToken = default);
}

/*
 Why no Update method?

 EF Core tracks changes to entities loaded from the database. When a handler loads a Sale,
modifies it, and calls UnitOfWork.SaveChangesAsync(), 
EF Core automatically detects the changes and generates UPDATE statements.
An explicit Update method is redundant.
 */