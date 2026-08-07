using TakOne.Application.Common.Authorization;
using TakOne.Application.Users.DTOs;

namespace TakOne.Application.Users.Queries.GetUserById;

/// <summary>
/// Loads a single User by Id and projects it to <see cref="UserDto"/>.
///
/// AUTHORIZATION MODEL:
///   - Admin / Manager: may view any user, including the GroupName field.
///   - Employee: may view any user, but GroupName is hidden in the DTO.
///   - Customer / ReadOnly: may view ONLY their own profile. GroupName is
///     hidden. (Customers must never see their own GroupName — it's an
///     internal grouping mechanism used to apply purchase limits.)
///
/// The "customers must never see their own GroupName" rule is enforced in
/// the handler by clearing the GroupName field on the DTO for non-admin,
/// non-manager callers.
///
/// WHY THIS IS A QUERY, NOT A COMMAND:
///   Reads are stateless. See GetSaleByIdQuery for the full rationale.
/// </summary>
[RequireAuthentication]
public sealed class GetUserByIdQuery
{
    public Guid UserId { get; init; }
}