using FluentValidation;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// FluentValidation validator for <see cref="AddItemToSaleCommand"/>.
/// Primitive checks only — no database access.
/// </summary>
public sealed class AddItemToSaleCommandValidator : AbstractValidator<AddItemToSaleCommand>
{
    public AddItemToSaleCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be at least 1.")
            .LessThanOrEqualTo(10_000).WithMessage("Quantity per line cannot exceed 10,000.");
    }
}