using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Creates a new Draft Sale for the current user.
///
/// The current user is BOTH the CustomerId and the CreatedByUserId.
/// We do not take a customer ID in the command — the sale always belongs
/// to whoever is logged in. (A sales employee creating a sale on behalf
/// of a customer will be supported in a future flow if needed; for now
/// every sale is created by and for its buyer.)
/// </summary>
public static class CreateSaleCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync(
        CreateSaleCommand command,
        ICurrentUserService currentUser,
        ISaleNumberGenerator saleNumberGenerator,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
            return Result.Failure("Authentication required.");

        // The current user IS the customer (and the creator).
        var saleNumber = await saleNumberGenerator.NextAsync("SALE", cancellationToken);

        var sale = Sale.Create(
            customerId: currentUser.UserId,
            customerName: currentUser.FullName,
            saleNumber: saleNumber,
            createdByUserId: currentUser.UserId,
            createdByName: currentUser.FullName);

        await saleRepository.AddAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created Draft Sale {SaleNumber} for user {UserId}. SaleId: {SaleId}",
            sale.SaleNumber,
            currentUser.UserId,
            sale.Id);

        return Result<Guid>.Success(sale.Id);
    }
}
