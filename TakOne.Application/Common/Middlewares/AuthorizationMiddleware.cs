using System.Reflection;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Wolverine middleware that runs BEFORE each message handler. Enforces
/// the authorization policy declared on the message type via
/// <see cref="RequireRolesAttribute"/>,
/// <see cref="RequireAuthenticationAttribute"/>, or
/// <see cref="RequireSystemInternalAttribute"/>.
///
/// FAIL-CLOSED POLICY (Issue #08):
///   If a command/query has NONE of the three attributes, the middleware
///   REJECTS it. The <see cref="AuthorizationPolicyVerifier"/> runs at
///   startup to catch missing attributes at app-launch time, but this
///   runtime check is the defense-in-depth backstop.
///
/// THREE POLICIES:
///   <list type="bullet">
///     <item><c>[RequireRoles(Roles.Admin, ...)]</c> — the current user
///           must be authenticated AND in at least one of the listed roles.</item>
///     <item><c>[RequireAuthentication]</c> — the current user must be
///           authenticated (no role restriction).</item>
///     <item><c>[RequireSystemInternal]</c> — the message is dispatched by
///           trusted in-process code (hosted services, domain-event side
///           effects) and has NO user context. The middleware BYPASSES the
///           user-auth check entirely. The trust boundary is enforced by
///           IMessageBus only being resolvable from in-process DI —
///           external HTTP requests do not dispatch Wolverine messages.</item>
///   </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>ENFORCEMENT MECHANISM — THROW, NOT RETURN (CRITICAL):</b>
/// </para>
/// <para>
/// Denials THROW <see cref="MessageAuthorizationException"/>. This mirrors
/// Wolverine's own FluentValidation integration (whose failure action throws
/// <c>ValidationException</c>) and is the only mechanism that reliably stops
/// the handler: a <c>Before</c> method returning <c>object?</c>/<c>Result</c>
/// does NOT short-circuit the generated handler chain in Wolverine 6.x
/// unless the exact return type is registered via <c>UseResultType&lt;T&gt;</c>,
/// and even a registered result type short-circuit returns <c>null</c> to
/// <c>InvokeAsync&lt;Result&lt;T&gt;&gt;</c> callers (an NRE in every page).
/// The thrown exception propagates to the dispatching page's existing
/// try/catch, exactly like validation failures.
/// </para>
/// <para>
/// <b>DOMAIN EVENTS ARE EXEMPT:</b>
/// Wolverine applies middleware policies to EVERY handler chain, including
/// domain-event handlers (NotifyOn*, broadcast fanout, …). Domain events are
/// raised by aggregates inside trusted handlers — they have no user context
/// and never carry the three attributes. Running the fail-closed check on
/// them would break ALL notification fanout, so messages whose type name
/// ends with "DomainEvent" (the project's naming convention for events) are
/// skipped up-front. The same convention heuristic is used by
/// <see cref="AuthorizationPolicyVerifier"/> for Command/Query discovery.
/// </para>
/// <para>
/// <b>WOLVERINE MIDDLEWARE PARAMETER CONVENTION (CRITICAL):</b>
/// The <c>Before</c> method's parameter MUST be <c>Envelope envelope</c>
/// (or a concrete message type, or <c>CancellationToken</c>, or services
/// from DI). It MUST NOT be <c>object message</c>.
///
/// If you use <c>object message</c>, Wolverine 6.x's code generator gets
/// confused and generates broken code:
///
///   <code>
///     var result_of_Before = authorizationMiddleware.Before(result_of_Before);
///   </code>
///
/// It passes the <c>result_of_Before</c> variable (which is being declared
/// on this very line) as the <c>message</c> argument -- a circular
/// reference. The generated code fails to compile with:
///
///   <c>CS0841: Cannot use local variable 'result_of_Before' before it is
///   declared</c>
///
/// Using <c>Envelope envelope</c> (the same pattern as
/// <see cref="LoggingMiddleware"/> and <see cref="PerformanceMiddleware"/>)
/// gives Wolverine a concrete, well-known parameter to pass
/// (<c>context.Envelope</c>), and we read the message from
/// <c>envelope.Message</c> inside the method.
/// </para>
/// </remarks>
public class AuthorizationMiddleware
{
    private readonly ICurrentUserService _currentUser;

