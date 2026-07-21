using FluentValidation;

namespace TakOne.Application.Categories.Commands.CreateCategory;

/// <summary>
/// FluentValidation validator for <see cref="CreateCategoryCommand"/>.
///
/// Only checks primitive, self-contained properties — does NOT touch the
/// database. The cross-aggregate check (name uniqueness) is the handler's
/// responsibility, because it requires repository access and should produce
/// a business-meaningful error message rather than a field-level validation
/// message.
/// </summary>
public sealed class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    public const int MaxNameLength = 100;

    public CreateCategoryCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Category name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Category name cannot exceed {MaxNameLength} characters.");
    }
}
