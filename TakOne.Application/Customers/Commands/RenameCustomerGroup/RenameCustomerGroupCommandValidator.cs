using FluentValidation;
using TakOne.Application.Customers.Commands.CreateCustomerGroup;

namespace TakOne.Application.Customers.Commands.RenameCustomerGroup;

public sealed class RenameCustomerGroupCommandValidator : AbstractValidator<RenameCustomerGroupCommand>
{
    public RenameCustomerGroupCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");

        RuleFor(x => x.NewName)
            .NotEmpty().WithMessage("New group name is required.")
            .MaximumLength(CreateCustomerGroupCommandValidator.MaxNameLength)
            .WithMessage($"Group name cannot exceed {CreateCustomerGroupCommandValidator.MaxNameLength} characters.");
    }
}