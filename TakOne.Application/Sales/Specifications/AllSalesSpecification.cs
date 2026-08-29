using Ardalis.Specification;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Specification that matches EVERY <see cref="Sale"/> — the "no filter" case.
///
/// Used by <c>GetSalesPaginatedQuery</c> when the caller is an admin, manager,
/// or employee (roles that can see all sales). For customer-scoped views, use
/// <see cref="SaleByCustomerSpecification"/> instead.
///
/// WHY THIS EXISTS INSTEAD OF `null`:
///   <c>ISaleRepository.GetPaginatedBySpecificationAsync</c> accepts an
///   <c>ISpecification&lt;Sale&gt;</c>. Passing <c>null</c> would force every
///   caller (and the repository implementation) to null-check. A dedicated
///   "match everything" specification keeps the contract non-nullable and
///   makes the caller's intent explicit at the call site:
///
///     <code>
///       ISpecification&lt;Sale&gt; spec = canSeeAllSales
///           ? new AllSalesSpecification()
///           : new SaleByCustomerSpecification(currentUserId);
///     </code>
///
///   reads as "if they can see all sales, use the all-sales spec; otherwise
///   restrict to their own". This is clearer than <c>null</c> vs <c>non-null</c>.
///
/// OPTIONAL STATUS FILTER:
///   The constructor accepts an optional <see cref="SaleStatus"/> filter. When
///   supplied, a <c>Query.Where(sale => sale.Status == status)</c> clause is
///   added. When null (the default), no status filter is applied — preserving
///   the previous "match everything" semantics. This lets
///   <c>GetSalesPaginatedQueryHandler</c> push the status filter down to SQL
///   instead of filtering in-memory after the page is loaded (Phase 7 item E).
///
/// ARDALIS USAGE:
///   Inherits from <c>Ardalis.Specification.Specification&lt;T&gt;</c>. The
///   <c>SpecificationEvaluator</c> translates whichever Where clauses we add
///   into the appropriate LINQ query against the Sales DbSet.
/// </summary>
public sealed class AllSalesSpecification : Specification<Sale>
{
    public AllSalesSpecification() : this(status: null) { }

    public AllSalesSpecification(SaleStatus? status)
        : this(status, fromUtc: null, toUtcExclusive: null) { }

    public AllSalesSpecification(
        SaleStatus? status,
        DateTime? fromUtc,
        DateTime? toUtcExclusive)
        : this(status, fromUtc, toUtcExclusive, searchTerm: null, filters: null) { }

    /// <summary>
    /// Round 4 (server-driven paging) constructor: the full filter +
    /// sort surface. <paramref name="searchTerm"/> is the legacy
    /// cross-column OR search (MobileSearch); <paramref name="filters"/>
    /// carries the per-column filters and the sort key/direction.
    /// </summary>
    public AllSalesSpecification(
        SaleStatus? status,
        DateTime? fromUtc,
        DateTime? toUtcExclusive,
        string? searchTerm,
        SalesListFilters? filters)
    {
        if (status.HasValue)
        {
            Query.Where(sale => sale.Status == status.Value);
        }

        // Optional creation-date range (Round 3) — half-open interval
        // [fromUtc, toUtcExclusive), pushed down to SQL like the status
        // filter so TotalCount stays accurate. Either bound may be null
        // (open-ended). Both are UTC; the caller (the query handler)
        // normalizes them BEFORE constructing the spec.
        if (fromUtc.HasValue)
        {
            Query.Where(sale => sale.CreatedAtUtc >= fromUtc.Value);
        }

        if (toUtcExclusive.HasValue)
        {
            Query.Where(sale => sale.CreatedAtUtc < toUtcExclusive.Value);
        }

        // Round 4: cross-column search + per-column filters + sort all
        // live in the shared helper so this spec and
        // SaleByCustomerSpecification can never drift apart.
        Query.ApplySearchTerm(searchTerm);
        Query.ApplyColumnFilters(filters);
        Query.ApplySort(filters?.SortBy, filters?.SortDescending ?? true);
    }
}