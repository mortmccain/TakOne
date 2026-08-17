using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Customers.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Customers.Queries.GetAllCustomerGroups;

/// <summary>
/// Handler for <see cref="GetAllCustomerGroupsQuery"/>.
///
/// Loads all groups via <c>ICustomerGroupRepository.GetAllAsync</c>
/// (expected 5-15 rows — no pagination needed). Projects to
/// <see cref="CustomerGroupListItemDto"/> (lightweight, no audit timestamps).
/// </summary>
public sealed class GetAllCustomerGroupsQueryHandler
{
    public static async Task<Result<List<CustomerGroupListItemDto>>> HandleAsync(
        GetAllCustomerGroupsQuery query,
        ICustomerGroupRepository customerGroupRepository,
        ILogger<GetAllCustomerGroupsQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var groups = await customerGroupRepository.GetAllAsync(
                includeInactive: query.IncludeInactive,
                cancellationToken);

            var dtos = groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CustomerGroupListItemDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    Salary = new MoneyDto
                    {
                        Amount = g.Salary.Amount,
                        Currency = g.Salary.Currency
                    },
                    IsActive = g.IsActive
                })
                .ToList();

            return Result<List<CustomerGroupListItemDto>>.Success(dtos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "GetAllCustomerGroups: failed to load customer groups from the repository.");

            return Result<List<CustomerGroupListItemDto>>.Failure(
                "Could not load customer groups. Please try again.");
        }
    }
}