using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Notifications.Queries.GetNotificationPreferences;

/// <summary>
/// Loads the current user's notification preferences — one entry per
/// <see cref="TakOne.Domain.Notifications.Enums.NotificationKind"/> with
/// the user's mute flag (false for kinds with no persisted preference
/// row — the sparse default).
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE INVARIANT</b>: the handler resolves <c>userId</c> from
/// <c>ICurrentUserService</c> — the caller cannot read another user's
/// preferences.
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — every
/// authenticated user manages their own notification preferences from
/// the Settings page.
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed record GetNotificationPreferencesQuery;
