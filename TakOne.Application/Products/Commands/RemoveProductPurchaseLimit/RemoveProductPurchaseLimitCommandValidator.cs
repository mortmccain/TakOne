using FluentValidation;
using TakOne.Application.Products.Commands.SetProductPurchaseLimit;

namespace TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;

public sealed class RemoveProductPurchaseLimitCommandValidator : AbstractValidator<RemoveProductPurchaseLimitCommand>
{
    public RemoveProductPurchaseLimitCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.GroupName)
            .NotEmpty().WithMessage("Group name is required.")
            .MaximumLength(SetProductPurchaseLimitCommandValidator.MaxGroupNameLength)
            .WithMessage($"Group name cannot exceed {SetProductPurchaseLimitCommandValidator.MaxGroupNameLength} characters.");
    }
}