using FluentValidation;

namespace TakOne.Application.Users.Commands.UpdateUserFullName;

/// <summary>
/// FluentValidation validator for <see cref="UpdateUserFullNameCommand"/>.
/// </summary>
public sealed class UpdateUserFullNameCommandValidator : AbstractValidator<UpdateUserFullNameCommand>
{
    public const int MaxFullNameLength = 200;

    public UpdateUserFullNameCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.NewFullName)
            .NotEmpty().WithMessage("New full name is required.")
            .MaximumLength(MaxFullNameLength)
            .WithMessage($"Full name cannot exceed {MaxFullNameLength} characters.");
    }
}