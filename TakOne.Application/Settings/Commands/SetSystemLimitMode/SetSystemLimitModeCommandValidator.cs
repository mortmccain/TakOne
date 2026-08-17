using FluentValidation;
using TakOne.Domain.Common.Enums;

namespace TakOne.Application.Settings.Commands.SetSystemLimitMode;

public sealed class SetSystemLimitModeCommandValidator : AbstractValidator<SetSystemLimitModeCommand>
{
    public SetSystemLimitModeCommandValidator()
    {
        // LimitMode enum starts at 1 (CountOnly=1) so the default
        // uninitialized value (0) is invalid. IsInEnum catches both
        // "out of range" AND "0 (Uninitialized)" since 0 is not a defined
        // member of the enum.
        //
        // The Domain's SystemSettings.EnsureLimitModeValid guard is the
        // hard backstop — it explicitly rejects enum value 0.
        RuleFor(x => x.NewMode)
            .IsInEnum().WithMessage("Limit mode must be a valid value (CountOnly=1, SalaryOnly=2, Both=3).");
    }
}