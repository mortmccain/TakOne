using TakOne.Domain.Common.Enums;
using TakOne.Domain.Common.Events;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Common.Entities;

/// <summary>
/// Singleton entity holding application-wide configuration that admins
/// can change at runtime via the UI (no app restart required).
///
/// SINGLETON PATTERN (DATABASE-LEVEL):
///   This table contains exactly ONE row, identified by the constant
///   <see cref="SingletonId"/> (<see cref="Guid.Empty"/>). The
///   Infrastructure configuration enforces this with a check
///   constraint on the Id column. The <see cref="ISystemSettingsRepository"/>
///   lazily creates the row with default values if it doesn't exist
///   yet on first read.
///
/// CACHING (Infrastructure layer):
///   Reads are infrequent changes but high-frequency reads — every
///   purchase-limit check reads <see cref="LimitMode"/>. The
///   <c>ISystemSettingsService</c> wraps the repository with an
///   in-process <c>IMemoryCache</c>:
///     - First read: hit DB, populate cache, return.
///     - Subsequent reads: hit cache (microsecond cost).
///     - Admin update via <c>SetSystemLimitModeCommand</c>: write to DB,
///       invalidate cache. Next read re-loads from DB.
///   In steady state, zero DB hits for the settings check.
///
/// WHY A TABLE (vs. appsettings.json):
///   The admin/manager can change the limit mode via the Manage Groups
///   page. This requires runtime persistence without a redeploy.
///   <c>appsettings.json</c> would require a file edit + app restart,
///   which violates that UX requirement.
///
/// WHY NOT PER-GROUP:
///   The limit mode is a system-wide policy decision — it applies to
///   every group, every user, every product. Storing it on
///   <c>CustomerGroup</c> would incorrectly suggest it could vary by
///   group, which it cannot. A separate singleton table makes the
///   global nature of the setting explicit at the schema level.
/// </summary>
public sealed class SystemSettings : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          CONSTANTS
    // ==================================================================================================================================



    /// <summary>
    /// The fixed primary key of the singleton row. Always
    /// <see cref="Guid.Empty"/>. The Infrastructure configuration
    /// enforces that only one row with this Id exists.
    /// </summary>
    public static readonly Guid SingletonId = Guid.Empty;



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The current system-wide limit mode. Controls how purchase limits
    /// are enforced for every customer. See <see cref="LimitMode"/> for
    /// the semantics of each value.
    /// </summary>
    public LimitMode LimitMode { get; private set; }

    /// <summary>
    /// The last-known application version, recorded by
    /// <c>AppUpdateBroadcasterHostedService</c> at startup. Used to detect
    /// "the running assembly version differs from the persisted one" →
    /// a deployment happened → broadcast an <see cref="TakOne.Domain.Notifications.Enums.NotificationKind.AppUpdate"/>
    /// notification to every user via the existing broadcast pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NULL ON FRESH INSTALL</b>: a brand-new DB has no row yet, the
    /// repo lazily creates it with <see cref="CreateDefault"/> which leaves
    /// this null. The hosted service sees null → treats it as "first boot",
    /// writes the running version, and SKIPS the broadcast (no point
    /// announcing an "update" to users who have nothing to update from).
    /// Subsequent restarts with the same version → no broadcast. Only a
    /// version CHANGE triggers the broadcast.
    /// </para>
    /// <para>
    /// <b>WHY A STRING (not a parsed Version)</b>: the assembly's
    /// <c>InformationalVersion</c> attribute can carry semver + git SHA
    /// suffixes (e.g. <c>1.2.0-beta1+sha.abc123</c>) that don't round-trip
    /// cleanly through <c>System.Version</c>. Storing the raw string and
    /// doing an exact-match comparison is the most robust check.
    /// </para>
    /// <para>
    /// <b>NOT ADMIN-CHANGEABLE</b>: this field is written exclusively by
    /// the hosted service. The admin can SEE it (future Settings page could
    /// surface it) but cannot edit it — it's a system marker, not a runtime
    /// config knob. It lives on <see cref="SystemSettings"/> rather than a
    /// separate table to avoid a new singleton + new repo + new migration
    /// just for one column. Pragmatic DDD: this is system-wide state, same
    /// as <see cref="LimitMode"/>.
    /// </para>
    /// </remarks>
    public string? LastKnownAppVersion { get; private set; }

    /// <summary>
    /// The UTC timestamp of the last update to any field on this row.
    /// Used for audit logging — admins can see when the mode was last
    /// changed and by whom (the "by whom" is in the application-layer
    /// log, not stored here, since the Domain has no concept of users).
    /// </summary>
    public DateTime UpdatedAt { get; private set; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in
    /// application code — use <see cref="CreateDefault"/> or
    /// <see cref="Load"/> instead.
    /// </summary>
    private SystemSettings() : base(SingletonId) { }
#pragma warning restore CS8618

    private SystemSettings(LimitMode limitMode, DateTime updatedAt) : base(SingletonId)
    {
        EnsureLimitModeValid(limitMode);

        LimitMode = limitMode;
        LastKnownAppVersion = null;
        UpdatedAt = updatedAt;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHODS
    // ==================================================================================================================================



    /// <summary>
    /// Creates the singleton row with default values
    /// (<see cref="LimitMode.CountOnly"/> — preserves the original
    /// pre-salary-feature behaviour on fresh installs). Used by the
    /// repository when the row doesn't exist yet.
    /// </summary>
    public static SystemSettings CreateDefault()
    {
        return new SystemSettings(LimitMode.CountOnly, DateTime.UtcNow);
    }

    /// <summary>
    /// Loads the singleton row from persisted state. Used by the
    /// repository to materialize the row from the database.
    /// </summary>
    public static SystemSettings Load(LimitMode limitMode, DateTime updatedAt, string? lastKnownAppVersion)
    {
        var settings = new SystemSettings(limitMode, updatedAt)
        {
            // Bypass the private setter via object initializer — the
            // property has a private setter, so we can't write to it
            // after construction. The initializer runs after the ctor
            // but before the reference is returned, so this is safe.
            LastKnownAppVersion = lastKnownAppVersion
        };
        return settings;
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR
    // ==================================================================================================================================



    /// <summary>
    /// Updates the system-wide limit mode. Called by
    /// <c>SetSystemLimitModeCommandHandler</c> when an admin changes
    /// the mode via the Manage Groups page.
    /// </summary>
    public void UpdateLimitMode(LimitMode newMode)
    {
        EnsureLimitModeValid(newMode);

        if (newMode == LimitMode) return;

        var previousMode = LimitMode;
        LimitMode = newMode;
        UpdatedAt = DateTime.UtcNow;
        AddDomainEvent(new SystemSettingsLimitModeChangedDomainEvent(previousMode, newMode));
    }

    /// <summary>
    /// Records the running assembly's version as the last-known app
    /// version. Called by <c>AppUpdateBroadcasterHostedService</c> at
    /// startup AFTER it has broadcast the <c>AppUpdate</c> notification
    /// (if the version differed). Subsequent restarts with the same
    /// version then short-circuit — no re-broadcast.
    /// </summary>
    /// <remarks>
    /// Idempotent: if <paramref name="version"/> equals the current
    /// <see cref="LastKnownAppVersion"/>, this is a no-op (no spurious
    /// <see cref="UpdatedAt"/> bump, no DB UPDATE generated).
    /// </remarks>
    public void UpdateLastKnownAppVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            // Defensive — the caller (hosted service) always passes a
            // real version string. If somehow empty, no-op rather than
            // throw — we don't want to crash the host on a degenerate input.
            return;
        }

        if (string.Equals(version, LastKnownAppVersion, StringComparison.Ordinal))
        {
            return;
        }

        var previousVersion = LastKnownAppVersion;
        LastKnownAppVersion = version;
        UpdatedAt = DateTime.UtcNow;
        AddDomainEvent(new SystemSettingsAppVersionUpdatedDomainEvent(previousVersion, version));
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureLimitModeValid(LimitMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new DomainException(
                $"LimitMode must be one of: {string.Join(", ", Enum.GetNames<LimitMode>())}.");
        }

        // Reject the implicit-zero value (0) explicitly — our enum starts
        // at 1, so a default-uninitialized LimitMode field would be 0,
        // which is invalid. This catches "I forgot to set it" bugs.
        if ((int)mode == 0)
        {
            throw new DomainException(
                "LimitMode cannot be 0 (Uninitialized). Use one of: CountOnly, SalaryOnly, Both.");
        }
    }
}