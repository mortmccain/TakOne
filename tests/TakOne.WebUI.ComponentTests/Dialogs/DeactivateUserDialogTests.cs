using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.DeactivateUserDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>DeactivateUserDialog</c> razor component
/// (Components/Dialogs/DeactivateUserDialog/DeactivateUserDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A second-step confirmation modal for user
/// deactivation. The dialog is opened by AdminUsers.razor + UserDetail.razor
/// via <c>DialogService.OpenAsync&lt;DeactivateUserDialog&gt;(...)</c>
/// passing a single parameter: <c>UserDisplayName</c> (the user's
/// full name). The dialog returns <c>true</c> if the user confirmed,
/// <c>false</c>/<c>null</c> otherwise (matching the
/// CancelConfirmationDialog contract).
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b>
/// <list type="bullet">
///   <item>The task spec mentioned <c>[Parameter] EventCallback OnConfirm</c>
///     and <c>[Parameter] EventCallback OnCancel</c>. The actual SUT does
///     NOT use EventCallbacks — instead the Confirm/Cancel buttons call
///     <c>DialogService.Close(true)</c> / <c>DialogService.Close(false)</c>
///     directly. Tests below assert on the substitute's <c>Close(...)</c>
///     calls rather than captured EventCallback values.</item>
///   <item>The task spec mentioned a <c>Visible</c> boolean parameter.
///     The actual SUT has no <c>Visible</c> parameter — Radzen dialogs
///     are opened by the parent's <c>DialogService.OpenAsync&lt;T&gt;</c>
///     call, which renders the component as a modal. There's no
///     visibility flag for tests to toggle. Tests below render the
///     component directly via bUnit (no Radzen dialog hosting) and
///     assert on the rendered markup.</item>
///   <item>The task spec mentioned a "I understand this cannot be undone"
///     checkbox gating the confirm button. The actual SUT uses a
///     <b>3-second countdown</b> instead — the Yes button is
///     <c>Disabled="@(countdown &gt; 0)"</c> for the first 3 seconds
///     after the dialog opens.</item>
/// </list>
/// </para>
/// <para>
/// <b>bUnit async-render timing.</b> bUnit's
/// <c>RenderComponent&lt;T&gt;</c> awaits the SYNCHRONOUS portion of
/// the initial render plus the initial lifecycle methods up to the
/// first <c>await</c>. For <c>OnInitializedAsync</c> with a
/// <c>Task.Delay</c>-based countdown loop, this means:
/// <list type="number">
///   <item><c>RenderComponent</c> returns AFTER the first
///     <c>await Task.Delay(...)</c> in <c>OnInitializedAsync</c> is
///     hit. At that point, <c>countdown == 3</c>, the button text is
///     "ButtonYesCounting" (with the countdown number substituted),
///     and the button is <c>Disabled=true</c>.</item>
///   <item>To observe the post-countdown state, tests use bUnit's
///     <c>cut.WaitForState(predicate, timeout)</c> which polls the
///     rendered markup until the predicate is true or the timeout
///     elapses. The countdown takes ~3 seconds, so the timeout is
///     set to 5 seconds.</item>
/// </list>
/// </para>
/// <para>
/// <b>Test runtime.</b> Tests that DON'T need the countdown to
/// complete (initial-state + cancel-button tests) finish in &lt;1
/// second. Tests that DO need the countdown to complete (confirm-
/// button-enabled test) take ~3 seconds. xUnit runs test classes in
/// parallel — this class's 6 tests run sequentially within the class
/// (per xUnit's default Collection Behavior), but other dialog test
/// classes run concurrently.
/// </para>
/// <para>
/// <b>Localization stub.</b> The SUT injects
/// <c>IStringLocalizer&lt;DeactivateUserDialog&gt;</c>. The test
/// registers an "identity" stub — <c>Loc["ButtonYesReady"]</c> returns
/// <c>"ButtonYesReady"</c> — so the rendered button text is the
/// resource key itself.
/// </para>
/// </remarks>
public class DeactivateUserDialogTests
{
    [Fact]
    public void DeactivateUserDialog_Rendered_ShowsUserDisplayNameInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice Johnson"));

