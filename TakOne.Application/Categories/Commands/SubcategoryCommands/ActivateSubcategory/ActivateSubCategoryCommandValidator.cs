using FluentValidation;

namespace TakOne.Application.Categories.Commands.ActivateSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="ActivateSubCategoryCommand"/>.
/// </summary>
public sealed class ActivateSubCategoryCommandValidator : AbstractValidator<ActivateSubCategoryCommand>
{
    public ActivateSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");
    }
}