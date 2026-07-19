using FluentValidation;

namespace TakOne.Application.Sales.Commands.MarkSaleAsInvoiced;

public sealed class MarkSaleAsInvoicedCommandValidator : AbstractValidator<MarkSaleAsInvoicedCommand>
{
    public MarkSaleAsInvoicedCommandValidator()
    {
        RuleFor(x => x.SaleId).NotEmpty();
    }
}