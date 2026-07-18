using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public sealed class ApproveSaleCommand
{
    public Guid SaleId { get; init; }
    public Guid ApprovedByUserId { get; init; }
    public IReadOnlyList<string> UserRoles { get; init; } = Array.Empty<string>();
}