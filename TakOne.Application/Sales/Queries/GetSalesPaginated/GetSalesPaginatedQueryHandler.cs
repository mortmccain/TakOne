using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

public static class GetSalesPaginatedQueryHandler
{
    public static async Task<PaginatedResult<SaleListItemDto>> Handle
        (
        GetSalesPaginatedQuery query,
        ISaleRepository saleRepository,
        CancellationToken cancellationToken
        )
    {
        // Employees get a creator-scoped spec; admins and managers get everything
        var spec = query.FilterByCreatorId.HasValue
            ? (Specification<Sale>)new SaleByCreatorSpecification(query.FilterByCreatorId.Value)
            : Specification<Sale>.Empty;

        var paginated = await saleRepository.GetPaginatedBySpecificationAsync(
            spec, query.PageNumber, query.PageSize, cancellationToken);

        var dtos = paginated.Items.Select(s => new SaleListItemDto
        {
            Id = s.Id,
            SaleNumber = s.SaleNumber.Value,
            CustomerName = s.CustomerName,
            Status = s.Status.ToString(),
            Total = new MoneyDto { Amount = s.Total.Amount, Currency = s.Total.Currency },
            CreatedAtUtc = s.CreatedAtUtc,
            CreatedByUserId = s.CreatedByUserId,
            CreatedByName = s.CreatedByName
        }).ToList();

        return new PaginatedResult<SaleListItemDto>(
            dtos, paginated.TotalCount, query.PageNumber, query.PageSize);
    }
}