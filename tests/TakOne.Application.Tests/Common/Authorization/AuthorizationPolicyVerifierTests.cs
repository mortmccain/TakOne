using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Notifications.Commands.SendBroadcastNotification;
using Xunit;

namespace TakOne.Application.Tests.Common.Authorization;

/// <summary>
/// Unit tests for <see cref="AuthorizationPolicyVerifier"/>.
///
/// COVERAGE APPROACH:
///   <see cref="AuthorizationPolicyVerifier.Verify"/> scans an assembly's
///   non-abstract, non-nested classes whose name ends with "Command" or
///   "Query" and throws <see cref="InvalidOperationException"/> if any
///   such type is missing ALL three authorization attributes
///   ([RequireRoles], [RequireAuthentication], [RequireSystemInternal]).
///
///   The test surface needs THREE distinct kinds of input assembly:
///     1. An assembly with ZERO matching types — Verify should NOT throw.
///        (typeof(object).Assembly has no Command/Query types.)
///     2. An assembly where every matching type IS decorated — Verify
///        should NOT throw. (TakOne.Application — every Command/Query in
///        production code carries an attribute; if a future commit breaks
///        this invariant, the test surfaces the regression.)
///     3. An assembly with at least one matching type missing all
///        attributes — Verify should throw and list ALL offenders
///        alphabetically by FullName. (A dynamically-emitted assembly via
///        System.Reflection.Emit.AssemblyBuilder — gives full control.)
///     4. null — ArgumentNullException.
/// </summary>
public class AuthorizationPolicyVerifierTests
{
    // ── Null argument ────────────────────────────────────────────────

