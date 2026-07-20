using FluentValidation;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// FluentValidation validator for <see cref="CreateSaleCommand"/>.
///
/// Only checks primitive, self-contained properties — does NOT touch the database.
/// Cross-aggregate checks (customer exists, products exist, stock available, etc.)
/// are the handler's responsibility, because they require repository access and
/// should produce business-meaningful error messages, not field-level validation
/// messages.
/// </summary>
public sealed class CreateSaleCommandValidator : AbstractValidator<CreateSaleCommand>
{
    /// <summary>
    /// Sanity cap on the number of line items per CreateSale call. Prevents a
    /// buggy or malicious client from sending 10,000 items and overloading the
    /// handler's per-item product-load loop.
    /// </summary>
    public const int MaxItemsPerSale = 100;

    /// <summary>
    /// Worker IDs can be up to 100 characters (matching the User aggregate's guard).
    /// </summary>
    public const int MaxWorkerIdLength = 100;

    public CreateSaleCommandValidator()
    {
        RuleFor(x => x.CustomerWorkerId)
            .NotEmpty().WithMessage("Customer worker ID is required.")
            .MaximumLength(MaxWorkerIdLength)
            .WithMessage($"Customer worker ID cannot exceed {MaxWorkerIdLength} characters.");

        RuleFor(x => x.Items)
            .NotNull().WithMessage("Items list is required (send an empty list if you want an empty draft).")
            .Must(items => items.Count > 0)
            .WithMessage("At least one item is required to create a sale. Use the cart page to add items first.")
            .Must(items => items.Count <= MaxItemsPerSale)
            .WithMessage($"A sale cannot have more than {MaxItemsPerSale} items at creation time.");

        // Per-item primitive checks.
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId)
                .NotEmpty().WithMessage("Product ID is required on each item.");

            item.RuleFor(i => i.Quantity)
                .GreaterThan(0).WithMessage("Quantity must be at least 1 on each item.")
                .LessThanOrEqualTo(10_000).WithMessage("Quantity per line cannot exceed 10,000.");
        });

        // Reject duplicate ProductIds in the same request — the client should
        // aggregate them into a single item with the sum quantity. This catches
        // client bugs early instead of silently letting the aggregate merge lines.
        RuleFor(x => x.Items)
            .Must(items => items.Select(i => i.ProductId).Distinct().Count() == items.Count)
            .WithMessage("Duplicate product IDs found in the items list. Combine duplicate lines into one.");
    }
}