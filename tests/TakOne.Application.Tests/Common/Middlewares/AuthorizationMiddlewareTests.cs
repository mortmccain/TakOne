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
///   <see cref="Envelope"/> and either returns normally (continue to the
///   handler) or THROWS <see cref="MessageAuthorizationException"/>
///   (deny — fail-closed). Throwing is the enforcement mechanism used by
///   Wolverine's own FluentValidation middleware (its failure action throws
///   ValidationException): a non-null return from Before is silently ignored
///   by Wolverine 6.x unless the exact result type is registered, so throwing
///   is the only reliable way to stop the handler. See the middleware class
///   remarks for the full rationale.
///
///   We mock <see cref="ICurrentUserService"/> with NSubstitute and pass
///   synthetic message types decorated (or not) with the three authorization
///   attributes. Each test exercises ONE branch of the fail-closed decision
///   tree.
///
/// BRANCHES TESTED:
///   • null message → returns normally (nothing to authorize)
///   • DOMAIN EVENT (name ends with "DomainEvent") → exempt, returns normally
///     (even with no attributes and an unauthenticated user)
///   • message with NO auth attributes → throws with UE|PolicyMissing code
///   • [RequireSystemInternal] alone → bypass (returns normally)
///   • [RequireSystemInternal] + [RequireAuthentication] together → bypass wins
///   • [RequireAuthentication] + !IsAuthenticated → throws "Authentication required."
///   • [RequireAuthentication] + IsAuthenticated → returns normally
///   • [RequireRoles(Admin)] + IsInRole(Admin)=true → returns normally
///   • [RequireRoles(Admin)] + IsInRole(Admin)=false → throws permission denied
///   • [RequireRoles(Admin,Manager)] + IsInRole(Manager)=true → returns normally
///   • [RequireRoles(Admin,Manager)] + none match → throws "Required role(s): Admin, Manager."
///   • [RequireRoles]+[RequireAuthentication] combo → role check runs
///   • unauthenticated user with [RequireRoles] → "Authentication required." (auth check runs BEFORE role check)
/// </summary>
public class AuthorizationMiddlewareTests
{
    // ── Synthetic message types (mirror the real Wolverine convention) ──

    // No attributes at all — should trip the fail-closed branch.
    private sealed class NoAttributeCommand;

    // A domain event — raised by aggregates, never user-dispatched, and by
    // convention never carries the auth attributes. Must be EXEMPT even when
    // the current user is anonymous (the fail-closed check must not break
    // notification fanout).
    private sealed record SaleSubmittedDomainEvent(Guid SaleId, string? SaleNumber);

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
    public void Before_WithNullMessage_DoesNotThrow()
    {
        // Arrange
        // An envelope with no message is a degenerate case — the middleware
        // lets the handler deal with it (auth doesn't apply).
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(null);

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    // ── Domain events are exempt ─────────────────────────────────────

    // Domain events are raised INSIDE already-authorized handlers by
    // aggregates. They carry no auth attributes and have no user context.
    // The fail-closed branch must NOT fire for them — otherwise every
    // notification fanout handler would be rejected and the notification
    // system would silently die.
    [Fact]
    public void Before_WithDomainEvent_IsExemptEvenForAnonymousCaller()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new SaleSubmittedDomainEvent(Guid.NewGuid(), "INT-1404-00000001"));

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    // ── Fail-closed ──────────────────────────────────────────────────

    // A message with NO authorization attribute must be REJECTED. This is
    // the fail-closed defense — a new command that forgets the attribute
    // never silently bypasses auth.
    [Fact]
    public void Before_WithMessageMissingAllAttributes_ThrowsWithPolicyMissingCode()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new NoAttributeCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        // Wire-format UE| prefix — recognized by ErrorDisplayService.Localize.
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage($"UE|{UnexpectedErrorCodes.AuthorizationMiddleware_PolicyMissing}");
    }

    // ── System-internal bypass ───────────────────────────────────────

    [Fact]
    public void Before_WithRequireSystemInternal_DoesNotThrow()
    {
        // Arrange
        // System-internal messages (e.g. EmitAppUpdateBroadcastCommand) are
        // dispatched by trusted in-process code with NO current user. The
        // middleware must bypass the user-auth check entirely.
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new SystemInternalCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
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
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    // ── RequireAuthentication ─────────────────────────────────────────

    [Fact]
    public void Before_WithRequireAuthenticationAndUnauthenticated_ThrowsAuthenticationRequired()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthenticatedCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage("Authentication required.");
    }

    [Fact]
    public void Before_WithRequireAuthenticationAndAuthenticated_DoesNotThrow()
    {
        // Arrange
        // Authenticated user with NO role restriction — passes through to
        // the handler. No role check runs.
        var currentUser = BuildCurrentUser(isAuthenticated: true);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthenticatedCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    // ── RequireRoles ──────────────────────────────────────────────────

    [Fact]
    public void Before_WithRequireRolesAndUserInRole_DoesNotThrow()
    {
        // Arrange
        // The user has the required Admin role — should pass.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Before_WithRequireRolesAndUserNotInRole_ThrowsPermissionDenied()
    {
        // Arrange
        // The user is authenticated but does not have Admin — should be
        // rejected with a message listing the required role(s).
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage(
                "You do not have permission to perform this action. Required role(s): Admin.");
    }

    [Fact]
    public void Before_WithRequireRolesAndUserInSecondRole_DoesNotThrow()
    {
        // Arrange
        // The user has Manager (not Admin) — for [RequireRoles(Admin, Manager)],
        // having AT LEAST ONE of the roles is sufficient.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Manager);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminOrManagerCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Before_WithRequireRolesAndUserInNoRoles_ThrowsPermissionDeniedWithCommaList()
    {
        // Arrange
        // The user has neither Admin nor Manager — should be rejected with
        // the comma-separated list of required roles.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AdminOrManagerCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage(
                "You do not have permission to perform this action. Required role(s): Admin, Manager.");
    }

    // ── Combo: [RequireAuthentication] + [RequireRoles] ─────────────

    [Fact]
    public void Before_WithAuthAndRolesAndUserInRole_DoesNotThrow()
    {
        // Arrange
        // Both attributes present. The user is authenticated AND in the
        // required Admin role. The role check is the second gate; it passes.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Admin);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().NotThrow();
    }

    // When [RequireAuthentication] + [RequireRoles] is present and the user
    // is UNAUTHENTICATED, the auth check fires FIRST (before the role check).
    // The error thrown is "Authentication required." — NOT the
    // permission-denied message. This ordering is defense-in-depth: don't
    // leak which roles exist to anonymous callers.
    [Fact]
    public void Before_WithAuthAndRolesAndUnauthenticated_ThrowsAuthenticationRequired()
    {
        // Arrange
        var currentUser = BuildCurrentUser(isAuthenticated: false);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage("Authentication required.");
    }

    [Fact]
    public void Before_WithAuthAndRolesAndAuthenticatedButNotInRole_ThrowsPermissionDenied()
    {
        // Arrange
        // Authenticated but missing the Admin role — the role check fires
        // (auth already passed) and rejects.
        var currentUser = BuildCurrentUser(isAuthenticated: true, Roles.Employee);
        var sut = new AuthorizationMiddleware(currentUser);
        var envelope = BuildEnvelope(new AuthAndRolesCommand());

        // Act
        var act = () => sut.Before(envelope);

        // Assert
        act.Should().Throw<MessageAuthorizationException>()
            .WithMessage(
                "You do not have permission to perform this action. Required role(s): Admin.");
    }
}
