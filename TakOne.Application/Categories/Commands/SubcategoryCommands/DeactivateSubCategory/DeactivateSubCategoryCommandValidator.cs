using FluentValidation;

namespace TakOne.Application.Categories.Commands.DeactivateSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="DeactivateSubCategoryCommand"/>.
/// </summary>
public sealed class DeactivateSubCategoryCommandValidator : AbstractValidator<DeactivateSubCategoryCommand>
{
    public DeactivateSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");
    }
}