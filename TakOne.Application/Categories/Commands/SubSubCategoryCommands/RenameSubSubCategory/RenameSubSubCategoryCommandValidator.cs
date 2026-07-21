using FluentValidation;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="RenameSubSubCategoryCommand"/>.
/// </summary>
public sealed class RenameSubSubCategoryCommandValidator : AbstractValidator<RenameSubSubCategoryCommand>
{
    public const int MaxNameLength = 100;

    public RenameSubSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");

        RuleFor(x => x.SubSubCategoryId)
            .NotEmpty().WithMessage("SubSubCategory ID is required.");

        RuleFor(x => x.NewName)
            .NotEmpty().WithMessage("New SubSubCategory name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"SubSubCategory name cannot exceed {MaxNameLength} characters.");
    }
}