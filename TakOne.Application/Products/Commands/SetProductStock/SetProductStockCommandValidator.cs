using FluentValidation;

namespace TakOne.Application.Products.Commands.SetProductStock;

/// <summary>
/// Validator for <see cref="SetProductStockCommand"/>.
///
/// Enforces the user-facing invariant that quantity must be ≥ 1 (positive).
/// Zero and negative values are rejected with a clear message pointing to
/// the deactivation flow as the way to zero out stock.
///
/// The domain method <c>Product.AdjustStockTo</c> enforces the same
/// invariant as defense-in-depth (in case a caller bypasses the validator
/// via tests or a non-HTTP host).
/// </summary>
public sealed class SetProductStockCommandValidator : AbstractValidator<SetProductStockCommand>
{
    /// <summary>
    /// Same sanity cap as IncreaseProductStockCommandValidator.MaxSingleIncrease.
    /// Prevents a typo from blowing up the stock count.
    /// </summary>
    public const int MaxStockValue = 100_000;

    public SetProductStockCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .WithMessage("Stock quantity must be greater than zero. To make it zero, deactivate the product instead.")
            .LessThanOrEqualTo(MaxStockValue)
            .WithMessage($"Stock quantity cannot exceed {MaxStockValue:N0} units.");
    }
}
