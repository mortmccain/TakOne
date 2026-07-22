using Ardalis.Specification;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Specification that matches EVERY <see cref="Sale"/> — the "no filter" case.
///
/// Used by <c>GetSalesPaginatedQuery</c> when the caller is an admin, manager,
/// or employee (roles that can see all sales). For customer-scoped views, use
/// <see cref="SaleByCreatorSpecification"/> instead.
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
///           : new SaleByCreatorSpecification(currentUserId);
///     </code>
///
///   reads as "if they can see all sales, use the all-sales spec; otherwise
///   restrict to their own". This is clearer than <c>null</c> vs <c>non-null</c>.
///
/// ARDALIS USAGE:
///   Inherits from <c>Ardalis.Specification.Specification&lt;T&gt;</c>. Because
///   we add no <c>Query.Where(...)</c> clauses, the
///   <c>SpecificationEvaluator</c> will translate this to an unfiltered query
///   (just <c>SELECT * FROM Sales</c> plus whatever ordering / pagination the
///   repository adds separately).
/// </summary>
public sealed class AllSalesSpecification : Specification<Sale>
{
    public AllSalesSpecification()
    {
        // No Where clause = match everything. We DO add a default ordering so
        // that pagination is deterministic (without ORDER BY, SQL Server does
        // not guarantee row order across pages — rows could appear on multiple
        // pages or be skipped entirely).
        Query.OrderByDescending(sale => sale.CreatedAtUtc);
    }
}