using TakOne.Application.Common.Authorization;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;

/// <summary>
/// System-emitted "app updated" broadcast, sent by
/// <c>AppUpdateBroadcasterHostedService</c> at app startup when the running
/// assembly version differs from <c>SystemSettings.LastKnownAppVersion</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>AUTHORIZATION POLICY: <c>[RequireSystemInternal]</c></b>
/// <list type="bullet">
///   <item>This command is marked
///         <c>[RequireSystemInternal]</c> — the third authorization policy
///         alongside <c>[RequireRoles]</c> and <c>[RequireAuthentication]</c>.
///         It is NOT dispatched by an HTTP request or a Blazor circuit acting
///         on behalf of a user; the only caller is the trusted in-process
///         <c>AppUpdateBroadcasterHostedService</c>.</item>
///   <item>The <c>AuthorizationMiddleware</c> recognizes
///         <c>[RequireSystemInternal]</c> and BYPASSES the user-auth check
///         (the hosted service has no <c>ICurrentUserService</c> identity).
///         The <c>AuthorizationPolicyVerifier</c> accepts it as a valid
///         policy so the app launches cleanly.</item>
///   <item>The trust boundary is enforced by <c>IMessageBus</c> only being
///         resolvable from in-process DI — external HTTP requests do not
///         dispatch Wolverine messages directly. A reviewer can grep for
///         <c>[RequireSystemInternal]</c> to audit exactly which messages
///         are dispatched by system code.</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY NOT <c>[RequireAuthentication]</c>?</b> That attribute would
/// reject the message at runtime — the middleware checks
/// <c>_currentUser.IsAuthenticated</c> and the hosted service has no
/// current user, so the broadcast would never go out. And leaving the
/// message unattributed would trip the fail-closed verifier at startup
/// (the exact crash the user hit after Round 12). The dedicated
/// <c>[RequireSystemInternal]</c> attribute is the correct, auditable
/// way to model this trust level.
/// </para>
/// <para>
/// <b>WHY A SEPARATE COMMAND (vs. reusing SendBroadcastNotificationCommand)</b>:
/// <list type="bullet">
///   <item><c>SendBroadcastNotificationCommand</c> is marked
///         <c>[RequireRoles(Admin)]</c> — the hosted service has no current
///         user, so the auth check would reject it.</item>
///   <item>The admin command emits <c>Kind=Broadcast</c> fanout rows;
///         the app-update command emits <c>Kind=AppUpdate</c> fanout rows
///         (so the UI can render the app-update notification with a
///         distinct icon + a "Reload" button).</item>
///   <item>The admin command records <c>SentByUserId = currentUser.UserId</c>;
///         the app-update command records <c>SentByUserId = Guid.Empty</c>
///         (system-emitted, no human author).</item>
/// </list>
/// Both commands delegate the actual fanout to the shared
/// <see cref="BroadcastFanout"/> helper — DRY, single source of truth for
/// the transactional fanout logic.
/// </para>
/// <para>
/// <b>SCOPE IS ALWAYS All</b>: the app-update notification reaches every
/// active user. The title/message come from the caller (the hosted service
/// composes them from the running version). The handler does NOT validate
/// the scope-target consistency the way the admin command does — it
/// hard-codes Scope=All, all targets null, which is always valid.
/// </para>
/// </remarks>
[RequireSystemInternal]
public sealed record EmitAppUpdateBroadcastCommand(
    string Title,
    string Message);
