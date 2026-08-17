using FluentValidation;

namespace TakOne.Application.Customers.Commands.DeactivateCustomerGroup;

public sealed class DeactivateCustomerGroupCommandValidator : AbstractValidator<DeactivateCustomerGroupCommand>
{
    public DeactivateCustomerGroupCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");
    }
}