using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components;
using TakOne.WebUI.Components.Pages;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// bUnit tests for <see cref="Error"/> razor page (Components/Pages/Error.razor).
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> The page is rendered by ASP.NET Core's exception
/// handler when an unhandled error propagates out of the pipeline. The
/// page takes a single <c>[CascadingParameter] HttpContext? HttpContext</c>
/// (private) and computes <c>RequestId = Activity.Current?.Id ??
/// HttpContext?.TraceIdentifier</c>. If RequestId is non-empty, a
/// "Request ID: ..." paragraph is rendered. The page also renders a
/// hard-coded "Development Mode" warning block — it is shown in all
/// environments to nudge the developer toward switching on the dev
/// environment for richer errors.
/// </para>
/// <para>
/// <b>SUT discovery (deviates from task spec).</b> The task spec said
/// "renders with status code 404 → shows 'Not Found' text; renders with
/// status code 500 → shows 'Internal Server Error'". The actual SUT does
/// NOT inspect the status code — the heading "Error." and sub-heading
/// "An error occurred while processing your request." are static. The
/// only dynamic element is the Request ID paragraph. Tests below
/// therefore cover the Request ID visibility logic + the static headings
/// + the development-mode warning, NOT status-code-conditional text.
/// </para>
/// <para>
/// <b>CascadingParameter plumbing.</b> The SUT's <c>HttpContext</c>
/// cascading parameter is declared <c>private</c>. bUnit's parameter
/// builder (<c>ps.Add(p =&gt; p.HttpContext, ...)</c>) cannot target
/// private members (the lambda wouldn't compile). The workaround is to
/// wrap the SUT in a <c>&lt;CascadingValue&lt;HttpContext&gt;&gt;</c>
/// component and pass the <c>Error</c> component as its
/// <c>ChildContent</c> via a <c>RenderFragment</c>.
/// </para>
/// </remarks>
public class ErrorPageTests
{
    [Fact]
    public void Error_RenderedWithNoHttpContext_ShowsStaticHeadingText()
    {
        // Arrange
        // Render Error directly (no CascadingValue<HttpContext> wrapper) —
        // HttpContext will be null, Activity.Current will be null, so
        // RequestId stays null.
        using var ctx = new TestContext();

        // Act
        // RenderComponent<Error> runs OnInitialized which sets
        // RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier.
        // With both null, RequestId = null and ShowRequestId = false.
        var cut = ctx.RenderComponent<Error>();

        // Assert
        // The page should always render the static headings regardless of
        // HttpContext presence — they are not inside an @if guard.
        cut.Find("h1.text-danger").TextContent.Should().Be("Error.");
        cut.FindAll("h2.text-danger").Should().HaveCount(1);
        cut.Find("h2.text-danger").TextContent.Should().Contain("An error occurred");
    }

    [Fact]
    public void Error_RenderedWithNoHttpContext_HidesRequestIdBlock()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<Error>();

        // Assert
        // The "Request ID:" paragraph is wrapped in @if(ShowRequestId) —
        // with no HttpContext and no Activity, ShowRequestId is false, so
        // the <p> should not appear in the markup.
        cut.Markup.Should().NotContain("Request ID:");
    }

    [Fact]
    public void Error_RenderedWithHttpContextHavingTraceIdentifier_ShowsRequestId()
    {
        // Arrange
        using var ctx = new TestContext();
        // Wrap the SUT in a CascadingValue<HttpContext> because the SUT's
        // HttpContext cascading parameter is private (see class remarks).
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace-id-12345";

        // Act
        var cut = ctx.RenderComponent<CascadingValue<HttpContext>>(ps => ps
            .Add(p => p.Value, httpContext)
            .Add(p => p.IsFixed, true)
            .Add(p => p.ChildContent, b =>
            {
                b.OpenComponent<Error>(0);
                b.CloseComponent();
            }));

        // Assert
        // RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier
        // Activity.Current is null in the test thread, so RequestId should
        // fall through to HttpContext.TraceIdentifier = "test-trace-id-12345".
        cut.Markup.Should().Contain("Request ID:");
        cut.Markup.Should().Contain("test-trace-id-12345");
    }

    [Fact]
    public void Error_RenderedWithHttpContextHavingEmptyTraceIdentifier_HidesRequestIdBlock()
    {
        // Arrange
        using var ctx = new TestContext();
        // DefaultHttpContext.TraceIdentifier defaults to a fresh Guid, so
        // we explicitly set it to empty string to test the empty branch.
        var httpContext = new DefaultHttpContext { TraceIdentifier = string.Empty };

        // Act
        var cut = ctx.RenderComponent<CascadingValue<HttpContext>>(ps => ps
            .Add(p => p.Value, httpContext)
            .Add(p => p.IsFixed, true)
            .Add(p => p.ChildContent, b =>
            {
                b.OpenComponent<Error>(0);
                b.CloseComponent();
            }));

        // Assert
        // RequestId = "" (empty string). ShowRequestId = !IsNullOrEmpty("") = false.
        // So the <p>Request ID:</p> block should NOT be rendered.
        cut.Markup.Should().NotContain("Request ID:");
    }

    [Fact]
    public void Error_Rendered_AlwaysShowsDevelopmentModeWarning()
    {
        // Arrange
        using var ctx = new TestContext();

        // Act
        var cut = ctx.RenderComponent<Error>();

        // Assert
        // The "Development Mode" heading + the dev-environment warning text
        // are always rendered (no @if guard around them). The text
        // mentioning the ASPNETCORE_ENVIRONMENT variable is the nudge the
        // page uses to push developers toward the dev environment.
        cut.Markup.Should().Contain("Development Mode");
        cut.Markup.Should().Contain("ASPNETCORE_ENVIRONMENT");
        cut.Markup.Should().Contain("Development environment shouldn't be enabled for deployed applications");
    }

    [Fact]
    public void Error_RenderedWithHttpContext_RendersRequestIdInCodeElement()
    {
        // Arrange
        using var ctx = new TestContext();
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-XYZ" };

        // Act
        var cut = ctx.RenderComponent<CascadingValue<HttpContext>>(ps => ps
            .Add(p => p.Value, httpContext)
            .Add(p => p.IsFixed, true)
            .Add(p => p.ChildContent, b =>
            {
                b.OpenComponent<Error>(0);
                b.CloseComponent();
            }));

        // Assert
        // The SUT wraps RequestId in a <code> element — verify the
        // structure (not just the text) so a CSS-class rename would fail
        // the test and surface the markup change to the maintainer.
        var codeElement = cut.Find("code");
        codeElement.TextContent.Should().Be("trace-XYZ");
    }
}
