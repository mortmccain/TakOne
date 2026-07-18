using FluentValidation;
using TakOne.Application.Sales.Commands.CancelSale;

namespace TakOne.Application.Sales.Commands.CancelSale;

public sealed class CancelSaleCommandValidator : AbstractValidator<CancelSaleCommand>
{
    public CancelSaleCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.CancelledByUserId)
            .NotEmpty().WithMessage("The cancelling user's ID is required.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A cancellation reason is required.")
            .MaximumLength(1000).WithMessage("Reason cannot exceed 1000 characters.");

        RuleFor(x => x.UserRoles)
            .NotEmpty().WithMessage("User roles must be provided.");
    }
}