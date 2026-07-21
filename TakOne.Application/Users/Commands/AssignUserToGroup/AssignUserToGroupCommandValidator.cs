using FluentValidation;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// FluentValidation validator for <see cref="AssignUserToGroupCommand"/>.
/// </summary>
public sealed class AssignUserToGroupCommandValidator : AbstractValidator<AssignUserToGroupCommand>
{
    public const int MaxGroupNameLength = 100;

    public AssignUserToGroupCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.GroupName)
            .NotEmpty().WithMessage("Group name is required.")
            .MaximumLength(MaxGroupNameLength)
            .WithMessage($"Group name cannot exceed {MaxGroupNameLength} characters.");
    }
}