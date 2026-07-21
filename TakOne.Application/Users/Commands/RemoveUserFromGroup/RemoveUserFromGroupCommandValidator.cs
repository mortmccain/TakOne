using FluentValidation;

namespace TakOne.Application.Users.Commands.RemoveUserFromGroup;

/// <summary>
/// FluentValidation validator for <see cref="RemoveUserFromGroupCommand"/>.
/// </summary>
public sealed class RemoveUserFromGroupCommandValidator : AbstractValidator<RemoveUserFromGroupCommand>
{
    public RemoveUserFromGroupCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");
    }
}