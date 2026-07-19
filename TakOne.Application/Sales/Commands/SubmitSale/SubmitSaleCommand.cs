using FluentValidation;

namespace TakOne.Application.Sales.Commands.SubmitSale;

public sealed class SubmitSaleCommandValidator : AbstractValidator<SubmitSaleCommand>
{
    public SubmitSaleCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
    }
}