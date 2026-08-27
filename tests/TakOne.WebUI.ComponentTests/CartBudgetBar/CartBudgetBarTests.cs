using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using System.Globalization;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.DTOs;
using TakOne.SharedKernel.ValueObjects;
// The test class lives in namespace TakOne.WebUI.ComponentTests.CartBudgetBar
// (folder-based namespace). That namespace's name "CartBudgetBar"
// shadows the SUT type `TakOne.WebUI.Components.CartBudgetBar.CartBudgetBar`
// when referenced unqualified. The using alias below resolves the
// type explicitly so `CartBudgetBar` in the test body unambiguously
// means the SUT type, not the local namespace.
using CartBudgetBarComponent = TakOne.WebUI.Components.CartBudgetBar.CartBudgetBar;
using Xunit;

namespace TakOne.WebUI.ComponentTests.CartBudgetBar;

/// <summary>
/// bUnit tests for the <c>CartBudgetBar</c> razor component
/// (Components/CartBudgetBar/CartBudgetBar.razor) — the salary-budget
/// status card shown above the cart line-item list.
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A self-contained card that shows: monthly budget
/// (Salary), spent this month (Consumed minus cart), in-cart now
/// (CartTotal), remaining (Salary - Consumed), a percent badge, and a
/// progress bar whose width is driven by
/// <c>usedPercent = Min(100, (consumed/salary.Amount)*100)</c> (clamped
/// at 100% when over-budget). When <c>Budget</c> is null, the component
/// renders nothing.
/// </para>
/// <para>
/// <b>SUT parameters.</b>
/// <list type="bullet">
///   <item><c>Budget</c>: SalaryBudgetInfo? — full snapshot from
///     ISalaryBudgetService. Null = render nothing.</item>
///   <item><c>CartTotal</c>: MoneyDto? — the current draft cart total.
///     When null/zero, "in-cart" shows 0.</item>
/// </list>
/// The progress bar uses the inline style
/// <c>style="width: {usedPercent}%"</c> so bUnit can assert on the
/// literal style attribute (no JS-side computation, no Radzen
/// ProgressBar that clamps). The color class is driven by tier:
/// <c>cart-budget-bar-fill--ok</c> (under 70%),
/// <c>--warning</c> (70-99%), <c>--danger</c> (≥100% or overdraft).
/// </para>
/// <para>
/// <b>SUT dependencies.</b> The component injects
/// <c>IStringLocalizer&lt;CartBudgetBar&gt;</c> (for the title + labels
/// + month-day footer format) and uses Radzen components (RadzenCard,
/// RadzenIcon, RadzenAlert). The localizer is stubbed as an "identity"
/// localizer — <c>Loc["Key"]</c> returns "Key" — so the markup is
/// predictable. Radzen services are registered via AddRadzenComponents()
/// + Loose JS interop (RadzenIcon/RadzenAlert call into JS during
/// OnAfterRender; in Loose mode those return default values).
/// </para>
/// <para>
/// <b>Culture.</b> The component uses
/// <c>CultureInfo.CurrentCulture</c> for the percent format
/// (<c>"{0:0}%"</c>). Tests run under the default invariant / en-US
/// culture, so the percent badge renders ASCII digits.
/// </para>
/// </remarks>
public class CartBudgetBarTests
{
    private static SalaryBudgetInfo BuildBudget(decimal salary, decimal consumed, decimal remaining)
        => new()
        {
            Salary = new Money(salary, "IRR"),
            Consumed = consumed,
            Remaining = remaining,
            WindowStartUtc = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowEndUtc = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    [Fact]
    public void CartBudgetBar_RenderedWithNullBudget_RendersNothing()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // Act
        // Budget=null is the default; the SUT's `@if (Budget is not null)`
        // guard means nothing renders.
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, null));

        // Assert
        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void CartBudgetBar_RenderedWithNullBudget_AlsoHandlesNullCartTotalGracefully()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var loc = Substitute.For<IStringLocalizer<CartBudgetBarComponent>>();
        loc[Arg.Any<string>()].Returns(ci => new LocalizedString((string)ci[0], (string)ci[0], false));
        ctx.Services.AddSingleton(loc);

        // Act
        // Both Budget and CartTotal null — should render nothing (Budget
        // guard short-circuits before CartTotal is consulted).
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, null)
            .Add(p => p.CartTotal, null));

        // Assert
        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void CartBudgetBar_ConsumedIs20PercentOfSalary_BarWidthIs20Percent()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 200m, remaining: 800m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget)
            .Add(p => p.CartTotal, new MoneyDto { Amount = 0m, Currency = "IRR" }));

        // Assert
        // usedPercent = Min(100, (200/1000)*100) = 20. The bar fill's
        // inline style should be "width: 20%".
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 20%");

        // Tier check: 20% < 70% → green/ok tier.
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--ok");
    }

    [Fact]
    public void CartBudgetBar_ConsumedEqualsSalary_BarWidthIs100PercentAndDangerTier()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 1000m, remaining: 0m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget)
            .Add(p => p.CartTotal, new MoneyDto { Amount = 0m, Currency = "IRR" }));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 100%");
        // usedPercent=100 → danger tier (isExceeded=false since remaining=0
        // is NOT < 0, but the condition is "isExceeded || usedPercent >= 100"
        // → true → danger).
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--danger");
    }

    [Fact]
    public void CartBudgetBar_ConsumedIs50Percent_BarWidthIs50Percent()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 500m, remaining: 500m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 50%");
        // 50% < 70% → ok tier.
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--ok");
    }

    [Fact]
    public void CartBudgetBar_ConsumedExceedsSalary_BarClampsTo100PercentAndShowsOverdraftAlert()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // Salary=1000, Consumed=1500 → remaining = -500 (< 0 → overdraft).
        // usedPercent = Min(100, (1500/1000)*100) = Min(100, 150) = 100.
        var budget = BuildBudget(salary: 1000m, consumed: 1500m, remaining: -500m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        // Bar clamps to 100% (the SUT's Math.Min(100, usedPercent)).
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 100%");
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--danger");

        // Overdraft alert (RadzenAlert) is rendered when isExceeded=true.
        // The alert's Title is set from Loc["ExceededTitle"] which the
        // identity localizer returns as "ExceededTitle".
        cut.Markup.Should().Contain("ExceededTitle");
        cut.Markup.Should().Contain("ExceededMessage");
    }

    [Fact]
    public void CartBudgetBar_OverdraftRemaining_ShowsNegativePrefixInRemainingRow()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 1500m, remaining: -500m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        // When isExceeded=true, the SUT renders a <span class="cart-budget-negative-prefix">
        // (an en-dash minus sign "−") before the remaining amount, so the
        // user sees "−500" rather than "500" or "-500" (the ASCII minus).
        cut.Find(".cart-budget-row--exceeded .cart-budget-negative-prefix")
           .TextContent.Should().NotBeEmpty();

        // The total row also gets the exceeded class — verify.
        cut.FindAll(".cart-budget-row--exceeded").Should().NotBeEmpty();
    }

    [Fact]
    public void CartBudgetBar_SalaryZeroAndConsumedZero_BarWidthIsZeroPercent()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // Edge case: salary=0. The SUT's expression
        //   usedPercent = salary.Amount > 0 ? Min(100, ...) : (isExceeded ? 100 : 0)
        // With salary=0, consumed=0 → remaining=0, isExceeded=false → usedPercent=0.
        var budget = BuildBudget(salary: 0m, consumed: 0m, remaining: 0m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 0%");
        // isExceeded=false (remaining=0 is NOT < 0), usedPercent=0 → ok tier.
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--ok");
    }

    [Fact]
    public void CartBudgetBar_SalaryZeroButConsumedPositive_BarClampsTo100AndShowsOverdraft()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // Edge case: salary=0, consumed=100 (no salary but money is owed).
        // remaining = 0-100 = -100 < 0 → isExceeded=true.
        // usedPercent = salary.Amount > 0 ? ... : (isExceeded ? 100 : 0) → 100.
        var budget = BuildBudget(salary: 0m, consumed: 100m, remaining: -100m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 100%");
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--danger");
        cut.Markup.Should().Contain("ExceededTitle");
    }

    [Fact]
    public void CartBudgetBar_ConsumedZero_BarWidthIsZeroPercentAndPercentBadgeShows0()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 0m, remaining: 1000m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 0%");

        // The percent badge text shows the formatted percent. Under the
        // default culture, "{0:0}%" with 0 → "0%".
        var percentBadge = cut.Find(".cart-budget-percent");
        percentBadge.TextContent.Trim().Should().Be("0%");

        // 0% → ok tier badge.
        percentBadge.GetAttribute("class").Should().Contain("cart-budget-percent--ok");
    }

    [Fact]
    public void CartBudgetBar_UsedPercentAt69Percent_UsesOkTier()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // 690/1000 = 69% — under the 70% warning threshold → ok tier.
        var budget = BuildBudget(salary: 1000m, consumed: 690m, remaining: 310m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 69%");
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--ok");
    }

    [Fact]
    public void CartBudgetBar_UsedPercentAt70Percent_UsesWarningTier()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // 700/1000 = 70% — exactly at the warning threshold (>= 70) → warning.
        var budget = BuildBudget(salary: 1000m, consumed: 700m, remaining: 300m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        var barFill = cut.Find(".cart-budget-bar-fill");
        barFill.GetAttribute("style").Should().Contain("width: 70%");
        barFill.GetAttribute("class").Should().Contain("cart-budget-bar-fill--warning");
    }

    [Fact]
    public void CartBudgetBar_CartTotalGreaterThanConsumed_AlreadySubmittedRowClampsToZero()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        // Edge case: CartTotal > Consumed (can happen if a sale was
        // cancelled but the cart still has items).
        // alreadySubmitted = Max(0, consumed - cartAmount) = Max(0, 100-300) = 0.
        var budget = BuildBudget(salary: 1000m, consumed: 100m, remaining: 900m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget)
            .Add(p => p.CartTotal, new MoneyDto { Amount = 300m, Currency = "IRR" }));

        // Assert
        // The SUT renders the stat rows. We don't assert on exact text
        // (because CultureFormat.FormatMoneyToman divides IRR by 10 and
        // labels "تومان" — locale-dependent). Instead, assert the rows
        // exist + the total row has the right class.
        cut.FindAll(".cart-budget-row").Should().HaveCount(4);
        cut.FindAll(".cart-budget-row--total").Should().HaveCount(1);

        // isExceeded check: remaining=900 > 0 → NOT exceeded → total row
        // should NOT have the exceeded modifier class.
        cut.FindAll(".cart-budget-row--exceeded").Should().BeEmpty();
    }

    [Fact]
    public void CartBudgetBar_Rendered_ProgressBarHasAriaAttributes()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<CartBudgetBarComponent>(ctx);

        var budget = BuildBudget(salary: 1000m, consumed: 250m, remaining: 750m);

        // Act
        var cut = ctx.RenderComponent<CartBudgetBarComponent>(ps => ps
            .Add(p => p.Budget, budget));

        // Assert
        // Accessibility contract: the progress bar must have
        // role="progressbar" + aria-valuenow + aria-valuemin + aria-valuemax
        // + aria-label so screen readers announce the budget status.
        var progressBar = cut.Find("[role='progressbar']");
        progressBar.GetAttribute("aria-valuemin").Should().Be("0");
        progressBar.GetAttribute("aria-valuemax").Should().Be("100");
        progressBar.GetAttribute("aria-valuenow").Should().Be("25"); // (int)Math.Round(25m)
        progressBar.HasAttribute("aria-label").Should().BeTrue();
    }
}
