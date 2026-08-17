using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Queries.GetCustomerGroupById;

/// <summary>
/// Loads a single customer group by Id, including its active-user count
/// (for the EditGroup page's delete/deactivate warning).
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed class GetCustomerGroupByIdQuery
{
    public Guid GroupId { get; init; }
}