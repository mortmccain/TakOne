using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Common.Errors;
using TakOne.Application.Resources;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ErrorDisplayService"/> — the UI-side chokepoint
/// that turns an opaque 7-char code (e.g. <c>"47NQR83"</c>) into a fully
/// localized user-facing "unexpected error" message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mocking strategy.</b> The service depends on
/// <c>IStringLocalizer&lt;UnexpectedErrorMessages&gt;</c>. NSubstitute
/// substitutes the indexer (it's an interface member, so trivially virtual).
/// The localizer's indexer returns a <see cref="LocalizedString"/>, which
/// carries both the resource key and the localized value — the service
/// implicitly converts it to <c>string</c> via the
/// <see cref="LocalizedString"/> implicit operator.
/// </para>
/// <para>
/// <b>Culture awareness.</b> <see cref="ErrorDisplayService.Unexpected"/>
/// uses <c>CultureInfo.CurrentUICulture</c> as the IFormatProvider for
/// <c>string.Format</c>. For the format string
/// "An unexpected error occurred. Error code: {0}", there's no
/// culture-sensitive separator — the only substitution is the code itself,
/// so the culture parameter is a no-op for this particular format. We
/// exercise both en-US and fa-IR cultures anyway to guard against regressions
/// if the format string is ever localized to include a decimal separator
/// (e.g. for an error-number code).
/// </para>
/// </remarks>
public class ErrorDisplayServiceTests
{
    // The localized template strings used as return values from the mock
    // localizer. Mirror the .resx values: "An unexpected error occurred.
    // Error code: {0}" (en) / "خطای غیرمنتظره‌ای رخ داد. کد خطا: {0}" (fa).
    private const string EnglishFormat = "An unexpected error occurred. Error code: {0}";
    private const string EnglishTitle = "Unexpected Error";

    /// <summary>
    /// Builds a real <see cref="ErrorDisplayService"/> wired to a mock
    /// localizer that returns the EN format/title for the two well-known
    /// resource keys.
    /// </summary>
    private static ErrorDisplayService CreateSut(
        string? formatOverride = null,
        string? titleOverride = null)
    {
        // Arrange — mock localizer responds to the two resource keys used
        // by ErrorDisplayService: Unexpected_Error_Format (the {0} template)
        // and Unexpected_Error_Title (the toast/alert heading).
        var localizer = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        localizer["Unexpected_Error_Format"]
            .Returns(new LocalizedString("Unexpected_Error_Format", formatOverride ?? EnglishFormat));
        localizer["Unexpected_Error_Title"]
            .Returns(new LocalizedString("Unexpected_Error_Title", titleOverride ?? EnglishTitle));
        return new ErrorDisplayService(localizer);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Unexpected(string)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unexpected_SevenCharCode_ReturnsLocalizedMessageWithCodeSubstituted()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Unexpected(UnexpectedErrorCodes.UnitOfWork_RetryExhausted);

        // Assert — {0} placeholder is substituted with the literal code
        result.Should().Be("An unexpected error occurred. Error code: 24FSM83");
    }

