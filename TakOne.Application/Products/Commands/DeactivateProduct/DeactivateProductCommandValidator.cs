using FluentValidation;

namespace TakOne.Application.Products.Commands.DeactivateProduct;

/// <summary>
/// Validator for <see cref="DeactivateProductCommand"/>.
///
/// Only validates the ProductId (must be a non-empty Guid). There's no
/// stock value to validate — the command unconditionally sets stock to 0,
/// and the domain's <c>SetStock(0)</c> is always valid.
/// </summary>
public sealed class DeactivateProductCommandValidator : AbstractValidator<DeactivateProductCommand>
{
    public DeactivateProductCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");
    }
}