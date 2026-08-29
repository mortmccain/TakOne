using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.RestockProductDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>RestockProductDialog</c> razor component
/// (Components/Dialogs/RestockProductDialog/RestockProductDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A popup dialog for product restock (add to stock).
/// Opened by AdminProducts.razor's <c>HandleRestockAsync</c> via
/// <c>DialogService.OpenAsync&lt;RestockProductDialog&gt;(...)</c>
/// passing <c>ProductName</c> (string) and <c>CurrentStock</c> (int).
/// Returns the entered quantity (int &gt; 0) if confirmed, or
/// <c>null</c>/<c>false</c> otherwise.
/// </para>
/// <para>
/// <b>SUT parameters + state.</b>
/// <list type="bullet">
///   <item><c>[Parameter] ProductName</c> (string) — shown in body.</item>
///   <item><c>[Parameter] CurrentStock</c> (int) — shown in current-stock box.</item>
///   <item><c>private int? _quantity = 10</c> — defaults to 10 (the
///     previous JS prompt default).</item>
///   <item><c>private bool _showValidationError</c> — false initially;
///     set to true after clicking Confirm with an invalid quantity
///     (null or &lt;= 0). Stays false until then so we don't shame the
///     user on first render.</item>
/// </list>
/// </para>
/// <para>
/// <b>SUT validation rules.</b> The <c>OnConfirm</c> handler:
/// <list type="bullet">
///   <item>If <c>_quantity is null</c> OR <c>_quantity &lt;= 0</c> →
///     set <c>_showValidationError = true</c> and return (no close).</item>
///   <item>Otherwise → <c>DialogService.Close(_quantity.Value)</c>.</item>
/// </list>
/// So the rejection paths are: empty/null quantity, zero, negative.
/// Tests below cover each path.
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b>
/// <list type="bullet">
///   <item>No <c>[Parameter] EventCallback&lt;int&gt; OnConfirm</c>
///     — the SUT calls <c>DialogService.Close(int)</c> directly.
///     Tests assert on the substitute's <c>Close(...)</c> calls.</item>
///   <item>No <c>Visible</c> parameter — the dialog is opened by the
///     parent's <c>DialogService.OpenAsync&lt;T&gt;</c>.</item>
///   <item>NO countdown on this dialog (unlike DeactivateUserDialog).</item>
///   <item>The native <c>&lt;input type="number"&gt;</c> (NOT
///     <c>RadzenNumeric</c>) is used — the SUT doc explains this is
///     because RadzenNumeric's JS mishandles caret position in RTL
///     (fa-IR) mode.</item>
/// </list>
/// </para>
/// <para>
/// <b>Input handling.</b> bUnit's <c>cut.Find("input").Input("100")</c>
/// sets the input value AND triggers <c>@oninput</c>/<c>@bind</c>. The
/// SUT uses <c>@bind="_quantity"</c> (two-way binding). bUnit's
/// <c>Input("100")</c> updates the bound field to 100 (parsed as int
/// because the input is type=number).
/// </para>
/// </remarks>
public class RestockProductDialogTests
{
    [Fact]
    public void RestockProductDialog_Rendered_ShowsProductNameInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        cut.Markup.Should().Contain("Apple");
        cut.FindAll("strong").Should().Contain(s => s.TextContent.Contains("Apple"));
    }

    [Fact]
    public void RestockProductDialog_Rendered_NoValidationErrorShownInitially()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // _showValidationError starts as false. The validation error
        // text (Loc["QuantityRequired"]) is wrapped in @if(_showValidationError).
        // On first render, it should NOT be present.
        cut.Markup.Should().NotContain("QuantityRequired");
    }

    [Fact]
    public void RestockProductDialog_Rendered_ShowsCurrentStockWithSuccessAccent()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 12345));

        // Assert
        // The current-stock box has a success-green left border (var(--rz-success))
        // to distinguish it visually from the DeactivateProductDialog's
        // warning-amber accent on the same box.
        cut.Markup.Should().Contain("border-left: 4px solid var(--rz-success)");
        cut.Markup.Should().Contain("CurrentStockLabel");
        cut.Markup.Should().Contain("12,345"); // N0-formatted
    }

    [Fact]
    public void RestockProductDialog_DefaultQuantityIs10_ClickConfirm_CallsDialogServiceCloseWith10()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Don't change the quantity input — the default _quantity=10 should
        // be in the bound field.
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // Close is called with _quantity.Value = 10 (the default).
        // WaitForAssertion removes the async-completion race on the click
        // pipeline (same hardening as the other dialog tests).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, 10))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RestockProductDialog_SetQuantityTo100_ClickConfirm_CallsDialogServiceCloseWith100()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Find the numeric input by its type attribute (the SUT uses
        // <input type="number" ... @bind="_quantity">).
        // NOTE: @bind defaults to onchange (not oninput), so bUnit's
        // .Change("100") is the correct API to trigger the binding.
        // .Input("100") would throw MissingEventHandlerException because
        // the @oninput handler isn't bound.
        var quantityInput = cut.Find("input[type='number']");
        quantityInput.Change("100");
        // Click the confirm button — the OnConfirm handler reads
        // _quantity (now 100), validates 100 > 0, calls Close(100).
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion for the async-completion race (see above).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, 100))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RestockProductDialog_EmptyQuantityInput_ClickConfirm_ShowsValidationErrorAndDoesNotClose()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Clear the input — bUnit's Change("") sets the value to empty
        // string, which the @bind parser fails to convert to int →
        // _quantity becomes null.
        var quantityInput = cut.Find("input[type='number']");
        quantityInput.Change("");
        // Click confirm — OnConfirm sees _quantity is null → sets
        // _showValidationError = true and returns WITHOUT closing.
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        // The validation error is now visible.
        cut.Markup.Should().Contain("QuantityRequired");

        // The DialogService.Close was NOT called (the SUT short-circuited
        // on the validation failure).
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.DidNotReceive().Close(Arg.Any<object?>());
    }

    [Fact]
    public void RestockProductDialog_QuantityZero_ClickConfirm_ShowsValidationErrorAndDoesNotClose()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Set quantity to 0 — the SUT's OnConfirm rejects _quantity <= 0.
        var quantityInput = cut.Find("input[type='number']");
        quantityInput.Change("0");
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        cut.Markup.Should().Contain("QuantityRequired");
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.DidNotReceive().Close(Arg.Any<object?>());
    }

    [Fact]
    public void RestockProductDialog_QuantityNegative_ClickConfirm_ShowsValidationErrorAndDoesNotClose()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        // Set quantity to -5 — the SUT's OnConfirm rejects _quantity <= 0.
        var quantityInput = cut.Find("input[type='number']");
        quantityInput.Change("-5");
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonConfirm"));
        confirmButton.Click();

        // Assert
        cut.Markup.Should().Contain("QuantityRequired");
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.DidNotReceive().Close(Arg.Any<object?>());
    }

    [Fact]
    public void RestockProductDialog_ClickCancel_CallsDialogServiceCloseWithNull()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonCancel"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // The Cancel button calls DialogService.Close(null) — the parent
        // treats anything that isn't a positive int as "aborted".
        // WaitForAssertion for the async-completion race (see above).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(null),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RestockProductDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }

    [Fact]
    public void RestockProductDialog_Rendered_QuantityInputHasMinAttribute1()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RestockProductDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RestockProductDialog>(ps => ps
            .Add(p => p.ProductName, "Apple")
            .Add(p => p.CurrentStock, 50));

        // Assert
        // The SUT's <input type="number" min="1" step="1" @bind="_quantity">
        // enforces positivity at the HTML level (browsers reject negative
        // spinner clicks). The server-side OnConfirm ALSO rejects <= 0 —
        // defense in depth.
        var quantityInput = cut.Find("input[type='number']");
        quantityInput.GetAttribute("min").Should().Be("1");
        quantityInput.GetAttribute("step").Should().Be("1");
    }
}
