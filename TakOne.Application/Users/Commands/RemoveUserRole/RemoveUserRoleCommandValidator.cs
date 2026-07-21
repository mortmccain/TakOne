using FluentValidation;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.RemoveUserRole;

/// <summary>
/// FluentValidation validator for <see cref="RemoveUserRoleCommand"/>.
/// </summary>
public sealed class RemoveUserRoleCommandValidator : AbstractValidator<RemoveUserRoleCommand>
{
    public static readonly IReadOnlySet<string> AllRoles = new HashSet<string>
    {
        Roles.Admin,
        Roles.Manager,
        Roles.Employee,
        Roles.ReadOnly,
        Roles.Customer,
    };

    public RemoveUserRoleCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(r => AllRoles.Contains(r))
            .WithMessage($"Role must be one of: {string.Join(", ", AllRoles)}.");
    }
}