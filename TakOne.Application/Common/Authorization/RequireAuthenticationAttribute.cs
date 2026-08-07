namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Marks a command/query as requiring an authenticated user, with NO
/// role restriction. Any authenticated user — regardless of role — may
/// dispatch the message. Unauthenticated (anonymous) callers are rejected
/// by <see cref="TakOne.Application.Common.Middlewares.AuthorizationMiddleware"/>.
///
/// This attribute is the "minimum bar" authorization policy: it asserts
/// that the message handler must NEVER run for an anonymous caller, but
/// does not restrict which authenticated role can call it.
///
/// WHEN TO USE <see cref="RequireAuthenticationAttribute"/> vs
///      <see cref="RequireRolesAttribute"/>:
///   - Use <c>[RequireAuthentication]</c> for customer-facing queries that
///     every role can call (e.g. browsing the shop, viewing one's own
///     cart). The handler may still apply per-user scoping (e.g. only
///     return the caller's own sales) — that's resource-level auth, not
///     role-level auth.
///   - Use <c>[RequireRoles(Roles.Admin, Roles.Manager)]</c> for staff-only
///     operations where non-staff roles must be rejected at the middleware
///     level, before the handler even runs.
///
/// FAIL-CLOSED POLICY (Issue #08):
///   <see cref="AuthorizationMiddleware"/> rejects ANY command/query that
///   has NEITHER <c>[RequireAuthentication]</c> NOR <c>[RequireRoles]</c>.
///   This means every message dispatched through Wolverine MUST explicitly
///   declare its authorization policy. A new command that forgets the
///   attribute is rejected at runtime with a clear error message, and the
///   <c>AuthorizationPolicyVerifier</c> (run at startup) fails the
///   application launch if any message type is missing both attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireAuthenticationAttribute : Attribute
{
}