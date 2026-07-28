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
/// NOTE on the repository contract: <see cref="ISaleRepository"/> currently
/// exposes <c>GetPaginatedBySpecificationAsync</c> but does NOT accept a
/// search term. Search-term filtering is therefore applied here on the
/// materialized page (after the DB returns). For small page sizes (≤100),
/// this is fine. If we ever need true server-side search, we'll add a
/// <c>GetPaginatedWithSearchAsync</c> method to the repository.
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
        // 1. Decide the spec. Admins/Managers/Employees see everything;
        //    customers and read-only users see only sales they created.
        // ------------------------------------------------------------------
        var canSeeAllSales =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        // `ISpecification<Sale>` is the Ardalis interface. The repository's
        // SpecificationEvaluator translates whichever spec we hand it into
        // the appropriate LINQ query against the Sales DbSet.
        //
        // The optional Status filter (Phase 7 item E) is pushed down into
        // the spec so it becomes part of the SQL WHERE clause — accurate
        // TotalCount, no in-memory filtering, scales beyond one page.
        ISpecification<Sale> spec = canSeeAllSales
            ? new AllSalesSpecification(query.Status)
            : new SaleByCreatorSpecification(currentUser.UserId, query.Status);

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
        // 3. Load the page from the repository.
        // ------------------------------------------------------------------
        var paginated = await saleRepository.GetPaginatedBySpecificationAsync(
            spec, pageNumber, pageSize, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Project to DTO. If a search term was supplied, apply it here
        //    (see class-level comment about why we filter in-memory).
        //    The search matches SaleNumber OR CustomerName, case-insensitive.
        // ------------------------------------------------------------------
        var searchTerm = query.SearchTerm?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(searchTerm);

        var dtos = paginated.Items
            .Where(s => !hasSearch ||
                        (s.SaleNumber.Value.Contains(searchTerm!, StringComparison.OrdinalIgnoreCase) ||
                         s.CustomerName.Contains(searchTerm!, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new SaleListItemDto
            {
                Id = s.Id,
                SaleNumber = s.SaleNumber.Value,
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

        // NOTE: when search term filters rows out in-memory, TotalCount is
        // no longer accurate (it's the pre-search count). For the v1 UI this
        // is acceptable — the search box reloads the page anyway. If we ever
        // add infinite scroll, we'll need server-side search first.

        return new PaginatedResult<SaleListItemDto>(
            dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}