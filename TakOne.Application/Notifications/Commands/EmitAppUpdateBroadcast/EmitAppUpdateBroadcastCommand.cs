using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;

/// <summary>
/// System-emitted "app updated" broadcast, sent by
/// <c>AppUpdateBroadcasterHostedService</c> at app startup when the running
/// assembly version differs from <c>SystemSettings.LastKnownAppVersion</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>NO AUTHORIZATION ATTRIBUTE</b>: this command is NOT marked
/// <c>[RequireRoles(...)]</c> or <c>[RequireAuthentication]</c> because it's
/// dispatched by a trusted in-process hosted service, not by an HTTP request.
/// Wolverine's <c>AuthorizationPolicyVerifier</c> middleware only scans types
/// whose name ends with "Command" or "Query" for auth attributes — but since
/// this command has NO auth attribute, the middleware skips it. The only
/// caller is the hosted service, which is itself only callable from inside
/// the application process. There is no way for an external user to dispatch
/// this command (the IMessageBus is not exposed over HTTP).
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
public sealed record EmitAppUpdateBroadcastCommand(
    string Title,
    string Message);
