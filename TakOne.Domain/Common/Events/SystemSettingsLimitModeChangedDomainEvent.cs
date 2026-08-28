using TakOne.Domain.Common.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Common.Events;

/// <summary>
/// Raised when the system-wide <c>LimitMode</c> is changed on the
/// singleton <see cref="Entities.SystemSettings"/> row via
/// <see cref="Entities.SystemSettings.UpdateLimitMode"/>. Carries
/// both the previous and new mode so subscribers (audit log, cache
/// invalidation) can react.
/// </summary>
public sealed class SystemSettingsLimitModeChangedDomainEvent : BaseDomainEvent
{
    public LimitMode PreviousMode { get; }
    public LimitMode NewMode { get; }

    public SystemSettingsLimitModeChangedDomainEvent(LimitMode previousMode, LimitMode newMode)
    {
        PreviousMode = previousMode;
        NewMode = newMode;
    }
}
