using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Repository abstraction for the <see cref="NotificationPreference"/>
/// aggregate (per-user, per-kind mute flags).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN INTERFACE IN APPLICATION</b>: same dependency-inversion
/// rationale as <see cref="INotificationRepository"/> — handlers depend
/// on this abstraction, never on <c>ApplicationDbContext</c>.
/// </para>
/// <para>
/// <b>TWO CONSUMER FAMILIES</b>:
/// <list type="bullet">
///   <item><b>Suppression (hot path)</b> — the NotifyOn* Wolverine event
///         handlers call <see cref="IsMutedAsync"/> before creating a
///         notification row, and <c>BroadcastFanout</c> calls
///         <see cref="GetMutedUserIdsAsync"/> once per broadcast to skip
///         muted recipients in bulk.</item>
///   <item><b>Settings UI (cold path)</b> — the settings page loads all
///         preferences (<see cref="GetAllForUserAsync"/>) and upserts a
///         single one (<see cref="GetForUserAsync"/> +
///         <see cref="AddAsync"/> via the command handler).</item>
/// </list>
/// </para>
/// <para>
/// <b>SPARSE SEMANTICS</b>: no row = not muted. <see cref="IsMutedAsync"/>
/// returning <c>false</c> for a user with zero preference rows is the
/// normal case, not an error.
/// </para>
/// </remarks>
public interface INotificationPreferenceRepository
{
    /// <summary>
    /// True if the user has muted the given kind. False when no preference
    /// row exists (sparse default: not muted) or the row exists with
    /// <c>IsMuted == false</c>.
    /// </summary>
    Task<bool> IsMutedAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads ALL preference rows for a user. Users typically have zero or
    /// a few rows (sparse storage) — this is the settings page's single
    /// load call.
    /// </summary>
    Task<IReadOnlyList<NotificationPreference>> GetAllForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the single preference row for (user, kind), or null when none
    /// exists. Used by the upsert command handler to decide between
    /// "toggle existing row" and "create new row".
    /// </summary>
    Task<NotificationPreference?> GetForUserAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of user Ids who have muted the given kind. Used by
    /// <c>BroadcastFanout</c> to skip muted recipients in bulk — ONE query
    /// for the whole fanout instead of one per recipient.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetMutedUserIdsAsync(
        NotificationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new preference row. The settings-page upsert path (the
    /// only creator of rows — suppression never writes).
    /// </summary>
    Task AddAsync(
        NotificationPreference preference,
        CancellationToken cancellationToken = default);
}
