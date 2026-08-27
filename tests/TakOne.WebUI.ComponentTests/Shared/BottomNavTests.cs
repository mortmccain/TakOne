using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using System.Security.Claims;
using System.Threading.Tasks;
using TakOne.Application.Common.Authorization;
using TakOne.WebUI.Components.Shared;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Shared;

/// <summary>
/// bUnit tests for <see cref="BottomNav"/> razor component
/// (Components/Shared/BottomNav.razor) — the role-aware mobile bottom
/// navigation bar.
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> The bar renders a different set of nav items based
/// on (1) the user's role (Customer vs Admin/Manager/Employee) and (2)
/// the current URL path (which "section" the user is in, e.g.
/// "products" vs "admin-products"). Customers always see 4 items;
/// staff see 5 items (Dashboard / Products / Cart / Orders / Settings
/// pattern, varying by section).
/// </para>
/// <para>
/// <b>SUT dependencies.</b> BottomNav injects <c>NavigationManager</c>,
/// <c>AuthenticationStateProvider</c>, and <c>IStringLocalizer&lt;BottomNav&gt;</c>.
/// NavigationManager is provided by bUnit. AuthenticationStateProvider
/// is registered as a substitute configured per test with the user's
/// role. The localizer is registered as an "identity" stub —
/// <c>Loc["Products"]</c> returns the resource key "Products" as the
/// localized value, so the rendered markup has predictable labels.
/// </para>
/// <para>
/// <b>SUT discovery (URL routing).</b> The component inspects
/// <c>Navigation.Uri</c> to compute the "section". bUnit's
/// <c>NavigationManager</c> defaults to <c>http://localhost/</c>. To
/// test the section logic, we use <c>ctx.Services.GetRequiredService&lt;NavigationManager&gt;().NavigateTo("/m/Products")</c>
/// BEFORE rendering. (This works because bUnit's TestNavigationManager
/// is registered as the singleton + is initialized when the test
/// context is created.)
/// </para>
/// <para>
/// <b>SUT discovery (CartCount badge).</b> The Cart item supports a
/// badge via the <c>CartCount</c> parameter (int). When CartCount &gt; 0,
/// the Cart item renders an additional <c>&lt;span class="nav-badge"&gt;</c>
/// with the count formatted by CultureFormat.FormatNumber. Tests below
/// verify the badge is rendered when CartCount&gt;0 and absent when 0.
/// </para>
/// </remarks>
public class BottomNavTests
{
    [Fact]
    public async Task BottomNav_CustomerRoleOnProductsPage_RendersExactlyFourNavItems()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        // Navigate to /m/Products so the section logic resolves to "products".
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        // OnInitializedAsync calls AuthStateProvider.GetAuthenticationStateAsync()
        // to read the user's roles. We await it via RenderComponent's sync
        // context (bUnit marshals the async work through InvokeAsync).
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // Customer sees: Products | MyOrders | Cart | Settings — 4 items.
        cut.FindAll("a.nav-item").Should().HaveCount(4);
    }

    [Fact]
    public async Task BottomNav_CustomerRole_NavItemsHaveExpectedHrefs()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // Each customer nav item href points to a mobile route. The localizer
        // stub returns the resource key as the label — we verify both the
        // href + the label structure (label text matches the resource key).
        var navItems = cut.FindAll("a.nav-item");
        navItems.Should().HaveCount(4);
        navItems[0].GetAttribute("href").Should().Be("/m/Products");
        navItems[1].GetAttribute("href").Should().Be("/m/Sales");
        navItems[2].GetAttribute("href").Should().Be("/m/Cart");
        navItems[3].GetAttribute("href").Should().Be("/m/Settings");
    }

    [Fact]
    public async Task BottomNav_CustomerOnProductsPage_ProductsItemMarkedActive()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // The first item (Products) should have the "active" CSS class
        // because the current URL path is /m/Products → section="products".
        var productsItem = cut.FindAll("a.nav-item")[0];
        productsItem.GetAttribute("class").Should().Contain("active");

        // The other items (MyOrders, Cart, Settings) should NOT be active.
        var cartItem = cut.FindAll("a.nav-item")[2];
        cartItem.GetAttribute("class").Should().NotContain("active");
    }

    [Fact]
    public async Task BottomNav_CustomerOnCartPage_CartItemMarkedActive()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Cart");

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // Now the Cart item (index 2) should be active — section="cart"
        // maps to the Cart nav item.
        var cartItem = cut.FindAll("a.nav-item")[2];
        cartItem.GetAttribute("class").Should().Contain("active");

        var productsItem = cut.FindAll("a.nav-item")[0];
        productsItem.GetAttribute("class").Should().NotContain("active");
    }

    [Fact]
    public async Task BottomNav_CustomerWithNonZeroCartCount_RendersBadgeOnCartItem()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        // Render with CartCount=5 — the Cart item should display a badge
        // span showing the count.
        var cut = ctx.RenderComponent<BottomNav>(ps => ps
            .Add(p => p.CartCount, 5));

        // Assert
        // The Cart nav item (href=/m/Cart) is the only item with ShowBadge=true.
        // When CartCount > 0, the markup includes a <span class="nav-badge">
        // inside the cart item's icon wrapper.
        var cartItem = cut.FindAll("a.nav-item").Single(a => a.GetAttribute("href") == "/m/Cart");
        cartItem.QuerySelector(".nav-badge").Should().NotBeNull();
        cartItem.QuerySelector(".nav-badge")!.TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BottomNav_CustomerWithZeroCartCount_DoesNotRenderBadge()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        // CartCount=0 (the default) — no badge should be rendered.
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        cut.FindAll(".nav-badge").Should().BeEmpty();
    }

    [Fact]
    public async Task BottomNav_AdminRoleOnDashboard_RendersFiveItemsWithDashboardActive()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        // Default URL is "/" → NormalizeSection returns "dashboard".
        // (No NavigateTo call needed — bUnit's default URL is "http://localhost/".)

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // Staff on Dashboard: Dashboard(active) | Products | AdminProducts |
        // AdminUsers | Settings — 5 items, the Dashboard one active.
        var navItems = cut.FindAll("a.nav-item");
        navItems.Should().HaveCount(5);
        navItems[0].GetAttribute("href").Should().Be("/m/Dashboard");
        navItems[0].GetAttribute("class").Should().Contain("active");
        navItems[1].GetAttribute("href").Should().Be("/m/Products");
        navItems[1].GetAttribute("class").Should().NotContain("active");
    }

    [Fact]
    public async Task BottomNav_AdminRoleOnAdminProductsPage_RendersFiveItemsWithAdminProductsActive()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Admin/Products");

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // On AdminProducts section: Dashboard | AdminProducts(active) |
        // AdminCategories | Settings — 4 items (the staff bar shrinks when
        // in admin-products because the cart item is dropped).
        var navItems = cut.FindAll("a.nav-item");
        navItems.Should().HaveCount(4);
        navItems[1].GetAttribute("href").Should().Be("/m/Admin/Products");
        navItems[1].GetAttribute("class").Should().Contain("active");
        navItems[0].GetAttribute("class").Should().NotContain("active");
    }

    [Fact]
    public async Task BottomNav_AnonymousUser_FallsBackToStaffBar()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAnonymousUser(ctx);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        // Default URL → section "dashboard".

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // Anonymous user: _isAdmin, _isManager, _isEmployee, _isCustomer are
        // all false → falls through to the staff branch (else). Section
        // "dashboard" → 5-item staff bar with Dashboard active.
        var navItems = cut.FindAll("a.nav-item");
        navItems.Should().HaveCount(5);
        navItems[0].GetAttribute("href").Should().Be("/m/Dashboard");
    }

    [Fact]
    public async Task BottomNav_RendersNavElementWithBottomNavClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddAuthenticatedUser(ctx, "alice", Roles.Customer);
        ComponentTestSetup.AddIdentityLocalizer<BottomNav>(ctx);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/m/Products");

        // Act
        var cut = ctx.RenderComponent<BottomNav>();

        // Assert
        // The component renders a single <nav class="bottom-nav"> root.
        var nav = cut.Find("nav.bottom-nav");
        nav.Should().NotBeNull();
        // Each item is an <a class="nav-item"> — verify the structure
        // (anchor-based nav, not button-based, to allow right-click open
        // in new tab).
        nav.QuerySelectorAll("a.nav-item").Length.Should().Be(4);
    }
}
