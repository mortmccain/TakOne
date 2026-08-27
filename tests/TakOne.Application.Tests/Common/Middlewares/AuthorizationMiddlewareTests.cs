using FluentAssertions;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Middlewares;
using Wolverine;
using Xunit;

namespace TakOne.Application.Tests.Common.Middlewares;

/// <summary>
/// Unit tests for <see cref="AuthorizationMiddleware"/>.
///
/// COVERAGE APPROACH:
///   The middleware is a Wolverine "Before" handler that takes an
///   <see cref="Envelope"/> and returns either null (continue) or a
///   <see cref="TakOne.SharedKernel.Common.Result"/> failure (short-circuit).
///   We mock <see cref="ICurrentUserService"/> with NSubstitute and pass
///   synthetic message types decorated (or not) with the three authorization
///   attributes. Each test exercises ONE branch of the fail-closed decision
///   tree.
///
/// BRANCHES TESTED:
///   • null message → null (nothing to authorize)
///   • message with NO auth attributes → fail-closed with UE|PolicyMissing
///   • [RequireSystemInternal] alone → bypass (null)
///   • [RequireSystemInternal] + [RequireAuthentication] together → bypass wins
///   • [RequireAuthentication] + !IsAuthenticated → "Authentication required."
///   • [RequireAuthentication] + IsAuthenticated → null
///   • [RequireRoles(Admin)] + IsInRole(Admin)=true → null
///   • [RequireRoles(Admin)] + IsInRole(Admin)=false → permission denied
///   • [RequireRoles(Admin,Manager)] + IsInRole(Manager)=true → null
///   • [RequireRoles(Admin,Manager)] + none match → "Required role(s): Admin, Manager."
///   • [RequireRoles]+[RequireAuthentication] combo → role check runs
///   • unauthenticated user with [RequireRoles] → "Authentication required." (auth check runs BEFORE role check)
/// </summary>
public class AuthorizationMiddlewareTests
{
    // ── Synthetic message types (mirror the real Wolverine convention) ──

    // No attributes at all — should trip the fail-closed branch.
    private sealed class NoAttributeCommand;

    // System-internal: bypasses auth entirely.
    [RequireSystemInternal]
    private sealed class SystemInternalCommand;

    // RequireAuthentication: any authenticated user.
    [RequireAuthentication]
    private sealed class AuthenticatedCommand;

    // Single role required.
    [RequireRoles(Roles.Admin)]
    private sealed class AdminCommand;

    // Multiple roles required.
    [RequireRoles(Roles.Admin, Roles.Manager)]
    private sealed class AdminOrManagerCommand;

    // Both RequireAuthentication + RequireRoles — the role check wins
    // (the auth check has already passed; the role check is the second gate).
    [RequireAuthentication]
    [RequireRoles(Roles.Admin)]
    private sealed class AuthAndRolesCommand;

    // System-internal wins over RequireAuthentication (auth check skipped).
    [RequireSystemInternal]
    [RequireAuthentication]
    private sealed class SystemInternalAndAuthCommand;

    // ── Helpers ───────────────────────────────────────────────────────

    // Wraps a message in a Wolverine Envelope. The middleware reads
    // envelope.Message, so we set it directly.
    private static Envelope BuildEnvelope(object? message)
        => message is null ? new Envelope() : new Envelope(message);

    // Builds an ICurrentUserService mock configured for the supplied role.
    private static ICurrentUserService BuildCurrentUser(
        bool isAuthenticated,
        params string[] roles)
    {
        var user = Substitute.For<ICurrentUserService>();
        user.IsAuthenticated.Returns(isAuthenticated);
        // IsInRole returns true if the role is in the supplied list.
        user.IsInRole(Arg.Any<string>())
            .Returns(ci => roles.Contains((string)ci[0]!));
        return user;
    }

    // ── Null message ─────────────────────────────────────────────────

    [Fact]
    public void Before_WithNullMessage_ReturnsNull()
    {
        // Arrange
        // An envelope with no message is a degenerate case — the middleware
        // lets the handler deal with it (auth doesn't apply).
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(null);

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    // ── Fail-closed ──────────────────────────────────────────────────

    // A message with NO authorization attribute must be REJECTED. This is
    // the fail-closed defense — a new command that forgets the attribute
    // never silently bypasses auth.
    [Fact]
    public void Before_WithMessageMissingAllAttributes_ReturnsFailureWithPolicyMissingCode()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new NoAttributeCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<TakOne.SharedKernel.Common.Result>()
            .Which.Error.Should().Be($"UE|{UnexpectedErrorCodes.AuthorizationMiddleware_PolicyMissing}");
    }

