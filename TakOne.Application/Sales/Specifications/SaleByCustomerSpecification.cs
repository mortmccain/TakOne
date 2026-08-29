using Ardalis.Specification;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Specification that selects only <see cref="Sale"/>s in which the given
/// user is the <b>customer</b> (i.e. the sale is FOR them, regardless of who
/// physically created it).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS (vs. filtering on <c>CreatedByUserId</c>)</b>:
/// The <c>Sale</c> aggregate distinguishes two parties:
/// <list type="bullet">
///   <item><c>CustomerId</c> — the user the sale is FOR (the buyer).</item>
///   <item><c>CreatedByUserId</c> — the user who physically created the sale
///         (the seller / data-entry person).</item>
/// </list>
/// For a self-serve purchase these are the same user. But the sale-creation
/// flow (<c>CreateSaleCommand</c>) explicitly supports <b>on-behalf</b>
/// purchases: a staff member (Employee/Manager/Admin) may create a sale on
/// behalf of a customer, in which case <c>CreatedByUserId = staff.Id</c>
/// and <c>CustomerId = customer.Id</c>.
/// </para>
/// <para>
/// A customer browsing their <c>/Sales</c> list, their sale-detail view, or
/// their shop-stats card wants to see purchases made FOR them — which
/// includes both self-serve purchases AND on-behalf purchases staff made
/// for them. Filtering on <c>CreatedByUserId</c> (the pre-Round-14 behavior)
/// excluded on-behalf purchases from the very customer they were made for —
/// a real bug. This spec filters on <c>CustomerId</c> to fix that.
/// </para>
/// <para>
/// <b>STAFF VIEW IS UNAFFECTED</b>: staff roles (Admin/Manager/Employee/
/// ReadOnly) use <see cref="AllSalesSpecification"/> (no filter) so they
/// see every sale for audit/approval/invoicing. This spec is only
/// constructed on the customer-role branch.
/// </para>
/// <para>
/// <b>OPTIONAL STATUS FILTER</b>: the constructor accepts an optional
/// <see cref="SaleStatus"/>. When supplied, an additional
/// <c>Query.Where(sale => sale.Status == status)</c> clause is added
/// alongside the customer filter. When null (the default), only the
/// customer filter is applied.
/// </para>
/// <para>
/// <b>ARDALIS USAGE</b>: inherits from
/// <c>Ardalis.Specification.Specification&lt;T&gt;</c>; the Infrastructure
/// layer's <c>SpecificationEvaluator</c> translates <c>Query.Where(...)</c>
/// into a SQL WHERE clause on the <c>Sales</c> DbSet.
/// </para>
/// </remarks>
public sealed class SaleByCustomerSpecification : Specification<Sale>
{
    public SaleByCustomerSpecification(Guid customerId) : this(customerId, status: null) { }

    public SaleByCustomerSpecification(Guid customerId, SaleStatus? status)
        : this(customerId, status, fromUtc: null, toUtcExclusive: null) { }

    public SaleByCustomerSpecification(
        Guid customerId,
        SaleStatus? status,
        DateTime? fromUtc,
        DateTime? toUtcExclusive)
        : this(customerId, status, fromUtc, toUtcExclusive, searchTerm: null, filters: null) { }

    /// <summary>
    /// Round 4 (server-driven paging) constructor: the full filter +
    /// sort surface. Mirrors <see cref="AllSalesSpecification"/>'s
    /// Round 4 constructor — the WHERE/ORDER BY clauses come from the
    /// shared <c>SalesSpecificationFilters</c> helper so the two specs
    /// can never drift apart.
    /// </summary>
    public SaleByCustomerSpecification(
        Guid customerId,
        SaleStatus? status,
        DateTime? fromUtc,
        DateTime? toUtcExclusive,
        string? searchTerm,
        SalesListFilters? filters)
    {
        // Defensive: a Guid.Empty customer id would silently match every
        // sale whose CustomerId hasn't been set yet (which shouldn't happen,
        // but cheap to guard against).
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException(
                "Customer id must be a non-empty Guid.", nameof(customerId));
        }

        // Filter on CustomerId (the buyer), NOT CreatedByUserId (the
        // seller). This is what makes on-behalf purchases visible to the
        // customer — see the class-level remarks.
        Query.Where(sale => sale.CustomerId == customerId);

        if (status.HasValue)
        {
            Query.Where(sale => sale.Status == status.Value);
        }

        // Optional creation-date range (Round 3) — half-open interval
        // [fromUtc, toUtcExclusive), pushed down to SQL. Same semantics
        // as AllSalesSpecification's identical block.
        if (fromUtc.HasValue)
        {
            Query.Where(sale => sale.CreatedAtUtc >= fromUtc.Value);
        }

        if (toUtcExclusive.HasValue)
        {
            Query.Where(sale => sale.CreatedAtUtc < toUtcExclusive.Value);
        }

        // Round 4: cross-column search + per-column filters + sort all
        // live in the shared helper (same call sites as
        // AllSalesSpecification — deliberately identical).
        Query.ApplySearchTerm(searchTerm);
        Query.ApplyColumnFilters(filters);
        Query.ApplySort(filters?.SortBy, filters?.SortDescending ?? true);
    }
}
