using TakOne.Domain.Sales.Entities;
using Ardalis.Specification;

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

    /// <summary>
    /// Returns a paginated slice of Sales matching the given specification.
    ///
    /// The caller (a query handler) builds an <see cref="ISpecification{Sale}"/>
    /// (e.g. <c>SaleByCreatorSpecification</c>) and passes it here. The
    /// Infrastructure layer's <c>SpecificationEvaluator</c> translates the
    /// spec into a LINQ query against the <c>Sales</c> DbSet — including
    /// any <c>Where</c>, <c>OrderBy</c>, <c>Include</c> clauses declared
    /// by the specification.
    ///
    /// Pass <c>Specification&lt;Sale&gt;.Empty</c> (or a null-spec) to get
    /// an unfiltered, paginated list — typically only for admin/manager views.
    /// </summary>
    Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync
        (
        ISpecification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns ALL matching sales without pagination.
    /// Line items are NOT eagerly loaded — call
    /// <see cref="GetByIdWithLineItemsAsync"/> per sale if you need them.
    /// </summary>
    Task<List<Sale>> GetAllBySpecificationAsync
        (
        ISpecification<Sale> specification,
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
