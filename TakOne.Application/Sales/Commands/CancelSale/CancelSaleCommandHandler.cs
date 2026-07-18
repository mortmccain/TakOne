using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using Microsoft.Extensions.Logging;
using TakOne.Application.Sales.Commands.CancelSale;

namespace ERP.Application.Sales.Commands.CancelSale;

public static class CancelSaleCommandHandler
{
    public static async Task<Result> Handle
        (
        CancelSaleCommand command,
        IUnitOfWork unitOfWork,
        ILogger<CancelSaleCommand> logger,
        CancellationToken cancellationToken
        )
    {
        // STEP 1: Load the sale
        var sale = await unitOfWork.GetByIdAsync<Sale>(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        bool isAdmin = command.UserRoles.Contains("Admin");
        bool isManager = command.UserRoles.Contains("Manager");
        bool isEmployee = !isAdmin && !isManager;

        // STEP 2: Role-based status restriction
        // The domain already blocks Shipped / Invoiced / already-Cancelled.
        // We add the business rules on top for Pending and Approved.
        switch (sale.Status)
        {
            case SaleStatus.Approved:
                if (!isAdmin)
                    return Result.Failure("Only Admins can cancel an Approved sale.");
                break;

            case SaleStatus.Pending:
                if (!isAdmin && !isManager)
                    return Result.Failure("Only Admins or Managers can cancel a Pending sale.");
                break;

            case SaleStatus.Draft:
                // All roles may cancel a Draft — ownership check below covers employees
                break;

            default:
                // Shipped, Invoiced, Cancelled — the domain guard inside Cancel() will throw
                break;
        }

        // STEP 3: Employees may only cancel sales they created
        if (isEmployee && sale.CreatedByUserId != command.CancelledByUserId)
            return Result.Failure("You can only cancel your own sales.");

        // STEP 4: Delegate to the domain aggregate
        // (guards: already cancelled, shipped, invoiced, empty reason, empty userId)       
        // something is fucking with the program in here (fucking with the program in what way?)
        try
        {
            sale.Cancel(command.CancelledByUserId, command.Reason);
        }
        catch (DomainException ex)
        {
            return Result.Failure(ex.Message);
        }

        // STEP 5: Persist
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleId} ({SaleNumber}) cancelled by user {UserId}. Reason: {Reason}",
            sale.Id, sale.SaleNumber.Value, command.CancelledByUserId, command.Reason);

        return Result.Success();
    }
}