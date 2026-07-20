using FluentValidation;

namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

public sealed class DeleteDraftSaleCommandValidator : AbstractValidator<DeleteDraftSaleCommand>
{
    public DeleteDraftSaleCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");
    }
}