using FluentValidation;

namespace TakOne.Application.Sales.Commands.MarkAsInvoiced;

public sealed class MarkAsInvoicedCommandValidator : AbstractValidator<MarkAsInvoicedCommand>
{
    public MarkAsInvoicedCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");
    }
}