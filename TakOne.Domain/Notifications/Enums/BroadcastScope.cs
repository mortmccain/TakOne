namespace TakOne.Domain.Notifications.Enums;

/// <summary>
/// The audience selector for an admin-authored
/// <see cref="Entities.BroadcastNotification"/>. Determines which users
/// receive a fanout <see cref="Entities.Notification"/> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN ENUM (not a string)</b>: stable discriminator for DB indexing,
/// resilient to renaming the user-facing label, and the UI layer can map
/// each value to a localized label without a magic-string lookup. Same
/// rationale as <see cref="NotificationKind"/>.
/// </para>
/// <para>
/// <b>SCOPE-TARGET CONSISTENCY</b>: the <see cref="Entities.BroadcastNotification"/>
/// aggregate's factory enforces that exactly one of
/// <c>TargetRoleName</c>/<c>TargetGroupId</c>/<c>TargetUserId</c> is set
/// according to the chosen scope, and that all three are null when
/// <see cref="All"/> is chosen. This invariant is checked at the domain
/// boundary so a malformed command (e.g. Scope=All but TargetUserId=foo)
/// is rejected before any fanout row is created.
/// </para>
/// <para>
/// <b>EXTENSIBILITY</b>: append new values (e.g. <c>Department</c>) when
/// new audience shapes are needed — DO NOT reuse existing values, since
/// persisted <c>BroadcastNotifications.Scope</c> column references these
/// integers.
/// </para>
/// </remarks>
public enum BroadcastScope
{
    /// <summary>
    /// Every active user in the system receives the broadcast. The
    /// <c>TargetRoleName</c>/<c>TargetGroupId</c>/<c>TargetUserId</c> fields
    /// MUST be null. Used by the auto-emitted "app updated" notification
    /// (<see cref="NotificationKind.AppUpdate"/>) and by admin-authored
    /// global announcements.
    /// </summary>
    All = 1,

    /// <summary>
    /// Every active user in the given ASP.NET Identity role receives the
    /// broadcast. <c>TargetRoleName</c> MUST be set to a role name from
    /// <see cref="TakOne.Application.Common.Authorization.Roles"/> (e.g.
    /// "Customer", "Employee", "Manager", "Admin", "ReadOnly");
    /// <c>TargetGroupId</c>/<c>TargetUserId</c> MUST be null.
    /// </summary>
    Role = 2,

    /// <summary>
    /// Every active user assigned to the given <see cref="Domain.Customers.Entities.CustomerGroup"/>
    /// receives the broadcast. <c>TargetGroupId</c> MUST be set to a
    /// non-empty Guid; <c>TargetRoleName</c>/<c>TargetUserId</c> MUST be null.
    /// </summary>
    Group = 3,

    /// <summary>
    /// Exactly one user — identified by <c>TargetUserId</c> — receives the
    /// broadcast. <c>TargetUserId</c> MUST be set to a non-empty Guid;
    /// <c>TargetRoleName</c>/<c>TargetGroupId</c> MUST be null.
    /// </summary>
    User = 4
}
