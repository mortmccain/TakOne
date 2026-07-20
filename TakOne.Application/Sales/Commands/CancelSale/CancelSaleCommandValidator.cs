using FluentValidation;

namespace TakOne.Application.Sales.Commands.CancelSale;

public sealed class CancelSaleCommandValidator : AbstractValidator<CancelSaleCommand>
{
    public CancelSaleCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A cancellation reason is required.")
            .MinimumLength(3).WithMessage("Cancellation reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Cancellation reason cannot exceed 500 characters.");
    }
}