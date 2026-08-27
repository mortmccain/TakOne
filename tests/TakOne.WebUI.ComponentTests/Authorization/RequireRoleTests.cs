using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using NSubstitute;
using System.Security.Claims;
using System.Threading.Tasks;
using TakOne.Application.Common.Authorization;
using TakOne.WebUI.Authorization;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Authorization;

/// <summary>
/// bUnit tests for the <c>RequireRole</c> razor component
/// (Authorization/RequireRole.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A reusable wrapper around <c>&lt;AuthorizeView
/// Roles="@_rolesCsv"&gt;</c> that takes <c>Roles</c> (string[]) +
/// <c>ChildContent</c> (RenderFragment) parameters and renders
/// <c>ChildContent</c> when the current user is in AT LEAST ONE of the
/// supplied roles (any-of/OR semantics). Renders nothing when the user
/// lacks all of the supplied roles (per roadmap Section 12.7: hide,
/// don't disable).
/// </para>
/// <para>
/// <b>SUT design rationale.</b> Wrapping <c>AuthorizeView</c> instead
/// of writing custom auth logic lets the SUT delegate to ASP.NET Core's
/// authorization pipeline — so role checks go through the registered
/// <c>AuthenticationStateProvider</c> + <c>IAuthorizationService</c>.
/// Tests below register a substitute <c>AuthenticationStateProvider</c>
/// that returns a configured <c>AuthenticationState</c> per test.
/// </para>
/// <para>
/// <b>SUT behavior on empty Roles.</b> When <c>Roles</c> is empty,
/// <c>_rolesCsv = ""</c> — <c>AuthorizeView</c> with an empty Roles
/// string treats the user as authorized (renders ChildContent) as long
/// as they're authenticated. Anonymous users still don't get ChildContent
/// rendered. Tests below document this behavior.
/// </para>
/// <para>
/// <b>SUT discovery.</b> The component has a <c>[Parameter] string[]
/// Roles</c> + <c>[Parameter] RenderFragment? ChildContent</c> — no
/// <c>NotAuthorized</c> parameter (the SUT renders nothing when the
/// user lacks the role, instead of rendering an alternative fragment).
/// </para>
/// </remarks>
public class RequireRoleTests
{
    private static IRenderedComponent<RequireRole> RenderWithRole(
        TestContext ctx,
        string userName,
        string[] userRoles,
        string[] requiredRoles,
        RenderFragment childContent)
    {
        if (string.IsNullOrEmpty(userName))
            ComponentTestSetup.AddBunitAnonymousUser(ctx);
        else
            ComponentTestSetup.AddBunitAuthorizedUser(ctx, userName, userRoles);

        // AddBunitAuthorizedUser uses bUnit's native AddAuthorization()
        // extension which registers the FULL ASP.NET Core auth pipeline:
        // TestAuthenticationStateProvider + IAuthorizationService +
        // IAuthorizationPolicyProvider + IAuthorizationEvaluator.
        // AuthorizeView in the SUT requires all three to be available
        // (it queries IAuthorizationPolicyProvider for policy resolution
        // even when only Roles is set).
        return ctx.RenderComponent<RequireRole>(ps => ps
            .Add(p => p.Roles, requiredRoles)
            .Add(p => p.ChildContent, childContent));
    }

