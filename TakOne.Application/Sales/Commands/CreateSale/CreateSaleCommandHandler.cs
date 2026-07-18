using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Net;
using TakOne.Application.Sales.Commands.CreateSale;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Handles the creation of a new Sale.
/// Orchestrates loading the Customer, creating the Sale Aggregate,
/// adding line items, and persisting everything.
/// </summary>
public static class CreateSaleCommandHandler
{
    public static async Task<Result<Guid>> Handle
        (
        CreateSaleCommand command,
        IBuyerRepository customerRepository,
        ISaleNumberGenerator saleNumberGenerator,
        IUnitOfWork unitOfWork,
        ILogger<CreateSaleCommand> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // STEP 1: Load the Customer
        // ------------------------------------------------------------------
        Customer? customer = await customerRepository.GetByIdAsync
            (
            command.CustomerId, cancellationToken
            );

        if (customer is null)
        {
            return Result<Guid>.Failure($"Customer with ID '{command.CustomerId}' was not found.");
        }

        if (!customer.IsActive)
        {
            return Result<Guid>.Failure($"Customer '{customer.Name}' is inactive. Cannot create a sale.");
        }

        // ------------------------------------------------------------------
        // STEP 2: Generate a SaleNumber
        // ------------------------------------------------------------------
        // In production, this would use a sequence or a domain service.
        // For now, we generate a simple sequential-ish number.
        var saleNumber = await saleNumberGenerator.NextAsync("SALE", cancellationToken);
        // ------------------------------------------------------------------
        // STEP 3: Create the address and Sale Aggregate
        // ------------------------------------------------------------------
        var shippingAddress = new Address
            (
                 customer.ShippingAddress.Country,
                 customer.ShippingAddress.Street,
                 customer.ShippingAddress.City,
                 customer.ShippingAddress.PostalCode,
                 customer.ShippingAddress.ExactAddress
            );
        var sale = Sale.Create
            (
            // shouldn't we take some of these from the database for security reasons? _currentUserService.UserId 
            command.CustomerId,
            customer.Name,
            shippingAddress,
            saleNumber,
            command.CreatedByUserId,
            command.CreatedByName
            );

        // ------------------------------------------------------------------
        // STEP 4: Add line items to the Sale
        // ------------------------------------------------------------------
        foreach (var item in command.Items)
        {
            var unitPrice = new Money(item.UnitPriceAmount, item.Currency);

            sale.AddLineItem
                (
                item.ProductId,
                item.ProductName,
                item.SKU,
                item.Quantity,
                unitPrice,
                productCategory: "General"      // Could come from Product aggregate in production
                );
        }

        // ------------------------------------------------------------------
        // STEP 5: Persist
        // ------------------------------------------------------------------
        await unitOfWork.AddAsync<Sale>(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // ------------------------------------------------------------------
        // STEP 6: Log and return
        // ------------------------------------------------------------------
        logger.LogInformation
            (
            "Created Sale {SaleNumber} for Customer {CustomerName}. SaleId: {SaleId}",
            sale.SaleNumber,
            customer.Name,
            sale.Id
            );

        return Result<Guid>.Success(sale.Id);
    }
}