using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.CancelConfirmationDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>CancelConfirmationDialog</c> razor component
/// (Components/Dialogs/CancelConfirmationDialog/CancelConfirmationDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A second-step confirmation modal for sale
/// cancellation. Opened by SaleDetail.razor via
/// <c>DialogService.OpenAsync&lt;CancelConfirmationDialog&gt;(...)</c>
/// passing a single parameter: <c>SaleNumber</c> (string?). Returns
/// <c>true</c> if confirmed, <c>false</c>/<c>null</c> otherwise.
/// Mirrors the 3-second countdown pattern of DeactivateUserDialog +
/// DeactivateGroupDialog — sale cancellation is irreversible, so the
/// countdown provides friction against reflex confirmations.
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b>
/// <list type="bullet">
///   <item>The task spec said "Has a reason input. Parameters likely
///     include: Visible, OnConfirm (EventCallback&lt;string&gt;),
///     OnCancel." The actual SUT has NONE of these — no reason input,
///     no EventCallback, no Visible parameter. It only takes
///     <c>SaleNumber</c> (string?) and calls <c>DialogService.Close(true)</c>
///     or <c>Close(false)</c>.</item>
///   <item>The task spec also mentioned "reason longer than 500 chars
///     → rejected (if the SUT has a max length check)". The SUT has NO
///     reason input + NO max-length validation. Tests below do NOT
///     cover this path (it doesn't exist).</item>
/// </list>
/// </para>
/// <para>
/// <b>SUT body.</b> The SUT renders the SaleNumber (when non-null) in
/// a <c>&lt;strong&gt;</c> tag inside the body, formatted via
/// <c>CultureFormat.FormatDigits(SaleNumber)</c> for culture-native
/// digits (Persian in fa-IR mode, ASCII in en-US mode). When null,
/// the SUT renders an em-dash placeholder ("—") instead.
/// </para>
/// </remarks>
public class CancelConfirmationDialogTests
{
    [Fact]
    public void CancelConfirmationDialog_Rendered_ShowsSaleNumberInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));

        // Assert
        // The SUT renders SaleNumber via CultureFormat.FormatDigits — under
        // en-US (default test culture), Persian digits in the input are
        // converted to ASCII. The full string "INT-1403-00000001" should
        // be present (ASCII form).
        cut.Markup.Should().Contain("INT-1403-00000001");
        cut.FindAll("strong").Should().NotBeEmpty();
    }

    [Fact]
    public void CancelConfirmationDialog_Rendered_WithNullSaleNumber_ShowsEmDashPlaceholder()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, null));

        // Assert
        // When SaleNumber is null, the SUT renders "—" (em-dash) instead
        // of the sale number — the conditional is
        // `@(SaleNumber is null ? "—" : CultureFormat.FormatDigits(SaleNumber))`.
        cut.Markup.Should().Contain("—");
    }

    [Fact]
    public void CancelConfirmationDialog_RenderedInitially_ConfirmButtonIsDisabledAndShowsCountdownLabel()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesCounting"));
        confirmButton.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void CancelConfirmationDialog_AfterCountdownCompletes_ConfirmButtonIsEnabled()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void CancelConfirmationDialog_AfterCountdownCompletes_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true)));
    }

    [Fact]
    public void CancelConfirmationDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false)));
    }

    [Fact]
    public void CancelConfirmationDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));

        // Assert
        var warning = cut.Find(".cancel-confirmation-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void CancelConfirmationDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<CancelConfirmationDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<CancelConfirmationDialog>(ps => ps
            .Add(p => p.SaleNumber, "INT-1403-00000001"));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }
}
