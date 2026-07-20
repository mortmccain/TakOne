using FluentValidation;

namespace TakOne.Application.Products.Commands.UpdateProductCategory;

public sealed class UpdateProductCategoryCommandValidator : AbstractValidator<UpdateProductCategoryCommand>
{
    public UpdateProductCategoryCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("Product ID is required.");

        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        // Self-contained consistency: SubSub requires Sub. The cross-aggregate
        // check (Sub actually belongs to Category) is the handler's job.
        RuleFor(x => x)
            .Must(x => x.SubCategoryId is not null || x.SubSubCategoryId is null)
            .WithMessage("Cannot specify a SubSubCategoryId without a SubCategoryId.");
    }
}