    [Fact]
    public void Before_WithMessageMissingAllAttributes_ReturnsFailureResult()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new NoAttributeCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        // Result is a failure with the wire-format UE| prefix.
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
    }

    // ── System-internal bypass ───────────────────────────────────────

    [Fact]
    public void Before_WithRequireSystemInternal_ReturnsNull()
    {
        // Arrange
        // System-internal messages (e.g. EmitAppUpdateBroadcastCommand) are
        // dispatched by trusted in-process code with NO current user. The
        // middleware must bypass the user-auth check entirely.
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new SystemInternalCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    // When BOTH [RequireSystemInternal] AND [RequireAuthentication] are
    // present, the system-internal bypass wins. This protects the
    // AppUpdateBroadcasterHostedService path: that handler's command is
    // decorated with [RequireSystemInternal] (the trusted-system policy);
    // the additional [RequireAuthentication] (left from a copy-paste) must
    // not cause a runtime auth failure for the host's anonymous identity.
    [Fact]
    public void Before_WithSystemInternalAndAuthentication_BypassWinsOverAuth()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new SystemInternalAndAuthCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    // ── RequireAuthentication ─────────────────────────────────────────

    [Fact]
    public void Before_WithRequireAuthenticationAndUnauthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthenticatedCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
        typed.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public void Before_WithRequireAuthenticationAndAuthenticated_ReturnsNull()
    {
        // Arrange
        // Authenticated user with NO role restriction — passes through to
        // the handler. No role check runs.
        var currentUser = BuildCurrentUser(isAuthenticated: true);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthenticatedCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    // ── RequireRoles ──────────────────────────────────────────────────

    [Fact]
    public void Before_WithRequireRolesAndUserInRole_ReturnsNull()
    {
        // Arrange
        // The user has the required Admin role — should pass.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Before_WithRequireRolesAndUserNotInRole_ReturnsPermissionDenied()
    {
        // Arrange
        // The user is authenticated but does not have Admin — should be
        // rejected with a message listing the required role(s).
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
        typed.Error.Should().Be(
            "You do not have permission to perform this action. Required role(s): Admin.");
    }

    [Fact]
    public void Before_WithRequireRolesAndUserInSecondRole_ReturnsNull()
    {
        // Arrange
        // The user has Manager (not Admin) — for [RequireRoles(Admin, Manager)],
        // having AT LEAST ONE of the roles is sufficient.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Manager);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminOrManagerCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Before_WithRequireRolesAndUserInNoRoles_ReturnsPermissionDeniedWithCommaList()
    {
        // Arrange
        // The user has neither Admin nor Manager — should be rejected with
        // the comma-separated list of required roles.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminOrManagerCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
        typed.Error.Should().Be(
            "You do not have permission to perform this action. Required role(s): Admin, Manager.");
    }

    // ── Combo: [RequireAuthentication] + [RequireRoles] ─────────────

    [Fact]
    public void Before_WithAuthAndRolesAndUserInRole_ReturnsNull()
    {
        // Arrange
        // Both attributes present. The user is authenticated AND in the
        // required Admin role. The role check is the second gate; it passes.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        result.Should().BeNull();
    }

    // When [RequireAuthentication] + [RequireRoles] is present and the user
    // is UNAUTHENTICATED, the auth check fires FIRST (before the role check).
    // The error returned is "Authentication required." — NOT the
    // permission-denied message. This ordering is defense-in-depth: don't
    // leak which roles exist to anonymous callers.
    [Fact]
    public void Before_WithAuthAndRolesAndUnauthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
        typed.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public void Before_WithAuthAndRolesAndAuthenticatedButNotInRole_ReturnsPermissionDenied()
    {
        // Arrange
        // Authenticated but missing the Admin role — the role check fires
        // (auth already passed) and rejects.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var result = sut.Before(envelope);

        // Assert
        var typed = result as TakOne.SharedKernel.Common.Result;
        typed.Should().NotBeNull();
        typed!.IsSuccess.Should().BeFalse();
        typed.Error.Should().Be(
            "You do not have permission to perform this action. Required role(s): Admin.");
    }
}
