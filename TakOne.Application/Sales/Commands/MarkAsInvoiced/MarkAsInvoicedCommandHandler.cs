using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.MarkAsInvoiced;

public sealed class MarkAsInvoicedCommandHandler
{
    public static async Task<Result> HandleAsync(
        MarkAsInvoicedCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<MarkAsInvoicedCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        if (currentUser.UserId == Guid.Empty)
        {
            return Result.Failure("Authentication required.");
        }

        // No need for line items — invoicing doesn't touch stock or lines.
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // Delegate to the aggregate. MarkAsInvoiced enforces:
        //   - sale is currently Approved (throws otherwise)
        //   - invoicedByUserId is a non-empty Guid (throws otherwise)
        sale.MarkAsInvoiced(currentUser.UserId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "MarkAsInvoiced: sale {SaleId} ({SaleNumber}) marked as invoiced by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}