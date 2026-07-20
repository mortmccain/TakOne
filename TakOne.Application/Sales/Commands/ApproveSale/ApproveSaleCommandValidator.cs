using FluentValidation;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public sealed class ApproveSaleCommandValidator : AbstractValidator<ApproveSaleCommand>
{
    public ApproveSaleCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");
    }
}