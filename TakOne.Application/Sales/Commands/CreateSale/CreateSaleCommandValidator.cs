using FluentValidation;

namespace TakOne.Application.Sales.Commands.CreateSale;

public sealed class CreateSaleCommandValidator : AbstractValidator<CreateSaleCommand>
{
    public CreateSaleCommandValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("Customer is required.");

        RuleFor(x => x.CreatedByUserId)
            .NotEmpty().WithMessage("Creator user ID is required.");

        RuleFor(x => x.CreatedByName)
            .NotEmpty().WithMessage("Creator name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("At least one line item is required.");

        RuleForEach(x => x.Items).ChildRules
            (
            item =>
            {
                item.RuleFor(i => i.ProductId)
                    .NotEmpty().WithMessage("Product ID is required for each item.");

                item.RuleFor(i => i.ProductName)
                    .NotEmpty().WithMessage("Product name is required for each item.")
                    .MaximumLength(500);

                item.RuleFor(i => i.Quantity)
                    .GreaterThan(0).WithMessage("Quantity must be greater than zero.")
                    .LessThanOrEqualTo(10_000).WithMessage("Quantity cannot exceed 10,000.");

                item.RuleFor(i => i.UnitPriceAmount)
                    .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.");

                item.RuleFor(i => i.Currency)
                    .NotEmpty().WithMessage("Currency is required.")
                    .Length(3).WithMessage("Currency must be a 3-letter ISO code.");
            }
        );
    }
}