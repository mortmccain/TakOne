using TakOne.Domain.Common.Enums;
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
    public static SystemSettings Load(LimitMode limitMode, DateTime updatedAt)
    {
        return new SystemSettings(limitMode, updatedAt);
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

        LimitMode = newMode;
        UpdatedAt = DateTime.UtcNow;
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureLimitModeValid(LimitMode mode)
    {
        if (!Enum.IsDefined(typeof(LimitMode), mode))
        {
            throw new DomainException(
                $"LimitMode must be one of: {string.Join(", ", Enum.GetNames(typeof(LimitMode)))}.");
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