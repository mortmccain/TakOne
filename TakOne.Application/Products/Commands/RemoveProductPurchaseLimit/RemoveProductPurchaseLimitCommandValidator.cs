using FluentValidation;
using TakOne.Application.Products.Commands.SetProductPurchaseLimit;

namespace TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;

public sealed class RemoveProductPurchaseLimitCommandValidator : AbstractValidator<RemoveProductPurchaseLimitCommand>
{
    public RemoveProductPurchaseLimitCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        // GroupId must reference an existing CustomerGroup. The handler
        // verifies existence — here we only check it's not Guid.Empty.
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");
    }
}
