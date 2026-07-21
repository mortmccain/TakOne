using FluentValidation;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.ActivateSubSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="ActivateSubSubCategoryCommand"/>.
/// </summary>
public sealed class ActivateSubSubCategoryCommandValidator : AbstractValidator<ActivateSubSubCategoryCommand>
{
    public ActivateSubSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");

        RuleFor(x => x.SubSubCategoryId)
            .NotEmpty().WithMessage("SubSubCategory ID is required.");
    }
}