using FluentValidation;
using TakOne.Application.Sales.Commands.UpdateSaleLineItem;

public sealed class UpdateSaleLineItemCommandValidator : AbstractValidator<UpdateSaleLineItemCommand>
{
    public UpdateSaleLineItemCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
        RuleFor(x => x.LineItemId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}