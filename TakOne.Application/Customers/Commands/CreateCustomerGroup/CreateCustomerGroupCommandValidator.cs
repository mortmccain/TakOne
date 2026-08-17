using FluentValidation;

namespace TakOne.Application.Customers.Commands.CreateCustomerGroup;

public sealed class CreateCustomerGroupCommandValidator : AbstractValidator<CreateCustomerGroupCommand>
{
    public const int MaxNameLength = 100;
    public const decimal MaxSalaryAmount = 1_000_000_000m; // 1 billion — covers IRR + USD ranges
    public const int CurrencyLength = 3; // ISO 4217

    public CreateCustomerGroupCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Group name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Group name cannot exceed {MaxNameLength} characters.");

        RuleFor(x => x.SalaryAmount)
            .GreaterThan(0).WithMessage("Salary amount must be greater than zero.")
            .LessThanOrEqualTo(MaxSalaryAmount)
            .WithMessage($"Salary amount cannot exceed {MaxSalaryAmount:N0}.");

        // Currency is enforced by the Money value object's constructor
        // (3-letter ISO 4217 code, uppercase). We check length here for
        // a friendlier error; the Money constructor's DomainException
        // is the hard guarantee.
        RuleFor(x => x.SalaryCurrency)
            .NotEmpty().WithMessage("Salary currency is required.")
            .Length(CurrencyLength)
            .WithMessage($"Salary currency must be a {CurrencyLength}-letter ISO 4217 code (e.g. USD, IRR).");
    }
}