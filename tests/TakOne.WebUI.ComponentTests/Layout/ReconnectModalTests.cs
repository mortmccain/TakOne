using Bunit;
using FluentAssertions;
using TakOne.WebUI.Components.Layout;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Layout;

/// <summary>
/// bUnit tests for <see cref="ReconnectModal"/> razor component
/// (Components/Layout/ReconnectModal.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A STATICALLY-RENDERED component: a single
/// <c>&lt;dialog id="components-reconnect-modal"&gt;</c> with the
/// complete reconnect-state message structure (first-attempt, repeated
/// attempt, failed, paused, resume-failed) baked into the markup.
/// Visibility is driven ENTIRELY by Blazor's client-side JS — the
/// <c>ReconnectModal.razor.js</c> module attaches event handlers to
/// <c>window.Blazor</c>'s reconnect events and toggles the
/// <c>.components-reconnect-show</c> / <c>.components-reconnect-hide</c>
/// CSS classes on the &lt;dialog&gt; root at runtime.
/// </para>
/// <para>
/// <b>SUT discovery (bUnit limitation).</b> The SUT includes
/// <c>&lt;script src="@Assets['Components/Layout/ReconnectModal.razor.js']"&gt;&lt;/script&gt;</c>
/// — Blazor 9's static-asset-fingerprint directive. bUnit renders
/// this as a literal <c>&lt;script src="..."&gt;</c> tag (the Razor
/// compiler resolved <c>@Assets</c> to a fingerprinted URL at WebUI
/// build time, baking the string literal into the compiled component).
/// The script is NOT loaded by bUnit (bUnit has no DOM script-execution
/// pipeline). This means the JS that toggles visibility never runs —
/// the test renders the component with ALL the visibility-state
/// paragraphs in the DOM simultaneously, which is the contract we
/// assert against below.
/// </para>
/// <para>
/// <b>JS interop visibility tests.</b> The task spec mentioned testing
/// the visible-when-shown vs visible-when-hidden states. Since the
/// visibility toggling is done in pure JS that bUnit can't execute,
/// these states are NOT observable in bUnit. Tests below assert the
/// always-present markup structure: the &lt;dialog&gt; element, the
/// Retry button, the Resume button, the reconnect animation, and all
/// three visibility-state paragraphs (first-attempt, repeated-attempt,
/// failed). These cover the SUT's rendered markup contract without
/// trying to mock the untestable JS path.
/// </para>
/// </remarks>
public class ReconnectModalTests
{
    [Fact]
    public void ReconnectModal_Rendered_ContainsReconnectDialogElement()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The dialog element is the root of the component. The
        // id="components-reconnect-modal" is the hook Blazor's circuit-
        // reconnect JS attaches to at runtime — present in the markup.
        var dialog = cut.Find("dialog#components-reconnect-modal");
        dialog.Should().NotBeNull();
        // data-nosnippet is present (SEO hint to suppress snippet
        // indexing) — verified separately by the next test using
        // HasAttribute, since GetAttribute returns empty string for
        // valueless attributes.
    }

    [Fact]
    public void ReconnectModal_Rendered_DataNoSnippetAttributePresent()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // data-nosnippet is a SEO hint: search engines should not index
        // this reconnect-state UI. Verify it's present (the markup
        // contract is part of the component's public surface — used by
        // the sitemap exclusion in WebUI's Program.cs).
        var dialog = cut.Find("dialog");
        dialog.HasAttribute("data-nosnippet").Should().BeTrue();
    }

    [Fact]
    public void ReconnectModal_Rendered_ContainsRetryButton()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The Retry button is shown when the reconnect has FAILED (the
        // components-reconnect-failed-visible CSS class controls its
        // actual visibility). Verify the structure is in the markup.
        var retryBtn = cut.Find("button#components-reconnect-button");
        retryBtn.TextContent.Should().Contain("Retry");
    }

    [Fact]
    public void ReconnectModal_Rendered_ContainsResumeButton()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The Resume button is shown when the circuit has been PAUSED
        // by the server (the components-pause-visible +
        // components-resume-failed-visible CSS classes control
        // visibility). Verify its presence in markup.
        var resumeBtn = cut.Find("button#components-resume-button");
        resumeBtn.TextContent.Should().Contain("Resume");
    }

    [Fact]
    public void ReconnectModal_Rendered_ContainsRejoiningAnimationDiv()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The reconnect animation (a spinner / pulsing dots) is rendered
        // inside the .components-rejoining-animation div, with two child
        // divs (the spinning element + the pulsing element).
        var animationContainer = cut.Find(".components-rejoining-animation");
        animationContainer.GetAttribute("aria-hidden").Should().Be("true");
        animationContainer.ChildNodes.Should().HaveCountGreaterThan(2);
    }

    [Fact]
    public void ReconnectModal_Rendered_ContainsAllVisibilityStateParagraphs()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // All four visibility-state paragraphs are present in the markup
        // simultaneously (their actual visibility is toggled by JS via
        // the .components-reconnect-show / .components-reconnect-hide
        // CSS classes on the parent dialog element — untestable in bUnit,
        // but the structural presence is the testable contract).
        cut.Find(".components-reconnect-first-attempt-visible")
           .TextContent.Should().Contain("Rejoining the server");
        cut.Find(".components-reconnect-repeated-attempt-visible")
           .TextContent.Should().Contain("Rejoin failed");
        cut.Find(".components-reconnect-failed-visible")
           .TextContent.Should().Contain("Failed to rejoin");
        cut.Find(".components-pause-visible")
           .TextContent.Should().Contain("session has been paused");
        cut.Find(".components-resume-failed-visible")
           .TextContent.Should().Contain("Failed to resume the session");
    }

    [Fact]
    public void ReconnectModal_Rendered_IncludesSecondsToNextAttemptPlaceholder()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The "Rejoin failed... trying again in X seconds" sentence has
        // a placeholder <span id="components-seconds-to-next-attempt">
        // that the JS fills in at runtime. Verify the span exists —
        // the JS contract relies on this id being stable.
        var secondsSpan = cut.Find("#components-seconds-to-next-attempt");
        secondsSpan.Should().NotBeNull();
    }

    [Fact]
    public void ReconnectModal_Rendered_IncludesScriptTagForRazorJs()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<ReconnectModal>();

        // Assert
        // The component emits a <script type="module" src="..."> tag
        // pointing at ReconnectModal.razor.js (the JS that subscribes
        // to Blazor's circuit-reconnect events). The exact src string
        // is the fingerprinted Blazor 9 asset URL — we don't pin the
        // fingerprint, but we assert the tag exists + the src contains
        // the JS file name. (bUnit does NOT execute the script —
        // verifying the structural contract is enough.)
        var script = cut.Find("script");
        script.GetAttribute("type").Should().Be("module");
        script.GetAttribute("src").Should().Contain("ReconnectModal.razor.js");
    }
}
