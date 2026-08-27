using FluentAssertions;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Radzen;
using TakOne.Application.Resources;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ToastService"/> — the thin wrapper around
/// Radzen's <see cref="NotificationService"/> that exposes severity-specific
/// helpers plus a dedicated <see cref="ToastService.UnexpectedError"/>
/// helper for catch-block surprises.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not mock NotificationService.</b> Radzen's
/// <see cref="NotificationService"/> is a concrete class whose
/// <see cref="NotificationService.Notify(NotificationMessage)"/> method is
/// non-virtual (verified via reflection against
/// <c>radzen.blazor.11.1.8/lib/net10.0/Radzen.Blazor.dll</c>). NSubstitute
/// cannot intercept non-virtual members on a class substitute — the call
/// would fall through to the base implementation unrecorded, breaking
/// <c>Received()</c> assertions. The workaround is to construct a REAL
/// <see cref="NotificationService"/> and spy on its
/// <see cref="NotificationService.Messages"/> collection (an
/// <c>ObservableCollection&lt;NotificationMessage&gt;</c>) — verified to
/// work outside a Blazor render context.
/// </para>
/// <para>
/// <b>Why not mock ErrorDisplayService.</b> <see cref="ErrorDisplayService"/>
/// is a sealed class — NSubstitute cannot substitute it. We use the REAL
/// ErrorDisplayService wired to a mock
/// <c>IStringLocalizer&lt;UnexpectedErrorMessages&gt;</c> (the localizer's
/// indexer is an interface member, so the substitute works).
/// </para>
/// <para>
/// <b>Default durations.</b> The SUT specifies per-severity default
/// durations: Success=3000ms, Info=4000ms, Warning=5000ms, Error=6000ms,
/// UnexpectedError=7000ms (one second longer than Error to give the user
/// time to copy the visible code).
/// </para>
/// </remarks>
public class ToastServiceTests
{
    private const string EnglishFormat = "An unexpected error occurred. Error code: {0}";
    private const string EnglishTitle = "Unexpected Error";

    /// <summary>
    /// Builds a real <see cref="ToastService"/> wired to a real
    /// <see cref="NotificationService"/> and a real
    /// <see cref="ErrorDisplayService"/> (with a mocked localizer).
    /// </summary>
    private static (ToastService sut, NotificationService radzen, ErrorDisplayService errorDisplay) CreateSut()
    {
        // Arrange — NotificationService is concrete with non-virtual Notify,
        // so we use the real instance and inspect its Messages collection.
        var radzen = new NotificationService();

        var localizer = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        localizer["Unexpected_Error_Format"]
            .Returns(new LocalizedString("Unexpected_Error_Format", EnglishFormat));
        localizer["Unexpected_Error_Title"]
            .Returns(new LocalizedString("Unexpected_Error_Title", EnglishTitle));
        var errorDisplay = new ErrorDisplayService(localizer);

        var sut = new ToastService(radzen, errorDisplay);
        return (sut, radzen, errorDisplay);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Success
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Success_DefaultCall_AddsSuccessToastWithDefaults()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("done");

        // Assert
        radzen.Messages.Should().HaveCount(1);
        var msg = radzen.Messages[0];
        msg.Severity.Should().Be(NotificationSeverity.Success);
        msg.Summary.Should().Be("Success");
        msg.Detail.Should().Be("done");
        msg.Duration.Should().Be(3000);
        msg.CloseOnClick.Should().BeTrue();
    }

    [Fact]
    public async Task Success_CustomSummary_OverridesDefaultSummary()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("done", "Custom");

