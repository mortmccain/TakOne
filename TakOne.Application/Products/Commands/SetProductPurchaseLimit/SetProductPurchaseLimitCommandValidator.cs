using FluentValidation;

namespace TakOne.Application.Products.Commands.SetProductPurchaseLimit;

public sealed class SetProductPurchaseLimitCommandValidator : AbstractValidator<SetProductPurchaseLimitCommand>
{
    public const int MaxLimit = 1_000_000;

    public SetProductPurchaseLimitCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        // GroupId must reference an existing CustomerGroup. The handler
        // verifies existence via ICustomerGroupRepository.GetByIdAsync —
        // here we only check it's not Guid.Empty (programmer error).
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");

        RuleFor(x => x.Limit)
            .GreaterThanOrEqualTo(1).WithMessage("Purchase limit must be at least 1.")
            .LessThanOrEqualTo(MaxLimit).WithMessage($"Purchase limit cannot exceed {MaxLimit:N0}.");
    }
}