using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.DeactivateGroupDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>DeactivateGroupDialog</c> razor component
/// (Components/Dialogs/DeactivateGroupDialog/DeactivateGroupDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> Mirrors <c>DeactivateUserDialog</c> exactly — same
/// 3-second countdown pattern, same footer button labels, same warning
/// heading structure. The only differences are: it's for customer-group
/// deactivation, the parameter is <c>GroupName</c> (not UserDisplayName),
/// and the localized resource keys live under a different resx file.
/// </para>
/// <para>
/// <b>SUT discovery.</b> Same as DeactivateUserDialog: no EventCallback
/// parameters, no Visible parameter. The 3-second countdown gates the
/// Yes button via <c>Disabled="@(countdown &gt; 0)"</c>.
/// </para>
/// <para>
/// <b>Test runtime.</b> Same as DeactivateUserDialogTests — tests that
/// wait for the countdown to complete take ~3 seconds. xUnit runs
/// classes in parallel so the wall-clock impact is amortized.
/// </para>
/// </remarks>
public class DeactivateGroupDialogTests
{
    [Fact]
    public void DeactivateGroupDialog_Rendered_ShowsGroupNameInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP Customers"));

        // Assert
        cut.Markup.Should().Contain("VIP Customers");
        cut.FindAll("strong").Should().Contain(s => s.TextContent.Contains("VIP Customers"));
    }

    [Fact]
    public void DeactivateGroupDialog_RenderedInitially_ConfirmButtonIsDisabledAndShowsCountdownLabel()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        // RenderComponent returns after the first await in OnInitializedAsync
        // (the 3-iteration countdown loop's first Task.Delay). At that point
        // countdown=3, button Disabled=true, text="ButtonYesCounting ...".
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesCounting"));
        confirmButton.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void DeactivateGroupDialog_AfterCountdownCompletes_ConfirmButtonIsEnabled()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void DeactivateGroupDialog_AfterCountdownCompletes_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));
        cut.WaitForState(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ButtonYesReady")),
            TimeSpan.FromSeconds(5));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYesReady"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion: the Radzen click pipeline can complete the
        // DialogService.Close call asynchronously w.r.t. the test thread
        // (observed as an intermittent ~1-in-15 flake with an immediate
        // assert). Polling the spy through bUnit's renderer-aware waiter
        // removes that race without weakening the assertion.
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DeactivateGroupDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion for the same async-completion race as the
        // confirm-click test above (deterministic even under load).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DeactivateGroupDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));

        // Assert
        var warning = cut.Find(".deactivate-group-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void DeactivateGroupDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<DeactivateGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<DeactivateGroupDialog>(ps => ps
            .Add(p => p.GroupName, "VIP"));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }
}
