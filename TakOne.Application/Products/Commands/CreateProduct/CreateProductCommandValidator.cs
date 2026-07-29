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