        // Assert
        radzen.Messages[0].Summary.Should().Be("Custom");
        radzen.Messages[0].Detail.Should().Be("done");
        radzen.Messages[0].Duration.Should().Be(3000);
    }

    [Fact]
    public async Task Success_CustomDuration_OverridesDefaultDuration()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("done", null, 5000);

        // Assert
        radzen.Messages[0].Duration.Should().Be(5000);
        radzen.Messages[0].Summary.Should().Be("Success");
    }

    [Fact]
    public async Task Success_CustomSummaryAndDuration_AppliesBothOverrides()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("done", "Saved", 8000);

        // Assert
        radzen.Messages[0].Summary.Should().Be("Saved");
        radzen.Messages[0].Duration.Should().Be(8000);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Info
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Info_DefaultCall_AddsInfoToastWithDefaults()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Info("hello");

        // Assert
        radzen.Messages.Should().HaveCount(1);
        var msg = radzen.Messages[0];
        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Summary.Should().Be("Info");
        msg.Detail.Should().Be("hello");
        msg.Duration.Should().Be(4000);
        msg.CloseOnClick.Should().BeTrue();
    }

    [Fact]
    public async Task Info_CustomSummary_OverridesDefaultSummary()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Info("hello", "Title");

        // Assert
        radzen.Messages[0].Summary.Should().Be("Title");
        radzen.Messages[0].Severity.Should().Be(NotificationSeverity.Info);
        radzen.Messages[0].Duration.Should().Be(4000);
    }

    [Fact]
    public async Task Info_CustomDuration_OverridesDefaultDuration()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Info("hello", null, 9000);

        // Assert
        radzen.Messages[0].Duration.Should().Be(9000);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Warning
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Warning_DefaultCall_AddsWarningToastWithDefaults()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Warning("careful");

        // Assert
        radzen.Messages.Should().HaveCount(1);
        var msg = radzen.Messages[0];
        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Summary.Should().Be("Warning");
        msg.Detail.Should().Be("careful");
        msg.Duration.Should().Be(5000);
        msg.CloseOnClick.Should().BeTrue();
    }

    [Fact]
    public async Task Warning_CustomSummaryAndDuration_AppliesBothOverrides()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Warning("careful", "Custom", 8000);

        // Assert
        radzen.Messages[0].Summary.Should().Be("Custom");
        radzen.Messages[0].Duration.Should().Be(8000);
        radzen.Messages[0].Severity.Should().Be(NotificationSeverity.Warning);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Error
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Error_DefaultCall_AddsErrorToastWithDefaults()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Error("bad");

        // Assert
        radzen.Messages.Should().HaveCount(1);
        var msg = radzen.Messages[0];
        msg.Severity.Should().Be(NotificationSeverity.Error);
        msg.Summary.Should().Be("Error");
        msg.Detail.Should().Be("bad");
        msg.Duration.Should().Be(6000);
        msg.CloseOnClick.Should().BeTrue();
    }

    [Fact]
    public async Task Error_CustomSummary_OverridesDefaultSummary()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Error("bad", "Custom");

        // Assert
        radzen.Messages[0].Summary.Should().Be("Custom");
        radzen.Messages[0].Severity.Should().Be(NotificationSeverity.Error);
        radzen.Messages[0].Duration.Should().Be(6000);
    }

    [Fact]
    public async Task Error_CustomDuration_OverridesDefaultDuration()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Error("bad", null, 10000);

        // Assert
        radzen.Messages[0].Duration.Should().Be(10000);
    }

    // ───────────────────────────────────────────────────────────────────────
    // UnexpectedError
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnexpectedError_DefaultCall_UsesErrorDisplayForDetailAndTitle()
    {
        // Arrange
        var (sut, radzen, errorDisplay) = CreateSut();
        var code = "47NQR83";

        // Act
        await sut.UnexpectedError(code);

        // Assert
        radzen.Messages.Should().HaveCount(1);
        var msg = radzen.Messages[0];
        msg.Severity.Should().Be(NotificationSeverity.Error);
        msg.Summary.Should().Be(errorDisplay.UnexpectedTitle);
        msg.Detail.Should().Be(errorDisplay.Unexpected(code));
        msg.Duration.Should().Be(7000);
        msg.CloseOnClick.Should().BeTrue();
    }

    [Fact]
    public async Task UnexpectedError_ExplicitDetail_MatchesLocalizedMessage()
    {
        // Arrange
        var (sut, radzen, errorDisplay) = CreateSut();
        var code = "47NQR83";

        // Act
        await sut.UnexpectedError(code);

        // Assert — the detail is the fully-localized message with the code
        // substituted into the format string.
        radzen.Messages[0].Detail.Should().Be("An unexpected error occurred. Error code: 47NQR83");
        radzen.Messages[0].Detail.Should().Be(errorDisplay.Unexpected(code));
    }

    [Fact]
    public async Task UnexpectedError_CustomSummary_OverridesLocalizedTitle()
    {
        // Arrange
        var (sut, radzen, errorDisplay) = CreateSut();

        // Act
        await sut.UnexpectedError("47NQR83", "Custom Summary");

        // Assert
        radzen.Messages[0].Summary.Should().Be("Custom Summary");
        radzen.Messages[0].Summary.Should().NotBe(errorDisplay.UnexpectedTitle);
        radzen.Messages[0].Duration.Should().Be(7000);
    }

    [Fact]
    public async Task UnexpectedError_CustomDuration_OverridesDefaultDuration()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.UnexpectedError("47NQR83", null, 8000);

        // Assert
        radzen.Messages[0].Duration.Should().Be(8000);
    }

    [Fact]
    public async Task UnexpectedError_RealCatalogCode_FormatsCodeIntoDetail()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act — uses the Login_UnexpectedFailure code from the catalog
        await sut.UnexpectedError(TakOne.Application.Common.Errors.UnexpectedErrorCodes.Login_UnexpectedFailure);

        // Assert
        radzen.Messages[0].Detail.Should().Be("An unexpected error occurred. Error code: 82CGH87");
    }

    [Fact]
    public async Task UnexpectedError_SeverityIsError()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.UnexpectedError("47NQR83");

        // Assert — UnexpectedError surfaces as Error severity (NOT Warning),
        // since the user needs to acknowledge that the operation failed.
        radzen.Messages[0].Severity.Should().Be(NotificationSeverity.Error);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Cross-cutting behavior
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoInvocation_NotificationServiceMessagesIsEmpty()
    {
        // Arrange — verify the baseline: a fresh NotificationService has no
        // messages, so any test that asserts on radzen.Messages after an
        // invocation can rely on this precondition.
        var radzen = new NotificationService();

        // Act — no call to the SUT

        // Assert
        radzen.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleInvocations_AddsOneMessagePerCall()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("one");
        await sut.Info("two");
        await sut.Warning("three");
        await sut.Error("four");
        await sut.UnexpectedError("47NQR83");

        // Assert — five calls produce five messages, in order.
        radzen.Messages.Should().HaveCount(5);
        radzen.Messages[0].Severity.Should().Be(NotificationSeverity.Success);
        radzen.Messages[1].Severity.Should().Be(NotificationSeverity.Info);
        radzen.Messages[2].Severity.Should().Be(NotificationSeverity.Warning);
        radzen.Messages[3].Severity.Should().Be(NotificationSeverity.Error);
        radzen.Messages[4].Severity.Should().Be(NotificationSeverity.Error);
    }

    [Fact]
    public async Task EachToast_SetsCloseOnClickToTrue()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("a");
        await sut.Info("b");
        await sut.Warning("c");
        await sut.Error("d");
        await sut.UnexpectedError("47NQR83");

        // Assert — every severity helper sets CloseOnClick=true so the user
        // can dismiss by clicking the toast body.
        radzen.Messages.Should().AllSatisfy(m => m.CloseOnClick.Should().BeTrue());
    }

    [Fact]
    public async Task EachToast_SetsDetailToMessageArgument()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("msg-success");
        await sut.Info("msg-info");
        await sut.Warning("msg-warn");
        await sut.Error("msg-error");

        // Assert — the message argument is forwarded as Detail for each
        // severity (UnexpectedError is excluded here because its detail is
        // the localized code message, not the raw argument).
        radzen.Messages[0].Detail.Should().Be("msg-success");
        radzen.Messages[1].Detail.Should().Be("msg-info");
        radzen.Messages[2].Detail.Should().Be("msg-warn");
        radzen.Messages[3].Detail.Should().Be("msg-error");
    }

    [Fact]
    public async Task EachSeverity_DefaultDurationsMatchSutContract()
    {
        // Arrange
        var (sut, radzen, _) = CreateSut();

        // Act
        await sut.Success("a");
        await sut.Info("b");
        await sut.Warning("c");
        await sut.Error("d");
        await sut.UnexpectedError("47NQR83");

        // Assert — Success=3000, Info=4000, Warning=5000, Error=6000,
        // UnexpectedError=7000. The unexpected path stays one second longer
        // so the user has time to copy the visible code.
        radzen.Messages[0].Duration.Should().Be(3000);
        radzen.Messages[1].Duration.Should().Be(4000);
        radzen.Messages[2].Duration.Should().Be(5000);
        radzen.Messages[3].Duration.Should().Be(6000);
        radzen.Messages[4].Duration.Should().Be(7000);
    }

    [Fact]
    public async Task EachSeverity_DefaultSummariesMatchSeverityName()
    {
        // Arrange
        var (sut, radzen, errorDisplay) = CreateSut();

        // Act
        await sut.Success("a");
        await sut.Info("b");
        await sut.Warning("c");
        await sut.Error("d");
        await sut.UnexpectedError("47NQR83");

        // Assert — Success→"Success", Info→"Info", Warning→"Warning",
        // Error→"Error"; UnexpectedError→ the localized unexpected title.
        radzen.Messages[0].Summary.Should().Be("Success");
        radzen.Messages[1].Summary.Should().Be("Info");
        radzen.Messages[2].Summary.Should().Be("Warning");
        radzen.Messages[3].Summary.Should().Be("Error");
        radzen.Messages[4].Summary.Should().Be(errorDisplay.UnexpectedTitle);
    }
}
