using FluentValidation;

namespace TakOne.Application.Categories.Commands.RenameCategory;

/// <summary>
/// FluentValidation validator for <see cref="RenameCategoryCommand"/>.
///
/// Only checks primitive, self-contained properties. Whether the Category
/// exists and whether the new name is unique are the handler's responsibility.
/// </summary>
public sealed class RenameCategoryCommandValidator : AbstractValidator<RenameCategoryCommand>
{
    public const int MaxNameLength = 100;

    public RenameCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");

        RuleFor(x => x.NewName)
            .NotEmpty().WithMessage("New category name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Category name cannot exceed {MaxNameLength} characters.");
    }
}
