using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Notifications.Commands.DeleteNotification;

/// <summary>
/// HARD-DELETES one of the caller's notifications (Round 4 — the
/// per-notification dismiss button on the notifications pages).
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE INVARIANT</b>: the delete predicate carries the caller's
/// user id IN SQL (<c>DELETE ... WHERE Id = @id AND UserId = @user</c>),
/// so a caller can never dismiss someone else's notification. A foreign
/// id deletes zero rows and surfaces as the same "not found" failure as
/// a missing id (anti-enumeration).
/// </para>
/// <para>
/// <b>WHY HARD DELETE (not a Dismissed flag)</b>: a dismissed
/// notification carries no business meaning — it's the user saying "I'm
/// done looking at this". Keeping a flag would make every feed query
/// filter on it forever and complicate the unread count; the row's
/// absence IS the dismissal. (The underlying business records — the
/// sale, its audit trail, the broadcast — are untouched; this only
/// removes the user's own inbox copy.)
/// </para>
/// <para>
/// <b>NO BROADCAST</b>: a pure inbox-state mutation — no domain event.
/// The user's OTHER devices refresh the list/unread count on their next
/// query or live-refresh ping, the same as MarkAsRead.
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any
/// authenticated user can dismiss their own notifications.
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed record DeleteNotificationCommand(Guid NotificationId);
