using FluentValidation;

namespace TakOne.Application.Products.Commands.IncreaseProductStock;

public sealed class IncreaseProductStockCommandValidator : AbstractValidator<IncreaseProductStockCommand>
{
    /// <summary>
    /// Sanity cap on a single restock operation. Prevents a typo (e.g.
    /// entering 1,000,000 instead of 100) from blowing up the stock count.
    /// Real restock operations above this threshold should be split into
    /// multiple commands or done via a dedicated bulk-import flow.
    /// </summary>
    public const int MaxSingleIncrease = 100_000;

    public IncreaseProductStockCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Stock increase quantity must be greater than zero.")
            .LessThanOrEqualTo(MaxSingleIncrease)
            .WithMessage($"A single stock increase cannot exceed {MaxSingleIncrease:N0} units. " +
                         "Split large restocks into multiple commands.");
    }
}