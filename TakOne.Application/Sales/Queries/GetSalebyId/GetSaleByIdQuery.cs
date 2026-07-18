using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Queries.GetSalebyId;

public sealed class GetSaleByIdQuery
{
    public Guid SaleId { get; init; }
    public Guid RequestedByUserId { get; init; }
    public IReadOnlyList<string> UserRoles { get; init; } = Array.Empty<string>();
}