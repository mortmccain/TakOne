namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Marks a command/query as a <b>system-internal</b> message: callable only
/// by trusted in-process code (hosted services, system jobs, domain-event
/// handlers) and NOT by an HTTP request or a Blazor circuit acting on behalf
/// of an external user.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHEN TO USE THIS ATTRIBUTE</b>:
/// <list type="bullet">
///   <item>A background <c>BackgroundService</c>/<c>IHostedService</c> that
///         dispatches a Wolverine message at app startup (e.g. the
///         <c>AppUpdateBroadcasterHostedService</c> that fans out an
///         "app updated" notification to every user when the assembly
///         version changes between boots).</item>
///   <item>A domain-event handler that, as a side effect of a user action,
///         publishes another command (the original action already passed
///         the user auth check — the downstream command should not require
///         a second user-context check).</item>
///   <item>A maintenance/migration job that runs on a timer and emits
///         system-level notifications or corrections.</item>
/// </list>
/// In every case the dispatcher is code INSIDE the application process,
/// not an external caller. There is no HTTP request, no
/// <c>HttpContext.User</c>, no <c>ICurrentUserService</c> identity.
/// </para>
/// <para>
/// <b>WHY A THIRD POLICY (not just reusing <c>[RequireAuthentication]</c>)</b>:
/// <list type="bullet">
///   <item><c>[RequireAuthentication]</c> would <b>reject</b> the message at
///         runtime — the middleware checks <c>_currentUser.IsAuthenticated</c>
///         and the hosted service has no current user, so the check fails and
///         the broadcast never goes out.</item>
///   <item>Leaving the message unattributed would <b>trip the fail-closed</b>
///         <c>AuthorizationPolicyVerifier</c> at startup AND the
///         <c>AuthorizationMiddleware</c> at runtime — exactly the bug this
///         attribute was introduced to fix (a system-emitted
///         <c>EmitAppUpdateBroadcastCommand</c> with no attribute crashed
///         the app at startup).</item>
///   <item>A dedicated attribute makes the trust level <b>explicit and
///         auditable</b>: a reviewer scanning the codebase can grep for
///         <c>[RequireSystemInternal]</c> and see exactly which messages are
///         dispatched by system code, rather than reasoning about which
///         "unattributed" messages are intentional vs. forgotten.</item>
/// </list>
/// </para>
/// <para>
/// <b>SECURITY/TRUST BOUNDARY</b>:
/// <list type="bullet">
///   <item>This attribute is a <b>declaration</b>, not an enforcement. It
///         tells the <c>AuthorizationMiddleware</c> "do not require a user
///         identity for this message".</item>
///   <item>The actual security boundary is that <c>IMessageBus</c> is only
///         resolvable from DI by code running <i>inside</i> the application
///         process. External HTTP requests do not dispatch Wolverine messages
///         directly — they go through controllers/Razor components which are
///         themselves user-authenticated and can be authorized at the
///         component level.</item>
///   <item>A handler decorated (transitively) with this attribute should
///         still do <b>defense-in-depth</b> validation of its inputs (e.g.
///         the app-update broadcast handler trusts its title/message
///         arguments because the only caller is the hosted service that
///         composes them from the assembly version — a value the caller
///         controls entirely).</item>
///   <item>Do NOT add this attribute to a message that is ever dispatched
///         from a code path reachable by an external caller. If a Blazor
///         component can trigger it via <c>IMessageBus.InvokeAsync</c>, an
///         authenticated customer could too — use <c>[RequireRoles]</c>
///         or <c>[RequireAuthentication]</c> instead.</item>
/// </list>
/// </para>
/// <para>
/// <b>FAIL-CLOSED POLICY (Issue #08) — EXTENDED</b>:
///   The fail-closed posture is preserved. Every command/query MUST declare
///   one of three policies: <c>[RequireRoles]</c>, <c>[RequireAuthentication]</c>,
///   or <c>[RequireSystemInternal]</c>. The <c>AuthorizationPolicyVerifier</c>
///   (startup scan) and the <c>AuthorizationMiddleware</c> (runtime check)
///   both treat all three as valid. A message with NONE of the three is
///   rejected — no message slips through unattributed.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireSystemInternalAttribute : Attribute
{
}
