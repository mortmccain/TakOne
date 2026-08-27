using System.Reflection;

namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Startup-time verifier that scans the Application assembly for all
/// command/query message types and asserts each one has an explicit
/// authorization policy (<see cref="RequireRolesAttribute"/>,
/// <see cref="RequireAuthenticationAttribute"/>, or
/// <see cref="RequireSystemInternalAttribute"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>PURPOSE (Issue #08):</b>
/// The <see cref="Middlewares.AuthorizationMiddleware"/> is fail-CLOSED
/// at runtime — it rejects any message that lacks all three attributes.
/// This verifier is the COMPILE-TIME/STARTUP-TIME backstop: it catches
/// missing attributes BEFORE the app starts accepting requests, so a
/// developer who adds a new command and forgets the attribute sees an
/// immediate startup failure with a clear error message listing the
/// offending type(s).
/// </para>
/// <para>
/// <b>WHY A STARTUP SCAN (not just a unit test):</b>
/// The unit test (in TakOne.Application.Tests) catches the issue in CI.
/// But not every developer runs tests before <c>dotnet run</c>. The
/// startup scan ensures the app REFUSES TO LAUNCH if any command is
/// missing its authorization policy — a fail-closed posture at the
/// application level, not just the test pipeline.
/// </para>
/// <para>
/// <b>MESSAGE TYPE DISCOVERY HEURISTIC:</b>
/// The project convention is that every Wolverine message type ends with
/// "Command" or "Query". This scanner uses that convention to find
/// message types. If a future message type uses a different suffix
/// (e.g. "Event", "Notification"), the scanner should be updated to
/// include it — OR, better, the message type should be decorated with
/// a marker interface like <c>IMessage</c> and the scanner should look
/// for that interface instead.
/// </para>
/// </remarks>
public static class AuthorizationPolicyVerifier
{
    /// <summary>
    /// Scans the given assembly for all command/query types and throws
    /// <see cref="InvalidOperationException"/> if any type is missing
    /// all three of <see cref="RequireRolesAttribute"/>,
    /// <see cref="RequireAuthenticationAttribute"/>, and
    /// <see cref="RequireSystemInternalAttribute"/>.
    /// </summary>
    /// <param name="assembly">
    /// The assembly to scan. Typically
    /// <c>typeof(ServiceCollectionExtensions).Assembly</c> (the
    /// TakOne.Application assembly).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if any command/query type in the assembly is missing all
    /// three authorization attributes. The exception message lists ALL
    /// offending types.
    /// </exception>
    public static void Verify(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var messageTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsNested)
            .Where(t => t.Name.EndsWith("Command", StringComparison.Ordinal)
                     || t.Name.EndsWith("Query", StringComparison.Ordinal));

        var missing = messageTypes
            .Where(t => Attribute.GetCustomAttribute(t, typeof(RequireRolesAttribute)) is null
                     && Attribute.GetCustomAttribute(t, typeof(RequireAuthenticationAttribute)) is null
                     && Attribute.GetCustomAttribute(t, typeof(RequireSystemInternalAttribute)) is null)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(name => name)
            .ToList();

        if (missing.Count > 0)
        {
            var list = string.Join("\n  - ", missing);
            throw new InvalidOperationException(
                "Authorization policy verification FAILED (Issue #08 — fail-closed).\n" +
                "The following command/query types are missing an explicit " +
                "authorization policy ([RequireRoles], [RequireAuthentication], " +
                "or [RequireSystemInternal]):\n" +
                $"  - {list}\n\n" +
                "Every command/query dispatched through Wolverine MUST declare its " +
                "authorization policy. This is a fail-closed security requirement — " +
                "the AuthorizationMiddleware will reject any message without an " +
                "attribute at runtime. Fix by adding:\n" +
                "  • [RequireRoles(...)] for role-restricted messages\n" +
                "  • [RequireAuthentication] for messages any authenticated user can call\n" +
                "  • [RequireSystemInternal] for trusted in-process system messages " +
                "(hosted services, domain-event side effects) that have no user context.");
        }
    }
}