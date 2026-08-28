using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Common.Events;

/// <summary>
/// Raised when the singleton <see cref="Entities.SystemSettings"/>'s
/// <c>LastKnownAppVersion</c> is updated by
/// <c>AppUpdateBroadcasterHostedService</c> at startup. Subscribers
/// can use this for audit / diagnostics.
/// </summary>
public sealed class SystemSettingsAppVersionUpdatedDomainEvent : BaseDomainEvent
{
    public string? PreviousVersion { get; }
    public string NewVersion { get; }

    public SystemSettingsAppVersionUpdatedDomainEvent(string? previousVersion, string newVersion)
    {
        PreviousVersion = previousVersion;
        NewVersion = newVersion;
    }
}
