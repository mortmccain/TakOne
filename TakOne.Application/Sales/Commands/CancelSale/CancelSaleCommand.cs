using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CancelSale;

public sealed class CancelSaleCommand
{
    public Guid SaleId { get; init; }
    public Guid CancelledByUserId { get; init; }

    /// <summary>
    /// Roles of the user performing the cancellation.
    /// The handler uses these to enforce role-based status restrictions.
    /// </summary>
    public IReadOnlyList<string> UserRoles { get; init; } = Array.Empty<string>();

    public string Reason { get; init; } = string.Empty;
}