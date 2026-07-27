using FluentValidation;

namespace TakOne.Application.Sales.Commands.CreateOrAppendSale;

/// <summary>
/// FluentValidation validator for <see cref="CreateOrAppendSaleCommand"/>.
/// Primitive checks only — no database access. Mirrors the rules on
/// <c>AddItemToSaleCommandValidator</c> (minus the SaleId rule, since this
/// command doesn't take a SaleId).
/// </summary>
public sealed class CreateOrAppendSaleCommandValidator : AbstractValidator<CreateOrAppendSaleCommand>
{
    public CreateOrAppendSaleCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be at least 1.")
            .LessThanOrEqualTo(10_000).WithMessage("Quantity per line cannot exceed 10,000.");
    }
}