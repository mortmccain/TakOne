using FluentValidation;
using TakOne.Application.Customers.Commands.CreateCustomer;

namespace TakOne.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Customer name is required.")
            .MaximumLength(300).WithMessage("Customer name cannot exceed 300 characters.");
    }
}
