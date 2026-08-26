using FluentValidation;
using TakOne.Application.Common.Authorization;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands.SendBroadcastNotification;

/// <summary>
/// FluentValidation validator for <see cref="SendBroadcastNotificationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY VALIDATE HERE (vs. relying on the domain factory's guards)</b>:
/// the domain's <c>BroadcastNotification.Create</c> factory throws
/// <c>DomainException</c> on invalid input. Wolverine's
/// <c>DomainExceptionMiddleware</c> catches that and converts it to a
/// <c>Result.Failure</c>, but the Result's error string is the raw
/// exception message — not a stable, culture-neutral code the UI can
/// localize. Validating here lets us return clean, localizable error
/// codes via <c>NotificationErrors</c>.
/// </para>
/// <para>
/// <b>SCOPE-TARGET CONSISTENCY</b>: enforced by a custom rule. Exactly
/// one of <c>TargetRoleName</c>/<c>TargetGroupId</c>/<c>TargetUserId</c>
/// must be set according to <c>Scope</c>, with all three null when
/// <c>Scope=All</c>.
/// </para>
/// <para>
/// <b>ROLE NAME VALIDATION</b>: when <c>Scope=Role</c>, the
/// <c>TargetRoleName</c> must be one of the canonical role names from
/// <see cref="Roles"/> (Admin/Manager/Employee/ReadOnly/Customer). This
/// catches typos like "Admins" (plural) before they reach the repo.
/// </para>
/// </remarks>
public sealed class SendBroadcastNotificationCommandValidator
    : AbstractValidator<SendBroadcastNotificationCommand>
{
    private static readonly HashSet<string> ValidRoleNames = new(StringComparer.Ordinal)
    {
        Roles.Admin,
        Roles.Manager,
        Roles.Employee,
        Roles.ReadOnly,
        Roles.Customer
    };

    public SendBroadcastNotificationCommandValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithErrorCode("BroadcastTitleRequired")
            .MaximumLength(200).WithErrorCode("BroadcastTitleTooLong");

        RuleFor(x => x.Message)
            .NotEmpty().WithErrorCode("BroadcastMessageRequired")
            .MaximumLength(1000).WithErrorCode("BroadcastMessageTooLong");

        RuleFor(x => x.Scope)
            .IsInEnum().WithErrorCode("BroadcastScopeInvalid");

        // ── Scope-target consistency ──
        // Exactly one target field set per the scope; all three null when All.
        RuleFor(x => x).Custom((cmd, ctx) =>
        {
            switch (cmd.Scope)
            {
                case BroadcastScope.All:
                    if (!string.IsNullOrEmpty(cmd.TargetRoleName)
                        || cmd.TargetGroupId.HasValue
                        || cmd.TargetUserId.HasValue)
                    {
                        ctx.AddFailure("BroadcastScopeAllTargetsMustBeNull",
                            "Scope=All must not specify any target.");
                    }
                    break;

                case BroadcastScope.Role:
                    if (string.IsNullOrWhiteSpace(cmd.TargetRoleName))
                    {
                        ctx.AddFailure("BroadcastScopeRoleRequiresTargetRoleName",
                            "Scope=Role requires TargetRoleName.");
                    }
                    else if (!ValidRoleNames.Contains(cmd.TargetRoleName!))
                    {
                        ctx.AddFailure("BroadcastRoleNameInvalid",
                            $"TargetRoleName must be one of: {string.Join(", ", ValidRoleNames)}.");
                    }
                    if (cmd.TargetGroupId.HasValue || cmd.TargetUserId.HasValue)
                    {
                        ctx.AddFailure("BroadcastScopeRoleExtraTargets",
                            "Scope=Role must not specify TargetGroupId or TargetUserId.");
                    }
                    break;

                case BroadcastScope.Group:
                    if (!cmd.TargetGroupId.HasValue || cmd.TargetGroupId.Value == Guid.Empty)
                    {
                        ctx.AddFailure("BroadcastScopeGroupRequiresTargetGroupId",
                            "Scope=Group requires a non-empty TargetGroupId.");
                    }
                    if (!string.IsNullOrEmpty(cmd.TargetRoleName) || cmd.TargetUserId.HasValue)
                    {
                        ctx.AddFailure("BroadcastScopeGroupExtraTargets",
                            "Scope=Group must not specify TargetRoleName or TargetUserId.");
                    }
                    break;

                case BroadcastScope.User:
                    if (!cmd.TargetUserId.HasValue || cmd.TargetUserId.Value == Guid.Empty)
                    {
                        ctx.AddFailure("BroadcastScopeUserRequiresTargetUserId",
                            "Scope=User requires a non-empty TargetUserId.");
                    }
                    if (!string.IsNullOrEmpty(cmd.TargetRoleName) || cmd.TargetGroupId.HasValue)
                    {
                        ctx.AddFailure("BroadcastScopeUserExtraTargets",
                            "Scope=User must not specify TargetRoleName or TargetGroupId.");
                    }
                    break;
            }
        });
    }
}
