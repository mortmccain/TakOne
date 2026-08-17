using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.UpdateCustomerGroupSalary;

public sealed class UpdateCustomerGroupSalaryCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateCustomerGroupSalaryCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateCustomerGroupSalaryCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("UpdateCustomerGroupSalary: unauthenticated call rejected.");
            return Result.Failure("Authentication required.");
        }

        var group = await customerGroupRepository.GetByIdAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "UpdateCustomerGroupSalary: group {GroupId} not found. Requested by user {UserId}.",
                command.GroupId, currentUser.UserId);
            return Result.Failure($"Customer group '{command.GroupId}' was not found.");
        }

        // ------------------------------------------------------------------
        // Delegate to the aggregate. UpdateSalary takes a Money value object
        // (preserves the existing currency — changing currency is not
        // allowed via this command; see the command's XML doc).
        //
        // The Money constructor throws DomainException on invalid input
        // (negative amount, wrong-length currency) — caught by middleware.
        // ------------------------------------------------------------------
        var previousAmount = group.Salary.Amount;
        var currency = group.Salary.Currency;
        var newSalary = new TakOne.SharedKernel.ValueObjects.Money(command.NewSalaryAmount, currency);
        group.UpdateSalary(newSalary);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UpdateCustomerGroupSalary: group {GroupId} salary updated from {PreviousAmount} to {NewAmount} {Currency} by user {UserId}.",
            group.Id, previousAmount, group.Salary.Amount, currency, currentUser.UserId);

        return Result.Success();
    }
}