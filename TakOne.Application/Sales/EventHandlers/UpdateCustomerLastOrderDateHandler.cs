using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Events;
using Microsoft.Extensions.Logging;

namespace TakOne.Application.Sales.EventHandlers;

/// <summary>
/// When a Sale is created, updates the Customer's LastOrderDateUtc.
/// This now correctly runs against the shared DbContext from UnitOfWork 
/// (same transaction as the Sale creation).
/// </summary>
public static class UpdateCustomerLastOrderDateHandler
{
    public static async Task Handle
        (
        SaleCreatedDomainEvent domainEvent,
        ICustomerRepository customerRepository,
        IUnitOfWork unitOfWork,
        ILogger<SaleCreatedDomainEvent> logger,
        CancellationToken cancellationToken
        )
    {
        var customer = await customerRepository.GetByIdAsync(
            domainEvent.CustomerId, cancellationToken);

        if (customer is null)
        {
            logger.LogWarning(
                "Customer {CustomerId} not found when updating LastOrderDate for Sale {SaleId}",
                domainEvent.CustomerId, domainEvent.SaleId);
            return;
        }

        customer.RecordOrder(domainEvent.CreatedAtUtc);

        logger.LogInformation(
            "Updated LastOrderDate for Customer {CustomerId} to {OrderDate}",
            customer.Id, domainEvent.CreatedAtUtc);
    }
}