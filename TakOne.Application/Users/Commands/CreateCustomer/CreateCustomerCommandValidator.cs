using FluentValidation;
using TakOne.Domain.Users;

namespace TakOne.Application.Users.Commands.CreateCustomer;

/// <summary>
/// FluentValidation validator for <see cref="CreateCustomerCommand"/>.
///
/// Only checks primitive, self-contained properties. Cross-aggregate
/// checks (WorkerId uniqueness, email uniqueness, Group existence) are
/// the handler's job.
/// </summary>
public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public const int MaxWorkerIdLength = 100;
    public const int MaxFullNameLength = 200;
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

        // GroupId must be a non-empty Guid. The handler verifies the group
        // exists via ICustomerGroupRepository.GetByIdAsync — here we only
        // check it's not Guid.Empty (programmer error from a UI that forgot
        // to populate the dropdown).
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("A customer group is required for new customers.");

        RuleFor(x => x.Email)
            // Email is OPTIONAL — employees don't have company emails, and
            // login uses WorkerId (UserName), not email. We still enforce
            // max length and valid format WHEN a non-empty value is provided
            // (for forward compatibility — e.g. if a future admin UI re-adds
            // an optional email field for password-reset flow). Empty/null
            // emails are passed straight through to Identity, which has
            // RequireUniqueEmail=false in appsettings.json.
            .MaximumLength(MaxEmailLength)
            .WithMessage($"Email cannot exceed {MaxEmailLength} characters.")
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Email must be a valid email address.");

        RuleFor(x => x.InitialPassword)
            .NotEmpty().WithMessage("Initial password is required.")
            .MinimumLength(MinPasswordLength)
            .WithMessage($"Password must be at least {MinPasswordLength} characters.")
            .MaximumLength(MaxPasswordLength)
            .WithMessage($"Password cannot exceed {MaxPasswordLength} characters.");

        // Gender — must be a defined enum value. C# enums can hold ANY
        // integer (e.g. (Gender)42), so IsEnumName() checks that the value
        // is one of the declared members (Male=0, Female=1). The Domain
        // User.ChangeGender / factory also enforces this, but failing fast
        // at the validator gives a friendlier error message.
        RuleFor(x => x.Gender)
            .IsInEnum().WithMessage("Gender must be a valid value (Male or Female).");
    }
}