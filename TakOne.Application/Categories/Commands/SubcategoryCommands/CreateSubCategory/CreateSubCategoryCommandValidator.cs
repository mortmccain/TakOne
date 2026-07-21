using FluentValidation;

namespace TakOne.Application.Categories.Commands.SubcategoryCommands.CreateSubCategory;

/// <summary>
/// FluentValidation validator for <see cref="CreateSubCategoryCommand"/>.
///
/// Only checks primitive, self-contained properties.
/// </summary>
public sealed class CreateSubCategoryCommandValidator : AbstractValidator<CreateSubCategoryCommand>
{
    public const int MaxNameLength = 100;

    public CreateSubCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("SubCategory name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"SubCategory name cannot exceed {MaxNameLength} characters.");
    }
}