using System.Globalization;
using FluentAssertions;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CultureFormat"/> — the centralized culture-aware
/// formatter for ALL numeric, monetary, date, and relative-time display in
/// the TakOne WebUI.
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A static class that formats values to ASCII digits
/// (en-US mode) or Persian digits (fa-IR mode) based on
/// <c>CultureInfo.CurrentUICulture.TwoLetterISOLanguageName</c>. Punctuation
/// (period, comma) is NOT swapped — only the digit characters 0-9 ↔ ۰-۹.
/// Persian calendar is invoked explicitly for date formats (the .NET fa-IR
/// culture's default DateTimeFormat.Calendar is Gregorian — see SUT doc).
/// </para>
/// <para>
/// <b>Culture setup pattern.</b> Every culture-dependent test uses the
/// <see cref="WithCulture"/> helper to set BOTH CurrentCulture (used by
/// <c>date.ToString("g", CultureInfo.CurrentCulture)</c> for English day /
/// month names) AND CurrentUICulture (used by IsPersian and ToCultureDigits)
/// in a try/finally block that restores the original cultures. This is the
/// MANDATORY pattern — never leave the culture modified for other tests.
/// </para>
/// <para>
/// <b>Persian-digit codepoints.</b> The SUT swaps ASCII 0-9 (U+0030–0039)
/// to Persian digits U+06F0–U+06F9 (EXTENDED Arabic-Indic, the Persian
/// form), NOT to the Arabic-Indic range U+0660–U+0669 (which uses a
/// slightly different glyph for 4/5/6).
/// </para>
/// </remarks>
public class CultureFormatTests
{
    // Persian digit literals — U+06F0..U+06F9 (verified by the test fixture
    // itself; see ToCultureDigits_AsciiDigitsUnderFaIr_ReturnsPersianDigits
    // which asserts the literal codepoints).
    private const char PersianZero = '۰';   // U+06F0
    private const char PersianOne = '۱';    // U+06F1
    private const char PersianTwo = '۲';     // U+06F2
    private const char PersianThree = '۳';  // U+06F3
    private const char PersianFour = '۴';   // U+06F4
    private const char PersianFive = '۵';   // U+06F5
    private const char PersianSix = '۶';     // U+06F6
    private const char PersianSeven = '۷';   // U+06F7
    private const char PersianEight = '۸';  // U+06F8
    private const char PersianNine = '۹';   // U+06F9

    /// <summary>
    /// U+202F — NARROW NO-BREAK SPACE. The en-US culture's
    /// ShortTimePattern uses this invisible character before the AM/PM
    /// designator (verified by reflection against
    /// <c>CultureInfo.GetCultureInfo("en-US").DateTimeFormat.ShortTimePattern</c>).
    /// The SUT calls <c>value.ToString("g", CultureInfo.CurrentCulture)</c>
    /// so its output also uses U+202F; tests must use this constant instead
    /// of a regular ASCII space (U+0020) when asserting against the full
    /// en-US formatted string.
    /// </summary>
    private const char NarrowNoBreakSpace = '\u202F';

