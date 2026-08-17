using FluentValidation;

namespace TakOne.Application.Customers.Commands.UpdateCustomerGroupSalary;

public sealed class UpdateCustomerGroupSalaryCommandValidator : AbstractValidator<UpdateCustomerGroupSalaryCommand>
{
    public UpdateCustomerGroupSalaryCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");

        RuleFor(x => x.NewSalaryAmount)
            .GreaterThan(0).WithMessage("Salary amount must be greater than zero.")
            .LessThanOrEqualTo(CreateCustomerGroup.CreateCustomerGroupCommandValidator.MaxSalaryAmount)
            .WithMessage($"Salary amount cannot exceed {CreateCustomerGroup.CreateCustomerGroupCommandValidator.MaxSalaryAmount:N0}.");
    }
}