using FluentValidation;

namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

public sealed class RemoveSaleLineItemCommandValidator : AbstractValidator<RemoveSaleLineItemCommand>
{
    public RemoveSaleLineItemCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.LineItemId)
            .NotEmpty().WithMessage("Line item ID is required.");
    }
}