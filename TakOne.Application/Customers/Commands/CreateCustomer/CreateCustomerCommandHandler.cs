using TakOne.SharedKernel.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Net;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Customers.Commands.CreateCustomer;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.CreateCustomer;

public static class CreateCustomerCommandHandler
{
    public static async Task<Result<Guid>> Handle
        (
        CreateCustomerCommand command,
        IUnitOfWork unitOfWork,
        ILogger<CreateCustomerCommand> logger,
        CancellationToken cancellationToken
        )
    {


        // STEP 3: Create the Customer Aggregate
        var customer = Customer.Create
            (
            command.Name
            );

        // STEP 4: Persist
        await unitOfWork.AddAsync<Customer>(customer, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // STEP 5: Log and return
        logger.LogInformation(
            "Created Customer {CustomerCode} - {CustomerName}. CustomerId: {CustomerId}",
            customer.CustomerCode,
            customer.Name,
            customer.Id);

        return Result<Guid>.Success(customer.Id);
    }
}