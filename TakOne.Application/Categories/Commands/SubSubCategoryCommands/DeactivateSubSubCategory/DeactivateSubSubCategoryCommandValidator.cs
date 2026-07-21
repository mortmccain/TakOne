using FluentValidation;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="DeactivateSubSubCategoryCommand"/>.
/// </summary>
public sealed class DeactivateSubSubCategoryCommandValidator : AbstractValidator<DeactivateSubSubCategoryCommand>
{
    public DeactivateSubSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");

        RuleFor(x => x.SubSubCategoryId)
            .NotEmpty().WithMessage("SubSubCategory ID is required.");
    }
}