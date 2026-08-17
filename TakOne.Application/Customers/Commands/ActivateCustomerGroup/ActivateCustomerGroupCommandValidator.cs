using FluentValidation;

namespace TakOne.Application.Customers.Commands.ActivateCustomerGroup;

public sealed class ActivateCustomerGroupCommandValidator : AbstractValidator<ActivateCustomerGroupCommand>
{
    public ActivateCustomerGroupCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");
    }
}