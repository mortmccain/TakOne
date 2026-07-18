using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetSalebyId;

public static class GetSaleByIdQueryHandler
{
    public static async Task<Result<SaleDto>> Handle
        (
        GetSaleByIdQuery query,
        ISaleRepository saleRepository,
        CancellationToken cancellationToken
        )
    {
        var sale = await saleRepository.GetByIdAsync(query.SaleId, cancellationToken);

        if (sale is null)
            return Result<SaleDto>.Failure($"Sale '{query.SaleId}' was not found.");

        bool isAdminOrManager =
            query.UserRoles.Contains("Admin") || query.UserRoles.Contains("Manager");

        if (!isAdminOrManager && sale.CreatedByUserId != query.RequestedByUserId)
            return Result<SaleDto>.Failure("You do not have permission to view this sale.");

        var dto = new SaleDto
        {
            Id = sale.Id,
            SaleNumber = sale.SaleNumber.Value,
            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,
            Status = sale.Status.ToString(),

            CreatedByUserId = sale.CreatedByUserId,
            CreatedByName = sale.CreatedByName,

            Total = new MoneyDto { Amount = sale.Total.Amount, Currency = sale.Total.Currency },

            CreatedAtUtc = sale.CreatedAtUtc,
            ApprovedAtUtc = sale.ApprovedAtUtc,
            ApprovedByUserId = sale.ApprovedByUserId,
            CancelledAtUtc = sale.CancelledAtUtc,
            CancellationReason = sale.CancellationReason,

            LineItems = sale.LineItems
                .OrderBy(li => li.LineNumber)
                .Select(li => new SaleLineItemDto
                {
                    Id = li.Id,
                    ProductId = li.ProductId,
                    ProductName = li.ProductName,
                    Quantity = li.Quantity,
                    UnitPrice = new MoneyDto { Amount = li.UnitPrice.Amount, Currency = li.UnitPrice.Currency },
                    Total = new MoneyDto { Amount = li.GrossTotal.Amount, Currency = li.GrossTotal.Currency },
                    LineTotal = new MoneyDto { Amount = li.LineTotal.Amount, Currency = li.LineTotal.Currency },
                    LineNumber = li.LineNumber
                }).ToList(),

            InvoicedAtUtc = sale.InvoicedAtUtc,
        };

        return Result<SaleDto>.Success(dto);
    }
}