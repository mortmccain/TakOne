using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.RemoveGroupDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>RemoveGroupDialog</c> razor component
/// (Components/Dialogs/RemoveGroupDialog/RemoveGroupDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A confirmation modal for removing a user from
/// their customer group. Mirrors RemoveRoleDialog (no countdown).
/// Parameters: <c>UserDisplayName</c> (string) + <c>GroupName</c>
/// (string). Returns <c>true</c> if confirmed, <c>false</c>/<c>null</c>
/// otherwise.
/// </para>
/// <para>
/// <b>SUT discovery.</b> Same pattern as the other dialogs: no
/// EventCallback parameters, no Visible parameter. The Yes button is
/// immediately clickable (no countdown).
/// </para>
/// </remarks>
public class RemoveGroupDialogTests
{
    [Fact]
    public void RemoveGroupDialog_Rendered_ShowsUserDisplayNameAndGroupNameInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));

        // Assert
        // The SUT body has TWO <strong> elements: one for the user name
        // and one for the group name.
        cut.Markup.Should().Contain("Alice");
        cut.Markup.Should().Contain("Wholesale Tier");
        var strongElements = cut.FindAll("strong");
        strongElements.Should().HaveCountGreaterThanOrEqualTo(2);
        strongElements.Should().Contain(s => s.TextContent.Contains("Alice"));
        strongElements.Should().Contain(s => s.TextContent.Contains("Wholesale Tier"));
    }

    [Fact]
    public void RemoveGroupDialog_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYes"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true)));
    }

    [Fact]
    public void RemoveGroupDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false)));
    }

    [Fact]
    public void RemoveGroupDialog_Rendered_ConfirmButtonIsImmediatelyEnabled()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));

        // Assert
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYes"));
        confirmButton.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void RemoveGroupDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));

        // Assert
        var warning = cut.Find(".remove-group-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void RemoveGroupDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveGroupDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveGroupDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.GroupName, "Wholesale Tier"));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }
}
