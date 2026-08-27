using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.DeactivateProductDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>DeactivateProductDialog</c> razor component
/// (Components/Dialogs/DeactivateProductDialog/DeactivateProductDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A confirmation modal for product deactivation. The
/// dialog is opened by AdminProducts.razor's
/// <c>HandleDeactivateAsync</c> via
/// <c>DialogService.OpenAsync&lt;DeactivateProductDialog&gt;(...)</c>
/// passing two parameters: <c>ProductName</c> (string) and
/// <c>CurrentStock</c> (int). The dialog returns <c>true</c> if the
/// user confirmed, <c>false</c>/<c>null</c> otherwise.
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b>
/// <list type="bullet">
///   <item>No <c>[Parameter] EventCallback</c>s — the Confirm/Cancel
///     buttons call <c>DialogService.Close(true)</c> / <c>Close(false)</c>
///     directly. Tests assert on the substitute's <c>Close(...)</c>
///     calls.</item>
///   <item>No <c>Visible</c> parameter — the dialog is rendered when
///     the parent calls <c>DialogService.OpenAsync&lt;T&gt;</c>.</item>
///   <item>No 3-second countdown — the Yes button is immediately
///     clickable. (The user-deactivation flow has the countdown;
///     product-deactivation doesn't because reverting is just a stock
///     recalculation — lower stakes.)</item>
/// </list>
/// </para>
/// <para>
/// <b>SUT additional behavior.</b> The dialog renders a current-stock
/// display in a bordered box with a warning-amber left border so the
/// user can copy down the stock value before confirming (the SUT
/// doc warns that deactivation zeros out the stock).
/// </para>
/// </remarks>
public class DeactivateProductDialogTests
{
    [Fact]
    public void DeactivateProductDialog_Rendered_ShowsProductNameInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // The SUT renders ProductName in a <strong> tag in the body.
        cut.Markup.Should().Contain("Apple");
        cut.FindAll("strong").Should().Contain(s => s.TextContent.Contains("Apple"));
    }

    [Fact]
    public void DeactivateProductDialog_Rendered_ShowsCurrentStockValueInBorderedBox()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 12345));

        // Assert
        // The SUT formats CurrentStock with N0 (thousands separator) via
        // CultureFormat.FormatNumber — under en-US culture, 12345 → "12,345".
        // (The CurrentStockLabel is also rendered via the identity localizer
        // as the literal key "CurrentStockLabel".)
        cut.Markup.Should().Contain("CurrentStockLabel");
        cut.Markup.Should().Contain("12,345");
    }

    [Fact]
    public void DeactivateProductDialog_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        // No countdown on this dialog — the Yes button is immediately
        // clickable.
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Confirm button is the one labeled "ButtonConfirm" (identity localizer).
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true)));
    }

    [Fact]
    public void DeactivateProductDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonCancel"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false)));
    }

    [Fact]
    public void DeactivateProductDialog_Rendered_WarningHeadingUsesDangerColor()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // The SUT wraps the warning heading in a RadzenText with inline
        // Style="color: var(--rz-danger)". The heading text comes from
        // Loc["WarningHeading"] = "WarningHeading" (identity localizer).
        cut.Markup.Should().Contain("WarningHeading");
        cut.Markup.Should().Contain("--rz-danger");
    }

    [Fact]
    public void DeactivateProductDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // Footer: Cancel + Confirm Deactivation — exactly 2 buttons.
        cut.FindAll("button").Should().HaveCount(2);
    }

    [Fact]
    public void DeactivateProductDialog_Rendered_StockBoxHasWarningLeftBorder()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // The stock-display box has border-left: 4px solid var(--rz-warning)
        // (warning-amber accent) so the user can visually pick out the
        // stock value to copy down before confirming.
        cut.Markup.Should().Contain("border-left: 4px solid var(--rz-warning)");
    }
}
