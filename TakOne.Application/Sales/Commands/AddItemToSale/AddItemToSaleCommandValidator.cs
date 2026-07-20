using FluentValidation;
using TakOne.Application.Sales.Commands.AddItemToSale;

public sealed class AddItemToSaleCommandValidator : AbstractValidator<AddItemToSaleCommand>
{
    public AddItemToSaleCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}