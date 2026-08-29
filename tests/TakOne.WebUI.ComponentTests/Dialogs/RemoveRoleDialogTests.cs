using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.WebUI.Components.Dialogs.RemoveRoleDialog;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Dialogs;

/// <summary>
/// bUnit tests for the <c>RemoveRoleDialog</c> razor component
/// (Components/Dialogs/RemoveRoleDialog/RemoveRoleDialog.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A confirmation modal for removing a role from a
/// user. Mirrors DeactivateUserDialog's structure but WITHOUT the
/// 3-second countdown (role removal is low-risk + instantly reversible
/// via re-assign). Parameters: <c>UserDisplayName</c> (string) and
/// <c>RoleLabel</c> (string — the localized role label, e.g. "Customer").
/// Returns <c>true</c> if confirmed, <c>false</c>/<c>null</c> otherwise.
/// </para>
/// <para>
/// <b>SUT discovery.</b> Same pattern as other dialogs: no EventCallback
/// parameters, no Visible parameter. The Yes button is immediately
/// clickable (no countdown). The body text mentions BOTH
/// <c>UserDisplayName</c> AND <c>RoleLabel</c> in <c>&lt;strong&gt;</c>
/// tags.
/// </para>
/// </remarks>
public class RemoveRoleDialogTests
{
    [Fact]
    public void RemoveRoleDialog_Rendered_ShowsUserDisplayNameAndRoleLabelInMarkup()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));

        // Assert
        // The SUT body has TWO <strong> elements: one for the user name
        // and one for the role label. Both should appear in the markup.
        cut.Markup.Should().Contain("Alice");
        cut.Markup.Should().Contain("Manager");
        var strongElements = cut.FindAll("strong");
        strongElements.Should().HaveCountGreaterThanOrEqualTo(2);
        strongElements.Should().Contain(s => s.TextContent.Contains("Alice"));
        strongElements.Should().Contain(s => s.TextContent.Contains("Manager"));
    }

    [Fact]
    public void RemoveRoleDialog_ClickConfirm_CallsDialogServiceCloseWithTrue()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYes"));
        confirmButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion removes the async-completion race on the click
        // pipeline (same hardening as the other dialog tests).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, true))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RemoveRoleDialog_ClickCancel_CallsDialogServiceCloseWithFalse()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));
        var cancelButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonNo"));
        cancelButton.Click();

        // Assert
        var spy = ComponentTestSetup.GetDialogServiceSpy(ctx);
        // WaitForAssertion for the async-completion race (see above).
        cut.WaitForAssertion(
            () => spy.Received(1).Close(Arg.Is<object?>(o => Equals(o, false))),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RemoveRoleDialog_Rendered_ConfirmButtonIsImmediatelyEnabled()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));

        // Assert
        // No countdown on this dialog (unlike DeactivateUserDialog).
        // The Yes button should be enabled from the very first render.
        var confirmButton = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("ButtonYes"));
        confirmButton.HasAttribute("disabled").Should().BeFalse(
            "RemoveRoleDialog has no countdown — confirm is immediately clickable");
    }

    [Fact]
    public void RemoveRoleDialog_Rendered_WarningHeadingHasWarningClass()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));

        // Assert
        var warning = cut.Find(".remove-role-dialog__warning");
        warning.TextContent.Should().Contain("WarningHeading");
    }

    [Fact]
    public void RemoveRoleDialog_Rendered_HasExactlyTwoButtons()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddDialogServiceSpy(ctx);
        ComponentTestSetup.AddIdentityLocalizer<RemoveRoleDialog>(ctx);

        // Act
        var cut = ctx.RenderComponent<RemoveRoleDialog>(ps => ps
            .Add(p => p.UserDisplayName, "Alice")
            .Add(p => p.RoleLabel, "Manager"));

        // Assert
        cut.FindAll("button").Should().HaveCount(2);
    }
}
