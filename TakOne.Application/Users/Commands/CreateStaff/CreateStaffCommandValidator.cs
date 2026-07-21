using FluentValidation;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.CreateStaff;

/// <summary>
/// FluentValidation validator for <see cref="CreateStaffCommand"/>.
/// </summary>
public sealed class CreateStaffCommandValidator : AbstractValidator<CreateStaffCommand>
{
    public const int MaxWorkerIdLength = 100;
    public const int MaxFullNameLength = 200;
    public const int MaxEmailLength = 256;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 100;

    /// <summary>
    /// Roles that a staff user can be assigned at creation. Customer is
    /// NOT in this list — staff accounts are never customers at creation
    /// time. (A manager or employee who wants to buy on their own behalf
    /// gets the Customer role added later via <c>AssignUserRoleCommand</c>.)
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedStaffRoles = new HashSet<string>
    {
        Roles.Employee,
        Roles.Manager,
        Roles.ReadOnly,
        Roles.Admin,
    };

    public CreateStaffCommandValidator()
    {
        RuleFor(x => x.WorkerId)
            .NotEmpty().WithMessage("Worker ID is required.")
            .MaximumLength(MaxWorkerIdLength)
            .WithMessage($"Worker ID cannot exceed {MaxWorkerIdLength} characters.");

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(MaxFullNameLength)
            .WithMessage($"Full name cannot exceed {MaxFullNameLength} characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .MaximumLength(MaxEmailLength)
            .WithMessage($"Email cannot exceed {MaxEmailLength} characters.")
            .EmailAddress().WithMessage("Email must be a valid email address.");

        RuleFor(x => x.InitialPassword)
            .NotEmpty().WithMessage("Initial password is required.")
            .MinimumLength(MinPasswordLength)
            .WithMessage($"Password must be at least {MinPasswordLength} characters.")
            .MaximumLength(MaxPasswordLength)
            .WithMessage($"Password cannot exceed {MaxPasswordLength} characters.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(r => AllowedStaffRoles.Contains(r))
            .WithMessage($"Role must be one of: {string.Join(", ", AllowedStaffRoles)}.");
    }
}