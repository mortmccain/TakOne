using FluentValidation;

namespace TakOne.Application.Products.Commands.SetProductPurchaseLimit;

public sealed class SetProductPurchaseLimitCommandValidator : AbstractValidator<SetProductPurchaseLimitCommand>
{
    public const int MaxGroupNameLength = 100;
    public const int MaxLimit = 1_000_000;

    public SetProductPurchaseLimitCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.GroupName)
            .NotEmpty().WithMessage("Group name is required.")
            .MaximumLength(MaxGroupNameLength)
            .WithMessage($"Group name cannot exceed {MaxGroupNameLength} characters.");

        RuleFor(x => x.Limit)
            .GreaterThanOrEqualTo(1).WithMessage("Purchase limit must be at least 1.")
            .LessThanOrEqualTo(MaxLimit).WithMessage($"Purchase limit cannot exceed {MaxLimit:N0}.");
    }
}