using FluentValidation;

namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

public sealed class RemoveSaleLineItemCommandValidator : AbstractValidator<RemoveSaleLineItemCommand>
{
    public RemoveSaleLineItemCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
        RuleFor(x => x.LineItemId).NotEmpty();
    }
}