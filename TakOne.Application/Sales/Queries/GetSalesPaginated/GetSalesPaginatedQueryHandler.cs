using Ardalis.Specification;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

/// <summary>
/// Handler for <see cref="GetSalesPaginatedQuery"/>.
///
/// Round 4 (server-driven paging): EVERY filter and the sort now ride
/// inside the SQL query via the specification — the handler is a thin
/// auth + clamp + pass-through + DTO-projection shell. The pre-Round-4
/// in-memory search (which filtered the materialized page, breaking
/// TotalCount and missing rows beyond page 1) is gone; MobileSearch's
/// legacy <c>SearchTerm</c> is now a server-side OR predicate.
/// </summary>
public sealed class GetSalesPaginatedQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<SaleListItemDto>> HandleAsync(
        GetSalesPaginatedQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        ILogger<GetSalesPaginatedQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetSalesPaginated: unauthenticated call rejected.");

            // Return an empty page rather than a Result.Failure because the
            // signature is PaginatedResult<T>, not Result<PaginatedResult<T>>.
            // The caller is expected to interpret an empty page as "no data"
            // and the warning log captures the auth failure for ops.
            return new PaginatedResult<SaleListItemDto>(
                Array.Empty<SaleListItemDto>(), 0, 1, 1);
        }

        // ------------------------------------------------------------------
        // 1. Decide the spec. Admins/Managers/Employees/ReadOnly see
        //    everything (staff audit view); customers see only sales in
        //    which they are the Customer (my-purchases view — includes
        //    on-behalf purchases staff made for them, see
        //    SaleByCustomerSpecification). ReadOnly is a staff role whose
        //    entire purpose is to audit sales without modifying them, so
        //    it MUST be in the "see all" branch — falling through to a
        //    customer-role spec would hide every sale created by anyone
        //    else (and ReadOnly users by definition never buy), leaving
        //    them with an empty grid.
        // ------------------------------------------------------------------
        var canSeeAllSales =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee) ||
            currentUser.IsInRole(Roles.ReadOnly);

        // `ISpecification<Sale>` is the Ardalis interface. The repository's
        // SpecificationEvaluator translates whichever spec we hand it into
        // the appropriate LINQ query against the Sales DbSet.
        //
        // Round 4: the column filters + sort are packed into ONE
        // SalesListFilters aggregate; the WHERE/ORDER BY clauses live in
        // the shared SalesSpecificationFilters helper so the staff and
        // customer specs can never drift apart. TotalCount is now
        // accurate for EVERY active filter (the pre-Round-4 note about
        // search breaking the count no longer applies — the search is in
        // SQL too).
        var filters = new SalesListFilters(
            SaleNumberTerm: query.SaleNumberTerm,
            CustomerName: query.CustomerNameFilter,
            CreatedByName: query.CreatedByNameFilter,
            Total: query.TotalFilter,
            SortBy: query.SortBy,
            SortDescending: query.SortDescending);

        ISpecification<Sale> spec = canSeeAllSales
            ? new AllSalesSpecification(
                query.Status, query.FromDateUtc, query.ToDateUtc,
                query.SearchTerm, filters)
            : new SaleByCustomerSpecification(
                currentUser.UserId, query.Status, query.FromDateUtc, query.ToDateUtc,
                query.SearchTerm, filters);

        // ------------------------------------------------------------------
        // 2. Clamp page parameters to safe values. Negative or zero page
        //    numbers/sizes would produce nonsensical SQL (OFFSET -1 ...) or
        //    return everything in one page.
        // ------------------------------------------------------------------
        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // ------------------------------------------------------------------
        // 3. Load the page from the repository (filters + ORDER BY are
        //    already inside the spec → SQL).
        // ------------------------------------------------------------------
        var paginated = await saleRepository.GetPaginatedBySpecificationAsync(
            spec, pageNumber, pageSize, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Project to DTO.
        // ------------------------------------------------------------------
        var dtos = paginated.Items
            .Select(s => new SaleListItemDto
            {
                Id = s.Id,
                SaleNumber = s.SaleNumber?.Value,
                CustomerName = s.CustomerName,
                Status = s.Status.ToString(),
                Total = new MoneyDto
                {
                    Amount = s.Total.Amount,
                    Currency = s.Total.Currency
                },
                CreatedAtUtc = s.CreatedAtUtc,
                CreatedByUserId = s.CreatedByUserId,
                CreatedByName = s.CreatedByName
            })
            .ToList();

        return new PaginatedResult<SaleListItemDto>(
            dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}