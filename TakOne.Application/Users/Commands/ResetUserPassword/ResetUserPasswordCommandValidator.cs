using FluentValidation;

namespace TakOne.Application.Users.Commands.ResetUserPassword;

/// <summary>
/// FluentValidation validator for <see cref="ResetUserPasswordCommand"/>.
/// </summary>
public sealed class ResetUserPasswordCommandValidator : AbstractValidator<ResetUserPasswordCommand>
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 100;

    public ResetUserPasswordCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("New password is required.")
            .MinimumLength(MinPasswordLength)
            .WithMessage($"Password must be at least {MinPasswordLength} characters.")
            .MaximumLength(MaxPasswordLength)
            .WithMessage($"Password cannot exceed {MaxPasswordLength} characters.");
    }
}