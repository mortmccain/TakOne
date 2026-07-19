using FluentValidation;

namespace TakOne.Application.Sales.Commands.updateSaleLineItem;

public sealed class UpdateSaleLineItemCommandValidator : AbstractValidator<UpdateSaleLineItemCommand>
{
    public UpdateSaleLineItemCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
        RuleFor(x => x.LineItemId).NotEmpty();
        RuleFor(x => x.NewQuantity).GreaterThan(0);
    }
}