    public AuthorizationMiddleware(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>
    /// Wolverine convention: a method named <c>Before</c> (or
    /// <c>BeforeAsync</c>) runs before the handler. A denial throws
    /// <see cref="MessageAuthorizationException"/> (see the class remarks
    /// for why throwing — rather than returning — is the only reliable
    /// short-circuit); returning normally continues to the handler.
    /// </summary>
    /// <param name="envelope">
    /// The Wolverine envelope containing the message being handled. We read
    /// <c>envelope.Message</c> to get the command/query object.
    /// </param>
    public void Before(Envelope envelope)
    {
        var message = envelope.Message;
        if (message is null)
            return; // Nothing to authorize -- let the handler deal with it.

        var messageType = message.GetType();

        // Domain events are raised by aggregates inside already-authorized
        // handlers and never carry user-auth attributes. They are exempt.
        // See the class remarks (a naive fail-closed check here would break
        // every notification fanout).
        if (messageType.Name.EndsWith("DomainEvent", StringComparison.Ordinal))
            return;

        var requireRolesAttr = messageType.GetCustomAttribute<RequireRolesAttribute>();
        var requireAuthAttr = messageType.GetCustomAttribute<RequireAuthenticationAttribute>();
        var requireSystemInternalAttr = messageType.GetCustomAttribute<RequireSystemInternalAttribute>();

        // --------------------------------------------------------------
        // FAIL-CLOSED (Issue #08):
        //   If NONE of the three authorization attributes is present,
        //   REJECT the message. The original implementation returned
        //   null here (fail-OPEN), which meant any command without the
        //   attribute was dispatched without any auth check. The
        //   AuthorizationPolicyVerifier catches this at startup, but
        //   this runtime check is the defense-in-depth backstop for
        //   messages that somehow bypass the startup scan (e.g. a
        //   dynamically-constructed message type).
        // --------------------------------------------------------------
        if (requireRolesAttr is null && requireAuthAttr is null && requireSystemInternalAttr is null)
        {
            // Wire-format prefix "UE|" — see ErrorDisplayService.Localize
            // in the WebUI for the recognizer path. The opaque 7-char
            // code (AuthorizationMiddleware_PolicyMissing) maps to this
            // file:line in the developer reference PDF; the user sees
            // "An unexpected error occurred. Error code: 27JSF84".
            throw new MessageAuthorizationException(
                $"UE|{UnexpectedErrorCodes.AuthorizationMiddleware_PolicyMissing}");
        }

        // --------------------------------------------------------------
        // SYSTEM-INTERNAL MESSAGES (e.g. AppUpdateBroadcasterHostedService's
        // EmitAppUpdateBroadcastCommand): bypass the user-auth check.
        // These messages are dispatched by trusted in-process code with
        // no current user context. The trust boundary is that IMessageBus
        // is only resolvable from in-process DI — external HTTP requests
        // do not dispatch Wolverine messages directly.
        // --------------------------------------------------------------
        if (requireSystemInternalAttr is not null)
        {
            return; // Trusted system caller — continue to the handler.
        }

        // --------------------------------------------------------------
        // USER-DISPATCHED MESSAGES: require authentication. Both
        // [RequireAuthentication] and [RequireRoles] imply an
        // authenticated current user.
        // --------------------------------------------------------------
        if (!_currentUser.IsAuthenticated)
            throw new MessageAuthorizationException("Authentication required.");

        // [RequireAuthentication] only checks authentication (already done
        // above) — no role check needed.
        if (requireAuthAttr is not null && requireRolesAttr is null)
            return; // Authenticated — continue to the handler.

        // [RequireRoles] checks that the user is in AT LEAST ONE of the
        // required roles.
        if (requireRolesAttr is not null)
        {
            bool allowed = requireRolesAttr.Roles.Any(r => _currentUser.IsInRole(r));
            if (!allowed)
            {
                throw new MessageAuthorizationException(
                    $"You do not have permission to perform this action. " +
                    $"Required role(s): {string.Join(", ", requireRolesAttr.Roles)}.");
            }
        }

        // Continue to the handler.
    }
}
