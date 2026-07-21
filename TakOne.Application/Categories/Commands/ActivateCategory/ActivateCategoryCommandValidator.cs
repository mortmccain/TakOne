using FluentValidation;

namespace TakOne.Application.Categories.Commands.ActivateCategory;

/// <summary>
/// FluentValidation validator for <see cref="ActivateCategoryCommand"/>.
/// </summary>
public sealed class ActivateCategoryCommandValidator : AbstractValidator<ActivateCategoryCommand>
{
    public ActivateCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("Category ID is required.");
    }
}
