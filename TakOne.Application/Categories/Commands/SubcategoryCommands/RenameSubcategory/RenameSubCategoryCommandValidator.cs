using FluentValidation;

namespace TakOne.Application.Categories.Commands.RenameSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="RenameSubCategoryCommand"/>.
/// </summary>
public sealed class RenameSubCategoryCommandValidator : AbstractValidator<RenameSubCategoryCommand>
{
    public const int MaxNameLength = 100;

    public RenameSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.SubCategoryId)
            .NotEmpty().WithMessage("SubCategory ID is required.");

        RuleFor(x => x.NewName)
            .NotEmpty().WithMessage("New SubCategory name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"SubCategory name cannot exceed {MaxNameLength} characters.");
    }
}