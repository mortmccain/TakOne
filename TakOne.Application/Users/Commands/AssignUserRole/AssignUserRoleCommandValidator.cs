using FluentValidation;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.AssignUserRole;

/// <summary>
/// FluentValidation validator for <see cref="AssignUserRoleCommand"/>.
/// </summary>
public sealed class AssignUserRoleCommandValidator : AbstractValidator<AssignUserRoleCommand>
{
    /// <summary>
    /// All roles that exist in the system. The Infrastructure layer seeds
    /// these into the IdentityRole table at startup. Keep in sync with
    /// <see cref="Roles"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> AllRoles = new HashSet<string>
    {
        Roles.Admin,
        Roles.Manager,
        Roles.Employee,
        Roles.ReadOnly,
        Roles.Customer,
    };

    public AssignUserRoleCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(r => AllRoles.Contains(r))
            .WithMessage($"Role must be one of: {string.Join(", ", AllRoles)}.");
    }
}