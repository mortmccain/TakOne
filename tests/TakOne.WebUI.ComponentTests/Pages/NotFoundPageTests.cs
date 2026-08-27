using Bunit;
using FluentAssertions;
using TakOne.WebUI.Components.Pages;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// bUnit tests for the <c>NotFound</c> razor page
/// (Components/Pages/NotFound.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A trivial static page: a single <c>&lt;h3&gt;Not
/// Found&lt;/h3&gt;</c> heading + a single <c>&lt;p&gt;</c> apology
/// paragraph. No parameters, no DI, no auth, no @page routing logic.
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b> The task spec said
/// "markup contains a 'Go home' link or similar". The actual SUT does
/// NOT render any link — it's just a heading + a sentence. Tests below
/// verify what the SUT actually exposes (the heading + the apology
/// text + the non-empty markup) and explicitly assert that no link
/// element exists, documenting the gap from the spec.
/// </para>
/// </remarks>
public class NotFoundPageTests
{
    [Fact]
    public void NotFound_Rendered_ShowsNotFoundHeading()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<NotFound>();

        // Assert
        // The heading text is hard-coded "Not Found" — no localization.
        cut.Find("h3").TextContent.Should().Be("Not Found");
    }

    [Fact]
    public void NotFound_Rendered_ShowsApologyParagraphText()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<NotFound>();

        // Assert
        // The apology sentence is hard-coded English text.
        cut.Find("p").TextContent.Should().Contain("Sorry, the content you are looking for does not exist.");
    }

    [Fact]
    public void NotFound_Rendered_HasNoGoHomeLink()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<NotFound>();

        // Assert
        // The task spec mentioned "a 'Go home' link or similar". The actual
        // SUT has NO link — we assert on this gap to document the divergence
        // and lock in the current behavior (so a future contributor adding
        // a link would notice this test failing + can update both).
        cut.FindAll("a").Should().BeEmpty("NotFound.razor has no anchor elements");
        cut.FindAll("button").Should().BeEmpty("NotFound.razor has no buttons");
    }

    [Fact]
    public void NotFound_Rendered_MarkupIsNonEmpty()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<NotFound>();

        // Assert
        // Sanity check: the page renders SOMETHING (non-empty markup).
        // If the page ever accidentally short-circuits on init (e.g. an
        // unhandled exception in a future OnInitialized override), this
        // test fails first, ahead of the heading-text assertion.
        cut.Markup.Should().NotBeEmpty();
        cut.Markup.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void NotFound_Rendered_HasExactlyOneHeadingAndOneParagraph()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<NotFound>();

        // Assert
        // Lock in the minimal structure — exactly one h3 + one p. A future
        // change adding a sub-heading or a second paragraph should fail
        // here so the maintainer consciously updates the markup contract.
        cut.FindAll("h3").Should().HaveCount(1);
        cut.FindAll("p").Should().HaveCount(1);
    }
}