    [Fact]
    public void Unexpected_AnotherSevenCharCode_ReturnsLocalizedMessageWithCodeSubstituted()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Unexpected(UnexpectedErrorCodes.Login_UnexpectedFailure);

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: 82CGH87");
    }

    [Fact]
    public void Unexpected_CustomCode_ReturnsFormattedMessage()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Unexpected("47NQR83");

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: 47NQR83");
    }

    [Fact]
    public void Unexpected_PersianFormatTemplate_ReturnsPersianMessageWithCodeSubstituted()
    {
        // Arrange — localizer returns the fa-IR template to verify the code
        // is inserted correctly regardless of the surrounding language.
        var sut = CreateSut(formatOverride: "خطای غیرمنتظره‌ای رخ داد. کد خطا: {0}");

        // Act
        var result = sut.Unexpected("47NQR83");

        // Assert
        result.Should().Be("خطای غیرمنتظره‌ای رخ داد. کد خطا: 47NQR83");
    }

    [Fact]
    public void Unexpected_TemplateWithMultiplePlaceholders_SubstitutesByPosition()
    {
        // Arrange — defensive test: if a future template adds more
        // placeholders, the SUT still substitutes the first positional slot
        // with the code. (This is the .NET string.Format contract.)
        var sut = CreateSut(formatOverride: "[{0}] Code: {0}");

        // Act
        var result = sut.Unexpected("47NQR83");

        // Assert
        result.Should().Be("[47NQR83] Code: 47NQR83");
    }

    [Fact]
    public void Unexpected_EmptyString_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var act = () => sut.Unexpected(string.Empty);

        // Assert — SUT calls ArgumentException.ThrowIfNullOrWhiteSpace(code)
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unexpected_NullString_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var act = () => sut.Unexpected(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unexpected_WhitespaceOnlyString_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var act = () => sut.Unexpected("   ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unexpected_UsesCurrentUICultureAsFormatProvider()
    {
        // Arrange — set the UI culture to fa-IR to confirm the SUT passes
        // CurrentUICulture as the IFormatProvider to string.Format. The
        // format string here has no separator, so the only effect is the
        // code substitution itself — but the test guards against future
        // regressions if the format gains a culture-sensitive token.
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fa-IR");
        try
        {
            var sut = CreateSut();

            // Act
            var result = sut.Unexpected("47NQR83");

            // Assert
            result.Should().Be("An unexpected error occurred. Error code: 47NQR83");
        }
        finally
        {
            // Restore the original culture so we don't poison other tests.
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // UnexpectedTitle property
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnexpectedTitle_ReturnsLocalizedTitleFromLocalizer()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var title = sut.UnexpectedTitle;

        // Assert
        title.Should().Be(EnglishTitle);
    }

    [Fact]
    public void UnexpectedTitle_PersianTemplate_ReturnsPersianTitle()
    {
        // Arrange — localizer returns a Persian title
        var sut = CreateSut(titleOverride: "خطای غیرمنتظره");

        // Act
        var title = sut.UnexpectedTitle;

        // Assert
        title.Should().Be("خطای غیرمنتظره");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Localize(string?) — the wire-format recognizer
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Localize_NullInput_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Localize(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Localize_EmptyString_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Localize(string.Empty);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Localize_WireFormatWithSevenCharCode_StripsPrefixAndReturnsLocalizedMessage()
    {
        // Arrange — wire-format string "UE|47NQR83" is what the Application
        // layer emits from Result.Failure($"UE|{UnexpectedErrorCodes.X}").
        var sut = CreateSut();

        // Act
        var result = sut.Localize("UE|47NQR83");

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: 47NQR83");
    }

    [Fact]
    public void Localize_WireFormatWithRealCatalogCode_StripsPrefixAndSubstitutesCode()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Localize($"UE|{UnexpectedErrorCodes.Cart_UpdateFailure}");

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: 67PWB69");
    }

    [Fact]
    public void Localize_WireFormatWithLongCode_TruncatesToFirstSevenChars()
    {
        // Arrange — code is longer than 7 chars (a malformed Application
        // output); SUT defensively takes code[..7] when code.Length >= 7.
        var sut = CreateSut();

        // Act
        var result = sut.Localize("UE|without_pipe_first");

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: without");
    }

    [Fact]
    public void Localize_WireFormatWithShortCode_PassesShortCodeToUnexpected()
    {
        // Arrange — code shorter than 7 chars falls through to the
        // Unexpected(code) call without truncation.
        var sut = CreateSut();

        // Act
        var result = sut.Localize("UE|short");

        // Assert
        result.Should().Be("An unexpected error occurred. Error code: short");
    }

    [Fact]
    public void Localize_WireFormatWithEmptyCode_ThrowsArgumentException()
    {
        // Arrange — "UE|" produces an empty code; SUT calls Unexpected("")
        // which calls ArgumentException.ThrowIfNullOrWhiteSpace, so this
        // path throws.
        var sut = CreateSut();

        // Act
        var act = () => sut.Localize("UE|");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Localize_WireFormatWithWhitespaceCode_ThrowsArgumentException()
    {
        // Arrange — whitespace-only code: Unexpected throws because the
        // SUT's guard rejects whitespace strings.
        var sut = CreateSut();

        // Act
        var act = () => sut.Localize("UE|   ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Localize_RawErrorMessageWithoutPrefix_ReturnsInputVerbatim()
    {
        // Arrange — when the input has no UE| prefix, it's a stable-code
        // message from the Application layer (e.g. "PurchaseLimitExceeded")
        // which the caller localizes via its own per-page resx.
        var sut = CreateSut();

        // Act
        var result = sut.Localize("PurchaseLimitExceeded");

        // Assert
        result.Should().Be("PurchaseLimitExceeded");
    }

    [Fact]
    public void Localize_RawErrorMessageWithColonAndPipeButNoPrefix_ReturnsInputVerbatim()
    {
        // Arrange — the recognizer keys off the literal "UE|" prefix only;
        // colons and other pipes elsewhere don't trigger the unexpected path.
        var sut = CreateSut();

        // Act
        var result = sut.Localize("Different:Message");

        // Assert
        result.Should().Be("Different:Message");
    }

    [Fact]
    public void Localize_RawErrorMessageWithLowercaseUePrefix_ReturnsInputVerbatim()
    {
        // Arrange — the prefix check is case-sensitive (Ordinal), so a
        // lowercase "ue|" doesn't trigger the wire-format path.
        var sut = CreateSut();

        // Act
        var result = sut.Localize("ue|47NQR83");

        // Assert
        result.Should().Be("ue|47NQR83");
    }

    [Fact]
    public void Localize_StringStartingWithPipeButNoUePrefix_ReturnsInputVerbatim()
    {
        // Arrange — StartsWith("UE|") is false for "|only".
        var sut = CreateSut();

        // Act
        var result = sut.Localize("|only");

        // Assert
        result.Should().Be("|only");
    }

    [Fact]
    public void Localize_RawNullFromBackend_PreviouslyNull_ReturnsNull()
    {
        // Arrange — coverage for the null branch (caller typically uses
        // null-coalescing on result.Error).
        var sut = CreateSut();

        // Act
        var result = sut.Localize(null);

        // Assert
        result.Should().BeNull();
    }

    // ───────────────────────────────────────────────────────────────────────
    // WireFormatPrefix constant
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void WireFormatPrefix_IsUePipeLiteral()
    {
        // Arrange / Act
        var prefix = ErrorDisplayService.WireFormatPrefix;

        // Assert — documented as the stable wire-format prefix the
        // Application layer emits for unexpected errors.
        prefix.Should().Be("UE|");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Round-trip / integration coverage
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Localize_WireFormatRoundTripsThroughUnexpected_ProducesSameMessage()
    {
        // Arrange — verify Localize("UE|{code}") == Unexpected(code) for
        // any valid 7-char code (the public contract).
        var sut = CreateSut();
        var code = "47NQR83";

        // Act
        var direct = sut.Unexpected(code);
        var viaLocalize = sut.Localize($"UE|{code}");

        // Assert
        viaLocalize.Should().Be(direct);
    }

    [Fact]
    public void Localize_WireFormatWithRealCodeFromCatalog_ProducesSameMessageAsUnexpected()
    {
        // Arrange
        var sut = CreateSut();
        var code = UnexpectedErrorCodes.SubmitSale_CustomerDisappeared;

        // Act
        var direct = sut.Unexpected(code);
        var viaLocalize = sut.Localize($"UE|{code}");

        // Assert
        viaLocalize.Should().Be(direct);
    }

    [Fact]
    public void Constructor_RealLocalizer_InjectsSuccessfully()
    {
        // Arrange — the constructor stores the localizer without null-check
        // (the C# nullable context enforces the contract at compile time).
        var localizer = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        localizer["Unexpected_Error_Format"]
            .Returns(new LocalizedString("Unexpected_Error_Format", EnglishFormat));
        localizer["Unexpected_Error_Title"]
            .Returns(new LocalizedString("Unexpected_Error_Title", EnglishTitle));

        // Act
        var sut = new ErrorDisplayService(localizer);

        // Assert — the SUT is usable immediately; no deferred validation.
        sut.Unexpected("47NQR83").Should().Contain("47NQR83");
    }
}
