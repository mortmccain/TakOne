using FluentValidation;
using TakOne.Application.Products.Commands.CreateProduct;

namespace TakOne.Application.Products.Commands.UpdateProductDetails;

/// <summary>
/// FluentValidation validator for <see cref="UpdateProductDetailsCommand"/>.
/// Primitive checks only — no database access. Mirrors the CreateProduct
/// validator's field-level rules (length bounds, format checks) but skips
/// category/stock validation since this command doesn't touch those.
/// </summary>
public sealed class UpdateProductDetailsCommandValidator : AbstractValidator<UpdateProductDetailsCommand>
{
    public UpdateProductDetailsCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(CreateProductCommandValidator.MaxNameLength)
            .WithMessage($"Product name cannot exceed {CreateProductCommandValidator.MaxNameLength} characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Product description is required.")
            .MaximumLength(CreateProductCommandValidator.MaxDescriptionLength)
            .WithMessage($"Product description cannot exceed {CreateProductCommandValidator.MaxDescriptionLength} characters.");

        RuleFor(x => x.PictureUrl)
            .MaximumLength(CreateProductCommandValidator.MaxPictureUrlLength)
            .WithMessage($"Picture URL cannot exceed {CreateProductCommandValidator.MaxPictureUrlLength} characters.")
            .Must(BeValidUrl).When(x => !string.IsNullOrWhiteSpace(x.PictureUrl))
            .WithMessage("Picture URL must be a valid absolute URL (e.g. https://...).");

        RuleFor(x => x.Price)
            .NotNull().WithMessage("Price is required.");

        RuleFor(x => x.Price.Amount)
            .GreaterThan(0).WithMessage("Product price must be greater than zero.")
            .When(x => x.Price is not null);

        RuleFor(x => x.Price.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-letter ISO 4217 code (e.g. USD, IRR).")
            .When(x => x.Price is not null);
    }

    private static bool BeValidUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out _);
}