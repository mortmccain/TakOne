using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.SubmitSaleConfirmationDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>SubmitSaleConfirmationDialog</c> razor component
/// (Components/Dialogs/SubmitSaleConfirmationDialog/SubmitSaleConfirmationDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A second-step confirmation modal for cart submission.
/// Opened by the shop mini-cart (Products.razor), the full Cart page, and
/// the mobile cart via
/// <c>DialogService.OpenAsync&lt;SubmitSaleConfirmationDialog&gt;(...)</c>
/// passing two parameters: <c>LineCount</c> (int) and <c>TotalDisplay</c>
/// (string? — the caller-formatted grand total). Returns <c>true</c> if
/// confirmed, <c>false</c>/<c>null</c> otherwise. Mirrors the 3-second
/// countdown pattern of CancelConfirmationDialog + the deactivate dialogs
/// — submitting an order is irreversible from the shopper's side (only
/// staff/support can cancel it afterwards), so the countdown provides
/// friction against reflex confirmations.
/// </para>
/// <para>
/// <b>SUT body.</b> The dialog renders the cart summary (item count
/// digit-localized via <c>CultureFormat.FormatDigits</c> + the
/// pre-formatted total) in a <c>&lt;strong&gt;</c> tag inside the body,
/// followed by the two owner-required warning sentences: the order can no
/// longer be changed or cancelled by the shopper, and cancelling after
/// submission requires contacting support.
/// </para>
/// <para>
/// <b>Localizer stub.</b> The tests run under the shared
/// <c>AddIdentityLocalizer</c> stub, whose <c>IStringLocalizer</c> returns
/// the KEY itself for missing keys — so assertions match key names like
/// "ButtonYesCounting" verbatim in the rendered markup.
/// </para>
/// </remarks>
public class SubmitSaleConfirmationDialogTests
{
    [Fact]
    public void SubmitSaleConfirmationDialog_Rendered_ShowsCartSummaryInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        var loc = ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);
        // The identity stub returns the KEY as the value, which would make
        // string.Format over the summary template a no-op. Configure the
        // one format template this test asserts on so the {0}/{1} holes
        // actually fill — verifying the parameters flow into the summary.
        loc["SummaryFormat"].Returns(
            new LocalizedString("SummaryFormat", "{0} items - total {1}", false));

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 3)
            .Add(p => p.TotalDisplay, "1,250,000 IRT"));

        // Assert — the summary line renders the digit-localized item count
        // and the caller-formatted total inside a <strong> tag.
        cut.Markup.Should().Contain("3");
        cut.Markup.Should().Contain("1,250,000 IRT");
        cut.FindAll("strong").Should().NotBeEmpty();
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_Rendered_ShowsIrreversibilityAndSupportWarnings()
    {
        // Arrange — the owner-required copy: the action cannot be undone
        // by the shopper, and cancelling after submission requires
        // contacting support.
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 2)
            .Add(p => p.TotalDisplay, "500,000 IRT"));

        // Assert — under the identity localizer the body sentences render
        // their key names verbatim.
        cut.Markup.Should().Contain("BodyPermanent");
        cut.Markup.Should().Contain("BodySupport");
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_RenderedInitially_ConfirmButtonIsDisabledAndShowsCountdownLabel()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 1)
            .Add(p => p.TotalDisplay, "100,000 IRT"));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesCounting"));
        confirmButton.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_AfterCountdownCompletes_ConfirmButtonIsEnabled()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 1)
            .Add(p => p.TotalDisplay, "100,000 IRT"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_AfterCountdownCompletes_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 4)
            .Add(p => p.TotalDisplay, "2,000,000 IRT"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion removes the async-completion race on the click
        // pipeline (same hardening as the other countdown dialogs).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act — the No button is enabled immediately (no countdown on the
        // safe path), so this needs no WaitForState.
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 1)
            .Add(p => p.TotalDisplay, "100,000 IRT"));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 1)
            .Add(p => p.TotalDisplay, "100,000 IRT"));

        // Assert
        var warning = cut.Find(".submit-sale-confirmation-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void SubmitSaleConfirmationDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<SubmitSaleConfirmationDialog>(ctx);

        // Act
        var cut = ctx.Render<SubmitSaleConfirmationDialog>(ps => ps
            .Add(p => p.LineCount, 1)
            .Add(p => p.TotalDisplay, "100,000 IRT"));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }
}
