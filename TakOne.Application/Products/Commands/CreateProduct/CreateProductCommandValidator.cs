using FluentValidation;

namespace TakOne.Application.Products.Commands.CreateProduct;

/// <summary>
/// FluentValidation validator for <see cref="CreateProductCommand"/>.
///
/// Only checks primitive, self-contained properties — does NOT touch the
/// database. Cross-aggregate checks (category hierarchy, name uniqueness)
/// are the handler's responsibility, because they require repository access
/// and should produce business-meaningful error messages, not field-level
/// validation messages.
/// </summary>
public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxPictureUrlLength = 500;
    public const int MaxStockQuantity = 1_000_000;

    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Product name cannot exceed {MaxNameLength} characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Product description is required.")
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Product description cannot exceed {MaxDescriptionLength} characters.");

        RuleFor(x => x.PictureUrl)
            .MaximumLength(MaxPictureUrlLength)
            .WithMessage($"Picture URL cannot exceed {MaxPictureUrlLength} characters.")
            .Must(BeValidUrl).When(x => !string.IsNullOrWhiteSpace(x.PictureUrl))
            .WithMessage("Picture URL must be a valid URL (e.g. /uploads/products/abc.jpg or https://example.com/img.png).");

        RuleFor(x => x.Price)
            .NotNull().WithMessage("Price is required.");

        RuleFor(x => x.Price.Amount)
            .GreaterThan(0).WithMessage("Product price must be greater than zero.")
            .When(x => x.Price is not null);

        RuleFor(x => x.Price.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-letter ISO 4217 code (e.g. USD, IRR).")
            .When(x => x.Price is not null);

        RuleFor(x => x.InitialStockQuantity)
            .GreaterThanOrEqualTo(0).WithMessage("Initial stock quantity cannot be negative.")
            .LessThanOrEqualTo(MaxStockQuantity)
            .WithMessage($"Initial stock quantity cannot exceed {MaxStockQuantity:N0}.");

        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        // Lightweight intra-product consistency: you can't specify a
        // SubSubCategory without a SubCategory. The cross-aggregate check
        // (SubCategory actually belongs to Category) is done by the handler.
        RuleFor(x => x)
            .Must(x => x.SubCategoryId is not null || x.SubSubCategoryId is null)
            .WithMessage("Cannot specify a SubSubCategoryId without a SubCategoryId.");

        // ── Per-group purchase limits ────────────────────────────────────
        // Each entry must have a non-empty GroupName (1..100 chars, matching
        // the domain CustomerGroupPurchaseLimit guard) and a Limit >= 1.
        // The handler also checks for duplicate group names across the list
        // (a list-level invariant that requires full-list context, which
        // FluentValidation can do but is cleaner in the handler).
        //
        // WHY .When(...) instead of `x => x.PurchaseLimits ?? Array.Empty<...>()`:
        //   FluentValidation's RuleForEach needs to infer a property name from
        //   the lambda body. A null-coalescing expression (??) breaks that
        //   inference — it throws InvalidOperationException at validation time:
        //     "Could not infer property name for expression: x => (x.PurchaseLimits ?? Empty())"
        //   The fix is to pass the property directly (which FluentValidation
        //   CAN infer) and guard the whole rule with .When(...) so an empty
        //   or null list simply skips validation (no entries to validate).
        RuleForEach(x => x.PurchaseLimits)
            .ChildRules(entry =>
            {
                entry.RuleFor(e => e.GroupName)
                    .NotEmpty().WithMessage("Group name is required for each purchase limit.")
                    .MaximumLength(100).WithMessage("Group name cannot exceed 100 characters.");

                entry.RuleFor(e => e.Limit)
                    .GreaterThanOrEqualTo(1).WithMessage("Purchase limit must be at least 1.")
                    .LessThanOrEqualTo(10_000).WithMessage("Purchase limit cannot exceed 10,000.");
            })
            .When(x => x.PurchaseLimits is { Count: > 0 });
    }

    /// <summary>
    /// Accepts BOTH relative URLs (e.g. "/uploads/products/abc.jpg" — what
    /// our own /api/product-image endpoint returns) AND absolute URLs
    /// (e.g. "https://cdn.example.com/img.png" — for external images).
    /// Uri.TryCreate with UriKind.AbsoluteOrRelative is used so that
    /// relative paths from our upload endpoint are not rejected.
    /// </summary>
    private static bool BeValidUrl(string? url) => Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out _);
}