    /// <summary>
    /// Temporarily sets BOTH <see cref="CultureInfo.CurrentCulture"/> and
    /// <see cref="CultureInfo.CurrentUICulture"/> to the specified culture,
    /// runs the action, then restores the originals in the finally block.
    /// NEVER inline culture mutations without this helper — leaving a test
    /// culture modified would poison every subsequent test.
    /// </summary>
    private static void WithCulture(string name, Action action)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var target = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentCulture = target;
        CultureInfo.CurrentUICulture = target;
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ToCultureDigits — the core digit-swap primitive
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ToCultureDigits_Null_ReturnsEmpty()
    {
        // Arrange / Act
        var result = CultureFormat.ToCultureDigits(null);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToCultureDigits_EmptyString_ReturnsEmpty()
    {
        // Arrange / Act
        var result = CultureFormat.ToCultureDigits(string.Empty);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToCultureDigits_AsciiDigitsUnderEnUs_ReturnsAsciiDigits()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.ToCultureDigits("0123456789");

            // Assert — en-US keeps ASCII digits
            result.Should().Be("0123456789");
        });
    }

    [Fact]
    public void ToCultureDigits_AsciiDigitsUnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.ToCultureDigits("0123456789");

            // Assert — ASCII digits are swapped to Persian U+06F0..U+06F9
            result.Should().Be($"{PersianZero}{PersianOne}{PersianTwo}{PersianThree}{PersianFour}{PersianFive}{PersianSix}{PersianSeven}{PersianEight}{PersianNine}");
            // Sanity-check the literal codepoints to catch any future
            // encoder change in the SUT (e.g. swap to Arabic-Indic U+0660).
            result[0].Should().Be((char)0x06F0);
            result[9].Should().Be((char)0x06F9);
        });
    }

    [Fact]
    public void ToCultureDigits_PersianDigitsUnderEnUs_ReturnsAsciiDigits()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — Persian digits stored in DB (e.g. SaleNumber)
            var persian = $"{PersianOne}{PersianFour}{PersianZero}{PersianFive}";

            // Act
            var result = CultureFormat.ToCultureDigits(persian);

            // Assert — Persian digits convert back to ASCII for English UI
            result.Should().Be("1405");
        });
    }

    [Fact]
    public void ToCultureDigits_PersianDigitsUnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange
            var persian = $"{PersianOne}{PersianFour}{PersianZero}{PersianFive}";

            // Act
            var result = CultureFormat.ToCultureDigits(persian);

            // Assert — Persian digits stay Persian
            result.Should().Be(persian);
        });
    }

    [Fact]
    public void ToCultureDigits_MixedLettersAndDigits_PersianMode_PreservesLettersSwapsDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — "INT-۱۴۰۵-۰۰۴۲" would be a SaleNumber stored in
            // Persian digits (the SUT doc cites this as the canonical case).
            var mixed = $"INT-1405-0042";

            // Act
            var result = CultureFormat.ToCultureDigits(mixed);

            // Assert — letters and the hyphen pass through; ASCII digits swap
            result.Should().Be($"INT-{PersianOne}{PersianFour}{PersianZero}{PersianFive}-{PersianZero}{PersianZero}{PersianFour}{PersianTwo}");
        });
    }

    [Fact]
    public void ToCultureDigits_MixedLettersAndDigits_EnglishMode_PreservesLettersSwapsDigits()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var mixed = $"INT-{PersianOne}{PersianFour}{PersianZero}{PersianFive}-{PersianZero}{PersianZero}{PersianFour}{PersianTwo}";

            // Act
            var result = CultureFormat.ToCultureDigits(mixed);

            // Assert — Persian digits convert back to ASCII for English UI
            result.Should().Be("INT-1405-0042");
        });
    }

    [Fact]
    public void ToCultureDigits_PeriodAndCommaAreNotSwapped()
    {
        // Document the SUT's digit-only swap contract: punctuation chars
        // (period '.', comma ',') pass through unchanged in BOTH cultures.
        // This means Persian-mode output looks like "1.5" (ASCII period) —
        // the SUT's own docstring claims "Persian decimal separator (٫,
        // U+066B)" but the implementation does NOT actually swap punctuation.
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.ToCultureDigits("1,234.56");

            // Assert — ALL digit chars swap to Persian; period and comma
            // stay ASCII (the digit-only-swap contract).
            result.Should().Be($"{PersianOne},{PersianTwo}{PersianThree}{PersianFour}.{PersianFive}{PersianSix}");
            // Confirm the punctuation chars specifically are ASCII
            result.Should().Contain(",");
            result.Should().Contain(".");
            result.Should().NotContain("٫"); // Persian decimal sep U+066B
            result.Should().NotContain("٬"); // Persian thousands sep U+066C
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // IsPersian
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsPersian_UnderEnUs_ReturnsFalse()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.IsPersian;

            // Assert
            result.Should().BeFalse();
        });
    }

    [Fact]
    public void IsPersian_UnderFaIr_ReturnsTrue()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.IsPersian;

            // Assert
            result.Should().BeTrue();
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatDigits(int)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatDigits_Int_UnderEnUs_ReturnsAsciiDigits()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(12345);

            // Assert — int.ToString("0", Invariant) = "12345" (no separators)
            result.Should().Be("12345");
        });
    }

    [Fact]
    public void FormatDigits_Int_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(12345);

            // Assert — Persian digits, no separators (the SUT uses "0" not "N0")
            result.Should().Be($"{PersianOne}{PersianTwo}{PersianThree}{PersianFour}{PersianFive}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatDigits(long)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatDigits_Long_UnderEnUs_ReturnsAsciiDigits()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(12345678L);

            // Assert
            result.Should().Be("12345678");
        });
    }

    [Fact]
    public void FormatDigits_Long_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(12345678L);

            // Assert
            result.Should().Be($"{PersianOne}{PersianTwo}{PersianThree}{PersianFour}{PersianFive}{PersianSix}{PersianSeven}{PersianEight}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatDigits(decimal, fmt)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatDigits_DecimalWithFmt_UnderEnUs_ReturnsFormattedAscii()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(1.5m, "0.0");

            // Assert — ASCII digits, period decimal separator
            result.Should().Be("1.5");
        });
    }

    [Fact]
    public void FormatDigits_DecimalWithFmt_UnderFaIr_ReturnsPersianDigitsWithAsciiPeriod()
    {
        // NOTE: The SUT's docstring claims Persian decimal separator (٫,
        // U+066B), but the implementation only swaps DIGIT chars via
        // ToCultureDigits — the period stays ASCII. This test documents
        // the actual behavior (digit-only swap, period unchanged).
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(1.5m, "0.0");

            // Assert — Persian digits + ASCII period (the period is NOT
            // swapped; only digit chars 0-9 → ۰-۹).
            result.Should().Be($"{PersianOne}.{PersianFive}");
        });
    }

    [Fact]
    public void FormatDigits_DecimalWithMultipleDecimals_PreservesPrecision()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(3.14159m, "0.00000");

            // Assert
            result.Should().Be($"{PersianThree}.{PersianOne}{PersianFour}{PersianOne}{PersianFive}{PersianNine}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatDigits(string?)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatDigits_String_Null_ReturnsEmpty()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits((string?)null);

            // Assert
            result.Should().BeEmpty();
        });
    }

    [Fact]
    public void FormatDigits_String_Empty_ReturnsEmpty()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits(string.Empty);

            // Assert
            result.Should().BeEmpty();
        });
    }

    [Fact]
    public void FormatDigits_String_NumericStringWithDecimal_PreservesPrecision_UnderEnUs()
    {
        WithCulture("en-US", () =>
        {
            // Act — the SUT parses with NumberStyles.Any + InvariantCulture,
            // detects the decimal count, and rebuilds the format string.
            var result = CultureFormat.FormatDigits("1.50");

            // Assert
            result.Should().Be("1.50");
        });
    }

    [Fact]
    public void FormatDigits_String_NumericStringWithDecimal_PreservesPrecision_UnderFaIr()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatDigits("1.50");

            // Assert — Persian digits (all swap), ASCII period (digit-only)
            result.Should().Be($"{PersianOne}.{PersianFive}{PersianZero}");
        });
    }

    [Fact]
    public void FormatDigits_String_NonNumeric_PassesThroughWithDigitSwap()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — a SaleNumber-like string with Persian digits stored
            // in the DB; the SUT's TryParse fails and falls back to ToCultureDigits.
            var nonNumeric = $"INT-{PersianOne}{PersianFour}{PersianZero}{PersianFive}";

            // Act
            var result = CultureFormat.FormatDigits(nonNumeric);

            // Assert — Persian digits stay Persian (no parse path); letters
            // and the hyphen pass through.
            result.Should().Be(nonNumeric);
        });
    }

    [Fact]
    public void FormatDigits_String_NonNumeric_PersianDigitsConvertedToAscii_UnderEnUs()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var nonNumeric = $"INT-{PersianOne}{PersianFour}{PersianZero}{PersianFive}";

            // Act
            var result = CultureFormat.FormatDigits(nonNumeric);

            // Assert — Persian digits converted back to ASCII for en-US UI
            result.Should().Be("INT-1405");
        });
    }

    [Fact]
    public void FormatDigits_String_PureIntegerString_ParsesAsNumber()
    {
        WithCulture("fa-IR", () =>
        {
            // Act — pure-integer string parses cleanly; decimals=0, fmt="0"
            var result = CultureFormat.FormatDigits("12345");

            // Assert
            result.Should().Be($"{PersianOne}{PersianTwo}{PersianThree}{PersianFour}{PersianFive}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatNumber(int)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatNumber_Int_UnderEnUs_ReturnsAsciiDigitsAndComma()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(12345);

            // Assert — N0 with InvariantCulture gives "12,345" (ASCII comma)
            result.Should().Be("12,345");
        });
    }

    [Fact]
    public void FormatNumber_Int_UnderFaIr_ReturnsPersianDigitsAndAsciiComma()
    {
        // NOTE: The SUT's docstring claims Persian thousands separator (٬,
        // U+066C), but the implementation only swaps DIGIT chars via
        // ToCultureDigits — the comma stays ASCII. This test documents
        // the actual behavior (digit-only swap, comma unchanged).
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(12345);

            // Assert — Persian digits + ASCII comma (the comma is NOT
            // swapped; only digit chars 0-9 → ۰-۹).
            result.Should().Be($"{PersianOne}{PersianTwo},{PersianThree}{PersianFour}{PersianFive}");
        });
    }

    [Fact]
    public void FormatNumber_Int_LargeNumber_UsesThousandsSeparators()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(1_000_000);

            // Assert — N0 places a comma every three digits
            result.Should().Be("1,000,000");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatNumber(long)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatNumber_Long_SameBehaviorAsInt()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(12345L);

            // Assert — long overload delegates to the same N0 path
            result.Should().Be("12,345");
        });
    }

    [Fact]
    public void FormatNumber_Long_UnderFaIr_PersianDigitsAndAsciiComma()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(12345L);

            // Assert
            result.Should().Be($"{PersianOne}{PersianTwo},{PersianThree}{PersianFour}{PersianFive}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatNumber(decimal)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatNumber_Decimal_RoundsToZeroDecimalsBeforeFormatting()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatNumber(12345.6m);

            // Assert — Math.Round(12345.6, 0) = 12346, then N0 = "12,346"
            result.Should().Be("12,346");
        });
    }

    [Fact]
    public void FormatNumber_Decimal_RoundsDownOnTie()
    {
        // Document the rounding semantics: .NET's Math.Round uses
        // MidpointRounding.ToEven (banker's rounding) by default. 12344.5
        // rounds to 12344 (even); 12345.5 rounds to 12346 (even).
        WithCulture("en-US", () =>
        {
            // Act
            var r1 = CultureFormat.FormatNumber(12344.5m);
            var r2 = CultureFormat.FormatNumber(12345.5m);

            // Assert — banker's rounding
            r1.Should().Be("12,344");
            r2.Should().Be("12,346");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatPercent(decimal, int)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatPercent_DefaultDecimals_UnderEnUs_ReturnsAsciiPercent()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatPercent(50m);

            // Assert — default decimals=1; ASCII % suffix
            result.Should().Be("50.0%");
        });
    }

    [Fact]
    public void FormatPercent_DefaultDecimals_UnderFaIr_ReturnsPersianDigitsAndArabicPercent()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatPercent(50m);

            // Assert — Persian digits (digits swap), ASCII period (not
            // swapped), Arabic percent symbol U+066A (٪) appended by the
            // IsPersian conditional.
            result.Should().Be($"{PersianFive}{PersianZero}.{PersianZero}٪");
        });
    }

    [Fact]
    public void FormatPercent_CustomDecimals_UsesDecimalsInFormat()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatPercent(33.33m, 2);

            // Assert — two decimal places, ASCII percent
            result.Should().Be("33.33%");
        });
    }

    [Fact]
    public void FormatPercent_ZeroDecimals_DropsFractionalPart()
    {
        // NOTE: decimal.ToString("0", Invariant) uses
        // MidpointRounding.AwayFromZero for fixed-point format strings —
        // 50.5 rounds to 51, NOT 50. This is a different rounding rule than
        // Math.Round which uses banker's rounding. To avoid the rounding
        // ambiguity, we use 50.4 (which rounds down unambiguously).
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatPercent(50.4m, 0);

            // Assert — fmt="0" → "50"
            result.Should().Be("50%");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatMoneyToman(MoneyDto?)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatMoneyToman_Null_ReturnsDash()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatMoneyToman(null);

            // Assert
            result.Should().Be("—");
        });
    }

    [Fact]
    public void FormatMoneyToman_ZeroAmount_ReturnsDash()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — zero-amount MoneyDto collapses to "—" (price-tag
            // convention: an empty price tag is cleaner than "0 تومان")
            var money = new MoneyDto { Amount = 0m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyToman(money);

            // Assert
            result.Should().Be("—");
        });
    }

    [Fact]
    public void FormatMoneyToman_IrrAmount_DividesBy10AndLabelsToman_UnderEnUs()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 10000m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyToman(money);

            // Assert — 10000 / 10 = 1000; N0 = "1,000"; suffix = " تومان"
            result.Should().Be("1,000 تومان");
        });
    }

    [Fact]
    public void FormatMoneyToman_IrrAmount_DividesBy10AndLabelsToman_UnderFaIr()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 10000m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyToman(money);

            // Assert — 10000/10=1000; N0="1,000"; ALL digits swap to Persian;
            // ASCII comma stays ASCII (digit-only swap); suffix is Persian
            // "تومان".
            result.Should().Be($"{PersianOne},{PersianZero}{PersianZero}{PersianZero} تومان");
        });
    }

    [Fact]
    public void FormatMoneyToman_UsdAmount_NoDivisionLabelsCurrency()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 100m, Currency = "USD" };

            // Act
            var result = CultureFormat.FormatMoneyToman(money);

            // Assert — no division; suffix is the currency code itself
            result.Should().Be("100 USD");
        });
    }

    [Fact]
    public void FormatMoneyToman_IrrAmount_LargeAmount_FormatsThousands()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 1,000,000 IRR = 100,000 toman
            var money = new MoneyDto { Amount = 1_000_000m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyToman(money);

            // Assert
            result.Should().Be("100,000 تومان");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatMoneyRial(MoneyDto?)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatMoneyRial_Null_ReturnsDash()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatMoneyRial(null);

            // Assert
            result.Should().Be("—");
        });
    }

    [Fact]
    public void FormatMoneyRial_IrrAmount_NoDivisionLabelsRial()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 10000m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyRial(money);

            // Assert — no division; suffix is "ریال"
            result.Should().Be("10,000 ریال");
        });
    }

    [Fact]
    public void FormatMoneyRial_ZeroAmount_ReturnsZeroRial()
    {
        // NOTE: FormatMoneyRial does NOT collapse zero amounts to "—" — admin
        // pages and order details need to show "0 ریال" so the column aligns.
        // This is the key behavioral difference vs FormatMoneyToman.
        WithCulture("en-US", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 0m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyRial(money);

            // Assert
            result.Should().Be("0 ریال");
        });
    }

    [Fact]
    public void FormatMoneyRial_ZeroAmount_UnderFaIr_ReturnsPersianZeroRial()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 0m, Currency = "IRR" };

            // Act
            var result = CultureFormat.FormatMoneyRial(money);

            // Assert — Persian zero + " ریال" suffix
            result.Should().Be($"{PersianZero} ریال");
        });
    }

    [Fact]
    public void FormatMoneyRial_UsdAmount_NoDivisionLabelsCurrency()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var money = new MoneyDto { Amount = 100m, Currency = "USD" };

            // Act
            var result = CultureFormat.FormatMoneyRial(money);

            // Assert
            result.Should().Be("100 USD");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatAmount(decimal)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatAmount_Decimal_UnderEnUs_ReturnsAsciiDigitsAndComma()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmount(12345m);

            // Assert
            result.Should().Be("12,345");
        });
    }

    [Fact]
    public void FormatAmount_Decimal_UnderFaIr_ReturnsPersianDigitsAndAsciiComma()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatAmount(12345m);

            // Assert — Persian digits + ASCII comma
            result.Should().Be($"{PersianOne}{PersianTwo},{PersianThree}{PersianFour}{PersianFive}");
        });
    }

    [Fact]
    public void FormatAmount_Decimal_RoundsBeforeFormatting()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmount(12345.6m);

            // Assert — rounds to 12346, then N0 = "12,346"
            result.Should().Be("12,346");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatAmountShort(decimal)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatAmountShort_LessThanThousand_ReturnsRawNumber()
    {
        WithCulture("en-US", () =>
        {
            // Act — falls through both thresholds; rounds to 0 decimals + N0
            var result = CultureFormat.FormatAmountShort(999m);

            // Assert — N0 of 999 is "999"
            result.Should().Be("999");
        });
    }

    [Fact]
    public void FormatAmountShort_ExactlyThousand_ReturnsKSuffix()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(1000m);

            // Assert — 1000/1000 = 1.0, fmt "0.0" → "1.0K"
            result.Should().Be("1.0K");
        });
    }

    [Fact]
    public void FormatAmountShort_ThousandFiveHundred_ReturnsOnePointFiveKSuffix()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(1500m);

            // Assert — 1500/1000 = 1.5
            result.Should().Be("1.5K");
        });
    }

    [Fact]
    public void FormatAmountShort_LessThanMillion_ReturnsKSuffixUnderFaIr()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(1500m);

            // Assert — Persian digits + ASCII period + ASCII "K"
            result.Should().Be($"{PersianOne}.{PersianFive}K");
        });
    }

    [Fact]
    public void FormatAmountShort_ExactlyOneMillion_ReturnsMSuffix()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(1_000_000m);

            // Assert — 1M/1M = 1.0
            result.Should().Be("1.0M");
        });
    }

    [Fact]
    public void FormatAmountShort_TwoPointFourMillion_ReturnsPointFourMSuffix()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(2_400_000m);

            // Assert — 2.4M
            result.Should().Be("2.4M");
        });
    }

    [Fact]
    public void FormatAmountShort_TwoPointFourMillion_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Act
            var result = CultureFormat.FormatAmountShort(2_400_000m);

            // Assert — Persian digits + ASCII period + ASCII "M"
            result.Should().Be($"{PersianTwo}.{PersianFour}M");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatDate(DateTime)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatDate_UnderEnUs_ReturnsGregorianDateWithDayName()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 2024-08-15 was a Thursday
            var date = new DateTime(2024, 8, 15);

            // Act
            var result = CultureFormat.FormatDate(date);

            // Assert — "dddd | yyyy/MM/dd" with en-US culture
            result.Should().Be("Thursday | 2024/08/15");
        });
    }

    [Fact]
    public void FormatDate_UnderFaIr_ReturnsJalaliDateWithPersianDigitsAndDayName()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — 2024-08-15 Gregorian → 1403-05-25 Jalali (Mordad 25,
            // a Thursday = "پنجشنبه" in Persian).
            var date = new DateTime(2024, 8, 15);

            // Act
            var result = CultureFormat.FormatDate(date);

            // Assert — Persian day name | Persian digits (4-digit year, 2-digit
            // month, 2-digit day). Day name comes from fa-IR culture's
            // DateTimeFormat.DayNames array (Thursday = "پنجشنبه").
            result.Should().Be($"پنجشنبه | {PersianOne}{PersianFour}۰۳/۰۵/۲۵");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatTehranDateTime(DateTimeOffset)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatTehranDateTime_DateTimeOffset_ConvertsToTehranTimeUnderEnUs()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — UTC 10:00 → Tehran +3:30 → 13:30 local
            var utc = new DateTimeOffset(2024, 8, 15, 10, 0, 0, TimeSpan.Zero);

            // Act
            var result = CultureFormat.FormatTehranDateTime(utc);

            // Assert — "g" format with en-US: short date + short time.
            // The en-US ShortTimePattern uses U+202F (NARROW NO-BREAK SPACE)
            // before the AM/PM designator (invisible in normal rendering but
            // distinct from a regular space U+0020).
            result.Should().Be($"8/15/2024 1:30{NarrowNoBreakSpace}PM");
        });
    }

    [Fact]
    public void FormatTehranDateTime_DateTimeOffset_ConvertsToTehranTimeUnderFaIr()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — UTC 10:00 → Tehran 13:30 (1:30 PM)
            var utc = new DateTimeOffset(2024, 8, 15, 10, 0, 0, TimeSpan.Zero);

            // Act
            var result = CultureFormat.FormatTehranDateTime(utc);

            // Assert — Persian digits + ASCII slash + ASCII space + ASCII
            // dash + ASCII colon. Format: "yyyy/MM/dd - HH:mm" in Jalali.
            result.Should().Be($"{PersianOne}{PersianFour}۰۳/۰۵/۲۵ - ۱۳:۳۰");
        });
    }

    [Fact]
    public void FormatTehranDateTime_DateTimeOffset_PreservesTehranOffsetAddition()
    {
        // Confirm the SUT's +3:30 hour-shift logic: a 09:00 UTC input
        // should produce 12:30 Tehran time (9 + 3:30 = 12:30).
        WithCulture("en-US", () =>
        {
            // Arrange
            var utc = new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero);

            // Act
            var result = CultureFormat.FormatTehranDateTime(utc);

            // Assert — 9 + 3:30 = 12:30. The time substring is "12:30".
            // (Don't assert the full "12:30 PM" — the en-US culture's
            // ShortTimePattern uses U+202F before "PM", which makes the
            // substring "12:30" the safer assertion.)
            result.Should().Contain("12:30");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatTehranDateTime(DateTime) — the convenience overload
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatTehranDateTime_DateTime_WrapsToDateTimeOffsetAndConverts()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — a DateTime in UTC (Kind=Utc) is the canonical input
            var utc = new DateTime(2024, 8, 15, 10, 0, 0, DateTimeKind.Utc);

            // Act
            var result = CultureFormat.FormatTehranDateTime(utc);

            // Assert — same Tehran-shifted output as the DateTimeOffset
            // overload (the SUT wraps it via `new DateTimeOffset(utc, Zero)`).
            // The en-US ShortTimePattern uses U+202F before "PM".
            result.Should().Be($"8/15/2024 1:30{NarrowNoBreakSpace}PM");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatLocalDateTime(DateTime)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatLocalDateTime_UnderEnUs_ReturnsLocalTime()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — already-local DateTime (no UTC→Tehran shift)
            var local = new DateTime(2024, 8, 15, 13, 30, 0);

            // Act
            var result = CultureFormat.FormatLocalDateTime(local);

            // Assert — "g" format with en-US uses U+202F (NARROW NO-BREAK
            // SPACE) before the AM/PM designator (the en-US culture's
            // ShortTimePattern is "h:mm\u202Ftt"; the U+202F is invisible
            // in normal rendering but distinct from U+0020 regular space).
            result.Should().Be($"8/15/2024 1:30{NarrowNoBreakSpace}PM");
        });
    }

    [Fact]
    public void FormatLocalDateTime_UnderFaIr_ReturnsJalaliDateTimeWithPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange
            var local = new DateTime(2024, 8, 15, 13, 30, 0);

            // Act
            var result = CultureFormat.FormatLocalDateTime(local);

            // Assert — Persian digits + ASCII separators (same format as
            // FormatTehranDateTime in fa-IR mode, but no UTC shift applied).
            result.Should().Be($"{PersianOne}{PersianFour}۰۳/۰۵/۲۵ - ۱۳:۳۰");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatMonthDay(DateTime)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatMonthDay_UnderEnUs_ReturnsMonthAbbrAndDay()
    {
        WithCulture("en-US", () =>
        {
            // Arrange
            var date = new DateTime(2024, 8, 15);

            // Act
            var result = CultureFormat.FormatMonthDay(date);

            // Assert — "MMM d" format → "Aug 15"
            result.Should().Be("Aug 15");
        });
    }

    [Fact]
    public void FormatMonthDay_UnderFaIr_ReturnsPersianDayAndMonthName()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — 2024-08-15 → 5th Persian month = Mordad = "مرداد"
            var date = new DateTime(2024, 8, 15);

            // Act
            var result = CultureFormat.FormatMonthDay(date);

            // Assert — "۲۵ مرداد" (Persian day digit + space + month name)
            result.Should().Be($"{PersianTwo}{PersianFive} مرداد");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // FormatRelativeTime
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatRelativeTime_Null_ReturnsDash()
    {
        WithCulture("en-US", () =>
        {
            // Act
            var result = CultureFormat.FormatRelativeTime(
                null, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be("—");
        });
    }

    [Fact]
    public void FormatRelativeTime_LessThanMinute_ReturnsJustNowText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — now minus 1 second → diff.TotalMinutes < 1
            var submittedAtUtc = DateTime.UtcNow.AddSeconds(-1);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be("just now");
        });
    }

    [Fact]
    public void FormatRelativeTime_LessThanHour_ReturnsMinutesAgoText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 30 min ago → 1 <= minutes < 60
            var submittedAtUtc = DateTime.UtcNow.AddMinutes(-30);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — FormatDigits(30) under en-US = "30"; concat with " " + "minutes ago"
            result.Should().Be("30 minutes ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_LessThanDay_ReturnsHoursAgoText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 3 hours ago
            var submittedAtUtc = DateTime.UtcNow.AddHours(-3);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be("3 hours ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_LessThanWeek_ReturnsDaysAgoText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 3 days ago → 1 <= days < 7
            var submittedAtUtc = DateTime.UtcNow.AddDays(-3);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be("3 days ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_MoreThanWeek_ReturnsAbsoluteDate()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — 10 days ago → falls back to FormatTehranDateTime
            var submittedAtUtc = DateTime.UtcNow.AddDays(-10);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — must contain a date (not "10 days ago"). The exact
            // format depends on the current time, but it must contain "/"
            // (en-US short-date separator) and ":" (time separator).
            result.Should().Contain("/");
            result.Should().Contain(":");
            result.Should().NotContain("days ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_MinutesAgoText_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — 30 min ago
            var submittedAtUtc = DateTime.UtcNow.AddMinutes(-30);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — Persian digit for 30 + " " + "minutes ago"
            result.Should().Be($"{PersianThree}{PersianZero} minutes ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_HoursAgoText_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — 3 hours ago
            var submittedAtUtc = DateTime.UtcNow.AddHours(-3);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be($"{PersianThree} hours ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_DaysAgoText_UnderFaIr_ReturnsPersianDigits()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — 3 days ago
            var submittedAtUtc = DateTime.UtcNow.AddDays(-3);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert
            result.Should().Be($"{PersianThree} days ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_JustNowText_UnderFaIr_ReturnsCallerSuppliedText()
    {
        WithCulture("fa-IR", () =>
        {
            // Arrange — justNowText is supplied by the caller; in fa-IR it
            // would be "همین الان" in the actual razor page, but the SUT
            // does NOT translate it (only digit-swaps the numeric paths).
            var submittedAtUtc = DateTime.UtcNow;

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "همین الان", "دقیقه پیش", "ساعت پیش", "روز پیش");

            // Assert — justNowText is returned verbatim (no digit swap)
            result.Should().Be("همین الان");
        });
    }

    [Fact]
    public void FormatRelativeTime_BoundaryAtOneMinute_UsesMinutesAgoText()
    {
        // Document the boundary: diff.TotalMinutes == 1 falls through the
        // `< 1` check into the `< 60` branch, so the message becomes
        // "1 minutes ago" (NOT "just now").
        WithCulture("en-US", () =>
        {
            // Arrange — slightly over 60 seconds ago
            var submittedAtUtc = DateTime.UtcNow.AddSeconds(-61);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — diff.TotalMinutes = ~1.016, so int cast = 1
            result.Should().Be("1 minutes ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_BoundaryAtOneHour_UsesHoursAgoText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — slightly over 60 minutes ago
            var submittedAtUtc = DateTime.UtcNow.AddMinutes(-61);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — diff.TotalHours = ~1.016, so int cast = 1
            result.Should().Be("1 hours ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_BoundaryAtOneDay_UsesDaysAgoText()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — slightly over 24 hours ago
            var submittedAtUtc = DateTime.UtcNow.AddHours(-25);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — diff.TotalDays = ~1.04, so int cast = 1
            result.Should().Be("1 days ago");
        });
    }

    [Fact]
    public void FormatRelativeTime_BoundaryAtSevenDays_FallsBackToAbsoluteDate()
    {
        WithCulture("en-US", () =>
        {
            // Arrange — slightly over 7 days ago
            var submittedAtUtc = DateTime.UtcNow.AddDays(-8);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — falls back to absolute date (NOT "8 days ago")
            result.Should().NotContain("days ago");
            result.Should().Contain("/");
        });
    }

    [Fact]
    public void FormatRelativeTime_FutureTimestamp_ReturnsJustNowText()
    {
        // Document edge behavior: if the timestamp is in the FUTURE (which
        // shouldn't happen in normal use but could happen with clock skew),
        // diff.TotalMinutes is NEGATIVE — and negative values still satisfy
        // the `diff.TotalMinutes < 1` check, so the SUT returns justNowText
        // (does NOT fall through to the absolute-date fallback). This is an
        // SUT quirk worth documenting.
        WithCulture("en-US", () =>
        {
            // Arrange — 1 day in the future
            var submittedAtUtc = DateTime.UtcNow.AddDays(1);

            // Act
            var result = CultureFormat.FormatRelativeTime(
                submittedAtUtc, "just now", "minutes ago", "hours ago", "days ago");

            // Assert — negative TotalMinutes < 1 → returns justNowText
            result.Should().Be("just now");
        });
    }
}
