using FluentValidation;

namespace TakOne.Application.Users.Commands.DeactivateUser;

/// <summary>
/// FluentValidation validator for <see cref="DeactivateUserCommand"/>.
/// </summary>
public sealed class DeactivateUserCommandValidator : AbstractValidator<DeactivateUserCommand>
{
    public DeactivateUserCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");
    }
}