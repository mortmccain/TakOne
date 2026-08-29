using TakOne.Application.Common.Authorization;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands.SetNotificationMuted;

/// <summary>
/// Sets the current user's mute preference for ONE notification kind.
/// Idempotent — muting an already-muted kind (or unmuting a
/// never-muted kind) succeeds without writing.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE INVARIANT</b>: the handler resolves <c>userId</c> from
/// <c>ICurrentUserService</c> and applies the preference to THAT user.
/// The caller cannot flip another user's preferences (anti-CSRF).
/// </para>
/// <para>
/// <b>EFFECT TIMING</b>: the mute takes effect on notification CREATION
/// from the very next domain event onwards — it is not retroactive.
/// Already-delivered rows of the kind stay in the feed and can be
/// dismissed as usual. See the <c>NotificationPreference</c> aggregate's
/// SUPPRESSION POINT remarks.
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any
/// authenticated user manages their own preferences. Muting is a purely
/// personal choice; no staff role is required (and no staff role can
/// mute on behalf of another user — by design).
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed record SetNotificationMutedCommand(
    NotificationKind Kind,
    bool IsMuted);
