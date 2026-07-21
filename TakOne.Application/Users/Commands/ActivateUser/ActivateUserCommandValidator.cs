using FluentValidation;

namespace TakOne.Application.Users.Commands.ActivateUser;

/// <summary>
/// FluentValidation validator for <see cref="ActivateUserCommand"/>.
/// </summary>
public sealed class ActivateUserCommandValidator : AbstractValidator<ActivateUserCommand>
{
    public ActivateUserCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");
    }
}