        // Assert
        // The SUT renders the user's name in a <strong> tag inside the
        // body paragraph: <strong>@UserDisplayName</strong>.
        cut.Markup.Should().Contain("Alice Johnson");
        var strongElements = cut.FindAll("strong");
        strongElements.Should().HaveCountGreaterThan(0);
        strongElements.Any(s => s.TextContent.Contains("Alice Johnson")).Should().BeTrue();
    }

    [Fact]
    public void DeactivateUserDialog_RenderedInitially_ConfirmButtonIsDisabledAndShowsCountdownLabel()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        // RenderComponent returns after the first await in
        // OnInitializedAsync — at that point countdown=3 and the
        // button text is "ButtonYesCounting" with the countdown
        // number substituted via string.Format.
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));

        // Assert
        // Find the danger-styled button (the "Yes" button — it's
        // the only button with rz-button-danger class).
        var confirmButton = cut.FindAll("button")
            .Single(b => b.GetAttribute("class")?.Contains("rz-button-danger") == true
                         || b.TextContent.Contains("ButtonYesCounting"));

        // Disabled="disabled" attribute should be present because
        // countdown > 0 initially.
        confirmButton.HasAttribute("disabled").Should().BeTrue(
            "the confirm button is Disabled while countdown > 0");

        // The button's text should be "ButtonYesCounting" (with the
        // countdown number formatted in via {0}). The identity localizer
        // returns "ButtonYesCounting" as the format string, and the SUT
        // calls string.Format with countdown=3 → "ButtonYesCounting 3"
        // or "ButtonYesCounting" + the localized separator + "3".
        confirmButton.TextContent.Should().Contain("ButtonYesCounting");
    }

    [Fact]
    public void DeactivateUserDialog_AfterCountdownCompletes_ConfirmButtonIsEnabledAndLabelIsReady()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));

        // Wait for the countdown to complete (max 5s — the SUT's
        // countdown is 3 iterations × 1s = ~3s). The condition is
        // "any button contains ButtonYesReady" — that text appears
        // once the SUT's ButtonText switches from the counting format
        // to Loc["ButtonYesReady"].
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.HasAttribute("disabled").Should().BeFalse(
            "the confirm button is enabled once countdown reaches 0");
    }

    [Fact]
    public void DeactivateUserDialog_AfterCountdownCompletes_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // Use Equals(o, true) instead of `o is true` because NSubstitute's
        // Arg.Is<T>(predicate) is an expression-tree, and expression trees
        // can't contain C# pattern-matching operators (CS8122).
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true)));
    }

    [Fact]
    public void DeactivateUserDialog_ClickCancelButton_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        // The Cancel button has no Disabled gate (no countdown applies
        // to it) — it's immediately clickable after the initial render.
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));
        // Cancel button is the Secondary-style button labeled "ButtonNo"
        // via the identity localizer. We find it by the localized text
        // (Radzen assigns rz-button-secondary class to Secondary-style
        // buttons — but matching by text is more robust to CSS-class
        // renames).
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false)));
    }

    [Fact]
    public void DeactivateUserDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));

        // Assert
        // The SUT renders the warning heading in a RadzenText with
        // Class="deactivate-user-dialog__warning". RadzenText renders
        // as a <div> or <p> depending on TextStyle — we just check the
        // CSS class is applied so a future contributor renaming it
        // would surface here.
        var warning = cut.Find(".deactivate-user-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void DeactivateUserDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateUserDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateUserDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice"));

        // Assert
        // The footer has a No (Secondary) + Yes (Danger) button — exactly
        // 2 buttons. No checkbox, no extra controls. A future contributor
        // adding a "remember my choice" checkbox would fail this test,
        // surfacing the markup contract change.
        cut.FindAll("button").Should().HaveCount(2);
    }
}
