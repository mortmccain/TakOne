using FluentValidation;

namespace TakOne.Application.Users.Commands.CreateCustomer;

/// <summary>
/// FluentValidation validator for <see cref="CreateCustomerCommand"/>.
///
/// Only checks primitive, self-contained properties. Cross-aggregate
/// checks (WorkerId uniqueness, email uniqueness) are the handler's job.
/// </summary>
public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public const int MaxWorkerIdLength = 100;
    public const int MaxFullNameLength = 200;
    public const int MaxGroupNameLength = 100;
    public const int MaxEmailLength = 256; // RFC 5321 practical limit
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 100;

    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.WorkerId)
            .NotEmpty().WithMessage("Worker ID is required.")
            .MaximumLength(MaxWorkerIdLength)
            .WithMessage($"Worker ID cannot exceed {MaxWorkerIdLength} characters.");

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(MaxFullNameLength)
            .WithMessage($"Full name cannot exceed {MaxFullNameLength} characters.");

        RuleFor(x => x.GroupName)
            .NotEmpty().WithMessage("Group name is required for customers.")
            .MaximumLength(MaxGroupNameLength)
            .WithMessage($"Group name cannot exceed {MaxGroupNameLength} characters.");

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
    }
}