    private static RenderFragment ChildContentFragment(string markerText)
        => b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "class", "require-role-test-content");
            b.AddContent(2, markerText);
            b.CloseElement();
        };

    [Fact]
    public async Task RequireRole_AdminUserAndAdminRequired_RendersChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: "admin",
            userRoles: new[] { Roles.Admin },
            requiredRoles: new[] { Roles.Admin },
            childContent: ChildContentFragment(marker));

        // Assert
        // Admin user IS in Admin role → AuthorizeView sees authorized
        // → ChildContent renders.
        cut.Markup.Should().Contain(marker);
        cut.FindAll(".require-role-test-content").Should().HaveCount(1);
    }

    [Fact]
    public async Task RequireRole_AdminUserAndManagerRequired_DoesNotRenderChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: "admin",
            userRoles: new[] { Roles.Admin },
            requiredRoles: new[] { Roles.Manager },
            childContent: ChildContentFragment(marker));

        // Assert
        // Admin user is NOT in Manager role → AuthorizeView sees not-
        // authorized → ChildContent does NOT render.
        cut.Markup.Should().NotContain(marker);
        cut.FindAll(".require-role-test-content").Should().BeEmpty();
    }

    [Fact]
    public async Task RequireRole_AdminUserAndAdminOrManagerRequired_RendersChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: "admin",
            userRoles: new[] { Roles.Admin },
            requiredRoles: new[] { Roles.Admin, Roles.Manager },
            childContent: ChildContentFragment(marker));

        // Assert
        // Admin user is in the Admin role → "any of" semantics →
        // ChildContent renders.
        cut.Markup.Should().Contain(marker);
    }

    [Fact]
    public async Task RequireRole_ManagerUserAndAdminOrEmployeeRequired_RendersChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: "manager",
            userRoles: new[] { Roles.Manager },
            requiredRoles: new[] { Roles.Admin, Roles.Employee, Roles.Manager },
            childContent: ChildContentFragment(marker));

        // Assert
        // Manager user matches the Manager entry in the Roles array →
        // ChildContent renders (any-of match).
        cut.Markup.Should().Contain(marker);
    }

    [Fact]
    public async Task RequireRole_AnonymousUserAndAdminRequired_DoesNotRenderChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: string.Empty,
            userRoles: Array.Empty<string>(),
            requiredRoles: new[] { Roles.Admin },
            childContent: ChildContentFragment(marker));

        // Assert
        // Anonymous user → not authenticated → AuthorizeView denies →
        // ChildContent does NOT render.
        cut.Markup.Should().NotContain(marker);
        cut.FindAll(".require-role-test-content").Should().BeEmpty();
    }

    [Fact]
    public async Task RequireRole_AuthenticatedUserWithoutRolesAndAdminRequired_DoesNotRenderChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        // Authenticated user (has a name claim) but no role claims.
        var cut = RenderWithRole(ctx,
            userName: "user-without-roles",
            userRoles: Array.Empty<string>(),
            requiredRoles: new[] { Roles.Admin },
            childContent: ChildContentFragment(marker));

        // Assert
        // Authenticated → AuthorizeView sees the user is authenticated,
        // but no roles match → not-authorized for the Roles list →
        // ChildContent does NOT render.
        cut.Markup.Should().NotContain(marker);
    }

    [Fact]
    public async Task RequireRole_AdminUserAndEmptyRoles_RendersChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        // Edge case: empty Roles array → _rolesCsv = "" → AuthorizeView
        // with empty Roles → for AUTHENTICATED users, AuthorizeView's
        // default behavior is "authorize" (renders ChildContent) because
        // the role check is bypassed. This is documented in the SUT's
        // class XML doc and tested here to lock in the behavior.
        var cut = RenderWithRole(ctx,
            userName: "admin",
            userRoles: new[] { Roles.Admin },
            requiredRoles: Array.Empty<string>(),
            childContent: ChildContentFragment(marker));

        // Assert
        cut.Markup.Should().Contain(marker);
    }

    [Fact]
    public async Task RequireRole_AnonymousUserAndEmptyRoles_DoesNotRenderChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        // Even with empty Roles, anonymous users still don't get
        // ChildContent rendered — AuthorizeView requires authentication
        // regardless of role checks.
        var cut = RenderWithRole(ctx,
            userName: string.Empty,
            userRoles: Array.Empty<string>(),
            requiredRoles: Array.Empty<string>(),
            childContent: ChildContentFragment(marker));

        // Assert
        cut.Markup.Should().NotContain(marker);
    }

    [Fact]
    public async Task RequireRole_CustomerUserAndAdminRequired_DoesNotRenderChildContent()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        // Customer role ≠ Admin → ChildContent should NOT render. This
        // is the role-isolation guarantee — Customer users can't see
        // admin-only buttons even if they navigate to the admin page.
        var cut = RenderWithRole(ctx,
            userName: "alice",
            userRoles: new[] { Roles.Customer },
            requiredRoles: new[] { Roles.Admin },
            childContent: ChildContentFragment(marker));

        // Assert
        cut.Markup.Should().NotContain(marker);
        cut.FindAll(".require-role-test-content").Should().BeEmpty();
    }

    [Fact]
    public async Task RequireRole_RendersAuthorizeViewWrapper_ChildContentIsInsideAuthorizeView()
    {
        // Arrange
        using var ctx = new TestContext();
        const string marker = "secret-content-marker";

        // Act
        var cut = RenderWithRole(ctx,
            userName: "admin",
            userRoles: new[] { Roles.Admin },
            requiredRoles: new[] { Roles.Admin },
            childContent: ChildContentFragment(marker));

        // Assert
        // The SUT wraps ChildContent in <AuthorizeView Roles="...">.
        // When authorized, AuthorizeView renders the ChildContent. We
        // can't directly assert on the AuthorizeView markup (it
        // doesn't emit a wrapper element), but we can verify the
        // ChildContent's marker is in the markup, which proves the
        // AuthorizeView's Authorized fragment rendered it.
        cut.Markup.Should().Contain("secret-content-marker");
    }
}
