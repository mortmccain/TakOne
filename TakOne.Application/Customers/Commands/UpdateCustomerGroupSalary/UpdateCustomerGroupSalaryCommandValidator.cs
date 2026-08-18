using FluentValidation;
using TakOne.Application.Customers.Commands.CreateCustomerGroup;

namespace TakOne.Application.Customers.Commands.UpdateCustomerGroupSalary;

public sealed class UpdateCustomerGroupSalaryCommandValidator : AbstractValidator<UpdateCustomerGroupSalaryCommand>
{
    public UpdateCustomerGroupSalaryCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");

        RuleFor(x => x.NewSalaryAmount)
            .GreaterThan(0).WithMessage("Salary amount must be greater than zero.")
            .LessThanOrEqualTo(CreateCustomerGroupCommandValidator.MaxSalaryAmount)
            .WithMessage($"Salary amount cannot exceed {CreateCustomerGroupCommandValidator.MaxSalaryAmount:N0}.");
    }
}