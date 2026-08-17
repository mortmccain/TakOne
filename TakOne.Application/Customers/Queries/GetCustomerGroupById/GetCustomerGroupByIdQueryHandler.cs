using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Customers.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Customers.Queries.GetCustomerGroupById;

public sealed class GetCustomerGroupByIdQueryHandler
{
    public static async Task<Result<CustomerGroupDto>> HandleAsync(
        GetCustomerGroupByIdQuery query,
        ICustomerGroupRepository customerGroupRepository,
        ILogger<GetCustomerGroupByIdQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        if (query.GroupId == Guid.Empty)
        {
            return Result<CustomerGroupDto>.Failure("Group ID is required.");
        }

        var group = await customerGroupRepository.GetByIdAsync(query.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogInformation(
                "GetCustomerGroupById: group {GroupId} not found.",
                query.GroupId);
            return Result<CustomerGroupDto>.Failure(
                $"Customer group '{query.GroupId}' was not found.");
        }

        // Load the active-user count for the delete/deactivate warning.
        var activeUserCount = await customerGroupRepository.GetActiveUserCountAsync(
            query.GroupId, cancellationToken);

        var dto = new CustomerGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Salary = new MoneyDto
            {
                Amount = group.Salary.Amount,
                Currency = group.Salary.Currency
            },
            IsActive = group.IsActive,
            CreatedAtUtc = group.CreatedAt,
            UpdatedAtUtc = group.UpdatedAt,
            ActiveUserCount = activeUserCount
        };

        return Result<CustomerGroupDto>.Success(dto);
    }
}