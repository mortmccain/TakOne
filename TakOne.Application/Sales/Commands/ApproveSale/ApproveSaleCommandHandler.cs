using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using Microsoft.Extensions.Logging;
using TakOne.Application.Sales.Commands.ApproveSale;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public static class ApproveSaleCommandHandler
{
    public static async Task<Result> Handle
        (
        ApproveSaleCommand command,
        IUnitOfWork unitOfWork,
        ILogger<ApproveSaleCommand> logger,
        CancellationToken cancellationToken
        )
    {
        var sale = await unitOfWork.GetByIdAsync<Sale>(command.SaleId, cancellationToken);

        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        bool canApprove =
            command.UserRoles.Contains("Admin") || command.UserRoles.Contains("Manager");

        if (!canApprove)
            return Result.Failure("Only Admins and Managers can approve sales.");

        try
        {
            sale.Approve(command.ApprovedByUserId);
        }
        catch (DomainException ex)
        {
            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            (
            "Sale {SaleNumber} (Id: {SaleId}) approved by user {UserId}.",
            sale.SaleNumber.Value, sale.Id, command.ApprovedByUserId
            );

        return Result.Success();
    }
}