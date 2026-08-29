using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.MarkAsInvoiced;

public sealed class MarkAsInvoicedCommandHandler
{
    /// <summary>
    /// Maximum retry attempts for the invoice + SaveChanges sequence.
    /// Catches <c>DbUpdateConcurrencyException</c> (sale row version
    /// conflict from a concurrent Cancel on the same sale) + SQL Server
    /// unique-constraint violations.
    /// </summary>
    private const int MaxAttempts = 3;

    public static async Task<Result> HandleAsync(
        MarkAsInvoicedCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        ISaleStateLock saleStateLock,
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

        // ------------------------------------------------------------------
        // Role check (defense-in-depth).
        //
        // The command is decorated [RequireRoles(Roles.Employee, Roles.
        // Manager, Roles.Admin)] and the AuthorizationMiddleware enforces
        // it, but marking as invoiced a sale mutates stock and the sale's audit trail —
        // a Customer must never perform it (not even on their own sale).
        // Mirrors the CreateStaffCommandHandler in-handler check pattern.
        // ------------------------------------------------------------------
        if (!currentUser.IsInRole(Roles.Employee)
            && !currentUser.IsInRole(Roles.Manager)
            && !currentUser.IsInRole(Roles.Admin))
        {
            return Result.Failure(
                "Only staff (employee, manager, or admin) may mark as invoiced a sale.");
        }

        // Initial load (lightweight) — to verify the sale exists + acquire
        // the per-sale state lock. The re-load happens inside the retry
        // loop with the lock held so we observe the freshest state.
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-SALE STATE-TRANSITION LOCK + RETRY (race-condition fix).
        //
        // The critical race here is MarkAsInvoiced × Cancel: both staff
        // members act on the same Approved sale, both pass the
        // EnsureApproved guard, the loser's SaveChanges fails. Worse
        // case: the loser's in-memory state shows Approved but the winner
        // has already cancelled (stock restored) — the loser's commit
        // would have transitioned Cancelled → Invoiced, an illegal jump.
        // The aggregate guards against this (EnsureApproved throws if
        // status is now Cancelled), but the loser would surface a generic
        // error. The lock + retry surfaces a clean "state changed" error.
        // ------------------------------------------------------------------
        await using var _saleStateLockHandle = await saleStateLock.AcquireAsync(
            sale.Id, cancellationToken);

        try
        {
            return await unitOfWork.ExecuteWithRetryAsync(
                operation: async ct =>
                {
                    var freshSale = await saleRepository.GetByIdAsync(
                        command.SaleId, ct);
                    if (freshSale is null)
                    {
                        return Result.Failure($"Sale '{command.SaleId}' was not found.");
                    }

                    // The aggregate enforces:
                    //   - sale is currently Approved (throws otherwise)
                    //   - invoicedByUserId is a non-empty Guid (throws otherwise)
                    // Throws DomainException if a concurrent Cancel moved
                    // the state to Cancelled.
                    freshSale.MarkAsInvoiced(currentUser.UserId);

                    await unitOfWork.SaveChangesAsync(ct);

                    logger.LogInformation(
                        "MarkAsInvoiced: sale {SaleId} ({SaleNumber}) marked as invoiced by user {UserId}.",
                        freshSale.Id, freshSale.SaleNumber, currentUser.UserId);

                    return Result.Success();
                },
                maxAttempts: MaxAttempts,
                cancellationToken: cancellationToken);
        }
        catch (DomainException ex)
        {
            // The aggregate's EnsureApproved() threw — a concurrent
            // Cancel moved the sale state to Cancelled. Surface as a
            // clean failure.
            logger.LogWarning(
                "MarkAsInvoiced: domain guard failed for sale {SaleId} (likely a concurrent Cancel). " +
                "Message: {Message}",
                command.SaleId, ex.Message);
            return Result.Failure(
                "The sale's state changed before invoicing could complete. Refresh and try again.");
        }
    }
}