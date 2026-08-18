using FluentValidation;

namespace TakOne.Application.Customers.Commands.BulkApplyDefaultsForGroup;

/// <summary>
/// Validator for <see cref="BulkApplyDefaultsForGroupCommand"/>.
///
/// Only validates the GroupId is non-empty at this layer. The handler
/// performs the substantive checks (group exists, group is active).
/// </summary>
public sealed class BulkApplyDefaultsForGroupCommandValidator : AbstractValidator<BulkApplyDefaultsForGroupCommand>
{
    public BulkApplyDefaultsForGroupCommandValidator()
    {
        RuleFor(x => x.GroupId)
            .NotEmpty().WithMessage("Group ID is required.");
    }
}