    [Fact]
    public void Verify_WithNullAssembly_ThrowsArgumentNullException()
    {
        // Arrange
        // The verifier's first line is `ArgumentNullException.ThrowIfNull(assembly)`.

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Assembly with no matching types ──────────────────────────────

    // typeof(object).Assembly is System.Private.CoreLib — the runtime's
    // core library. It has thousands of types, but none end in "Command"
    // or "Query" (those are TakOne conventions, not BCL conventions).
    // Verify should complete silently.
    [Fact]
    public void Verify_WithCoreLibAssembly_DoesNotThrow()
    {
        // Arrange
        var assembly = typeof(object).Assembly;

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(assembly);

        // Assert
        act.Should().NotThrow();
    }

    // ── Assembly where every matching type IS decorated ─────────────

    // TakOne.Application is the production Application layer. Every
    // Command/Query dispatched through Wolverine is decorated with one of
    // the three authorization attributes — the convention is enforced at
    // PR-review time. This test is a defense-in-depth assertion: if a
    // future commit adds a Command without an attribute, this test will
    // fail at the next test run, alerting the developer before the app
    // launches (the production start-up scan would also fail — but the
    // test catches it earlier, at CI build time).
    [Fact]
    public void Verify_WithTakOneApplicationAssembly_DoesNotThrow()
    {
        // Arrange
        // Pick a representative type from TakOne.Application — any type
        // will do; we just want typeof(...).Assembly to resolve to
        // TakOne.Application.dll.
        var assembly = typeof(SendBroadcastNotificationCommand).Assembly;

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(assembly);

        // Assert
        act.Should().NotThrow();
    }

    // ── Assembly with a missing-attributes type ───────────────────────

    // Dynamically emit an in-memory assembly with one class named
    // "BadCommand" that has NO authorization attributes. The verifier
    // should throw InvalidOperationException listing that type's FullName.
    [Fact]
    public void Verify_WithSingleMissingAttributeCommand_ThrowsInvalidOperationExceptionListingType()
    {
        // Arrange
        // Build a dynamic assembly: "BadCommandAssembly.dll" with one
        // public non-abstract non-nested class named "BadCommand".
        // No attributes are applied — this is the fail-closed scenario.
        var assembly = EmitDynamicAssembly("BadCommandAssembly", "BadCommand");

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(assembly);

        // Assert
        // The exception message must contain the offending type's FullName
        // so the developer can find it.
        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("BadCommand");
        // The exception message should also mention Issue #08 so the
        // developer can find the fail-closed rationale.
        ex.Message.Should().Contain("fail-closed");
    }

    // Multiple missing-attribute types must be listed in ALPHABETICAL
    // ORDER by FullName. This is a stable output invariant — when a
    // developer reads the error message, the type list is the same every
    // time, regardless of reflection ordering quirks.
    [Fact]
    public void Verify_WithMultipleMissingCommands_ListsAllAlphabeticallyByFullName()
    {
        // Arrange
        // Emit an assembly with three classes:
        //   - "ZetaCommand"  (last alphabetically)
        //   - "AlphaCommand" (first alphabetically)
        //   - "MuCommand"    (middle alphabetically)
        // All three lack attributes. The exception message must list them
        // in the order: AlphaCommand, MuCommand, ZetaCommand.
        var assembly = EmitDynamicAssembly(
            "MultiBadCommandAssembly",
            "ZetaCommand",
            "AlphaCommand",
            "MuCommand");

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(assembly);

        // Assert
        var ex = act.Should().Throw<InvalidOperationException>().Which;
        // Verify alphabetical ordering: AlphaCommand should appear
        // before MuCommand which should appear before ZetaCommand in
        // the exception message.
        var msg = ex.Message;
        var alphaIdx = msg.IndexOf("AlphaCommand", StringComparison.Ordinal);
        var muIdx = msg.IndexOf("MuCommand", StringComparison.Ordinal);
        var zetaIdx = msg.IndexOf("ZetaCommand", StringComparison.Ordinal);

        alphaIdx.Should().BeGreaterThan(-1, "AlphaCommand must be in the list");
        muIdx.Should().BeGreaterThan(-1, "MuCommand must be in the list");
        zetaIdx.Should().BeGreaterThan(-1, "ZetaCommand must be in the list");
        alphaIdx.Should().BeLessThan(muIdx, "AlphaCommand must come before MuCommand");
        muIdx.Should().BeLessThan(zetaIdx, "MuCommand must come before ZetaCommand");
    }

    // The scanner should NOT pick up types whose name does NOT end with
    // "Command" or "Query" — even if they have no attributes. A
    // dynamically-emitted "Dog" class (no attributes, but no
    // Command/Query suffix) must NOT trigger the verifier.
    [Fact]
    public void Verify_WithNonCommandNamedType_DoesNotThrow()
    {
        // Arrange
        // Emit an assembly with one class named "Dog" — no attributes.
        // The scanner's naming heuristic skips it (name doesn't end with
        // "Command" or "Query"), so Verify completes silently.
        var assembly = EmitDynamicAssembly("DogAssembly", "Dog");

        // Act
        var act = () => AuthorizationPolicyVerifier.Verify(assembly);

        // Assert
        act.Should().NotThrow();
    }

    // ── Dynamic-assembly emitter helper ─────────────────────────────

    /// <summary>
    /// Builds a runnable in-memory assembly with one or more public
    /// non-abstract non-nested classes named per <paramref name="typeNames"/>.
    /// NO authorization attributes are applied — the classes are bare.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="AssemblyBuilder"/> + <see cref="ModuleBuilder"/> +
    /// <see cref="TypeBuilder"/> to construct the assembly on the fly.
    /// The assembly is saved to an in-memory stream (no disk file).
    /// </remarks>
    private static Assembly EmitDynamicAssembly(string assemblyName, params string[] typeNames)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName),
            AssemblyBuilderAccess.Run);

        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName + ".dll");

        foreach (var typeName in typeNames)
        {
            // Public, non-abstract, non-nested class.
            var typeBuilder = moduleBuilder.DefineType(
                typeName,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
            _ = typeBuilder.CreateType();
        }

        return assemblyBuilder;
    }
}
