using Ardalis.Specification;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Specification that selects only <see cref="Sale"/>s created by a given user.
///
/// Used by <c>GetSalesPaginatedQuery</c> when the caller is a customer (or
/// other non-admin role) so they only see their own sales. Admins and managers
/// get a null specification (no filter), which the repository translates to
/// "return everything".
///
/// The "creator" interpretation (rather than "customer") is deliberate: an
/// employee who creates a sale on behalf of a customer is the creator, and
/// the sale should appear on the employee's "sales I started" list — even
/// though it should also appear on the customer's "purchases made for me"
/// list (driven by a separate <c>SaleByCustomerSpecification</c>, if needed).
///
/// ARDALIS USAGE:
///   Inherits from <c>Ardalis.Specification.Specification&lt;T&gt;</c>, which
///   is the base class for all specifications in this project. The query
///   expression is set in the constructor via <c>Query.Where(...)</c>. The
///   Infrastructure layer's <c>SpecificationEvaluator</c> translates this to
///   a LINQ <c>Where</c> clause on the DbSet — no manual translation needed
///   in each repository.
/// </summary>
public sealed class SaleByCreatorSpecification : Specification<Sale>
{
    public SaleByCreatorSpecification(Guid creatorId)
    {
        // Defensive: a Guid.Empty creator id would silently match every sale
        // whose CreatedByUserId hasn't been set yet (which shouldn't happen,
        // but cheap to guard against).
        if (creatorId == Guid.Empty)
        {
            // TODO: consider a custom exception type for this project,
            // e.g. InvalidSpecificationException.and decide if needed to log the error or just throw.
            throw new ArgumentException
                ("Creator id must be a non-empty Guid.", nameof(creatorId));
        }

        // `Query` is Ardalis's fluent builder. `.Where(...)` adds a filter
        // that the evaluator will translate to SQL. Other fluent methods on
        // `Query` include `.Include(...)`, `.OrderBy(...)`, `.Skip(...).Take(...)`,
        // `.AsNoTracking()`, `.AsSplitQuery()`, etc. — see other specifications
        // in this folder for examples.
        Query.Where(sale => sale.CreatedByUserId == creatorId);

        // PERFORMANCE: list views (where this spec is used) don't need line
        // items. Order by most-recent first so the default pagination shows
        // the latest sales on page 1.
        Query.OrderByDescending(sale => sale.CreatedAtUtc);
    }
}