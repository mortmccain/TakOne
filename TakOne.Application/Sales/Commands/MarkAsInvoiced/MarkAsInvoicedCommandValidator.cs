using FluentValidation;
using TakOne.Application.Sales.Commands.MarkAsInvoiced;

namespace ERP.Application.Sales.Commands.MarkAsInvoiced;

public sealed class MarkAsInvoicedCommandValidator : AbstractValidator<MarkAsInvoicedCommand>
{
    public MarkAsInvoicedCommandValidator()
    {
        RuleFor(x => x.SaleId)
            .NotEmpty().WithMessage("Sale ID is required.");

        RuleFor(x => x.MarkedByUserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.UserRoles)
            .NotEmpty().WithMessage("User roles must be provided.")
            .Must(roles => roles.Contains("Admin") || roles.Contains("Manager"))
            .WithMessage("Only Admins and Managers can mark a sale as invoiced.");
    }
}