using FluentValidation;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// FluentValidation validator for <see cref="AssignUserToGroupCommand"/>.
///
/// Only checks primitive, self-contained properties. Cross-aggregate checks
/// (group existence, role scoping) are the handler's job.
/// </summary>
public sealed class AssignUserToGroupCommandValidator : AbstractValidator<AssignUserToGroupCommand>
{
    public AssignUserToGroupCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        // GroupId must reference an existing CustomerGroup row. The handler
        // verifies existence via ICustomerGroupRepository.GetByIdAsync —
        // here we only check it's not Guid.Empty (programmer error).
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");
    }
}