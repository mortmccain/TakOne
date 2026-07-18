using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using Microsoft.Extensions.Logging;
using TakOne.Application.Sales.Commands.MarkAsInvoiced;

namespace ERP.Application.Sales.Commands.MarkAsInvoiced;

public static class MarkAsInvoicedCommandHandler
{
    public static async Task<Result> Handle
        (
        MarkAsInvoicedCommand command,
        IUnitOfWork unitOfWork,
        ILogger<MarkAsInvoicedCommand> logger,
        CancellationToken cancellationToken
        )
    {
        var sale = await unitOfWork.GetByIdAsync<Sale>(command.SaleId, cancellationToken: cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        if (!command.UserRoles.Contains("Admin") && !command.UserRoles.Contains("Manager"))
            return Result.Failure("Only Admins and Managers can mark a sale as invoiced.");

        try
        {
            sale.MarkAsInvoiced();
        }
        catch (DomainException ex)
        {
            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleNumber} (Id: {SaleId}) marked as invoiced by user {UserId}.",
            sale.SaleNumber.Value, sale.Id, command.MarkedByUserId);

        return Result.Success();
    }
}