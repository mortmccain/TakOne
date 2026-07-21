using FluentValidation;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="CreateSubSubCategoryCommand"/>.
/// </summary>
public sealed class CreateSubSubCategoryCommandValidator : AbstractValidator<CreateSubSubCategoryCommand>
{
    public const int MaxNameLength = 100;

    public CreateSubSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("SubSubCategory name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"SubSubCategory name cannot exceed {MaxNameLength} characters.");
    }
}