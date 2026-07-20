using FluentValidation;

namespace TakOne.Application.Sales.Commands.updateSaleLineItem;

public sealed class UpdateSaleLineItemCommandValidator : AbstractValidator<UpdateSaleLineItemCommand>
{
    public UpdateSaleLineItemCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.LineItemId)
            .NotEmpty().WithMessage("Line item ID is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be at least 1. Use the remove-line command to delete the line.")
            .LessThanOrEqualTo(10_000).WithMessage("Quantity per line cannot exceed 10,000.");
    }
}