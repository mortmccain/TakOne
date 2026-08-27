using System.Globalization;
using FluentAssertions;
using TakOne.Domain.Common;
using Xunit;

namespace TakOne.Domain.Tests.Common;

/// <summary>
/// Unit tests for the <see cref="SalaryBudgetWindow"/> pure helper.
/// Verifies the start-of-current-Persian-month calculation, the
/// start-of-next-Persian-month rollover (including year-rollover at
/// month 12 → month 1 of next year), the Utc kind, and the
/// hour=0/minute=0/second=0 boundary.
///
/// Uses <see cref="PersianCalendar"/> directly in the test to compute
/// expected values — same BCL class the SUT uses — so the tests are
/// self-consistent (they assert that SalaryBudgetWindow's logic
/// matches the Persian calendar, without hardcoding fragile dates).
/// </summary>
public class SalaryBudgetWindowTests
{
    private static readonly PersianCalendar Pc = new();

    /// <summary>
    /// Returns the Gregorian UTC DateTime corresponding to the 1st day
    /// (00:00:00) of the Persian month containing <paramref name="utcNow"/>.
    /// This is what the SUT should return.
    /// </summary>
    private static DateTime ExpectedStartOfCurrentMonth(DateTime utcNow)
    {
        var dt = Pc.ToDateTime(Pc.GetYear(utcNow), Pc.GetMonth(utcNow), 1, 0, 0, 0, 0);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Returns the Gregorian UTC DateTime corresponding to the 1st day
    /// (00:00:00) of the NEXT Persian month after the one containing
    /// <paramref name="utcNow"/>. Handles year rollover at Persian month 12.
    /// </summary>
    private static DateTime ExpectedStartOfNextMonth(DateTime utcNow)
    {
        var year = Pc.GetYear(utcNow);
        var month = Pc.GetMonth(utcNow);

        if (month >= 12)
        {
            year++;
            month = 1;
        }
        else
        {
            month++;
        }

        var dt = Pc.ToDateTime(year, month, 1, 0, 0, 0, 0);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    // ======================================================================
    //                          GetStartOfCurrentMonth — known Gregorian dates
    // ======================================================================

    [Fact]
    public void GetStartOfCurrentMonth_ForGregorian2024_03_20_ReturnsPersian1403_01_01()
    {
        // Arrange — 2024-03-20 UTC is the first day of Persian year 1403
        // (Nowruz). The start of the current Persian month for this date is
        // Persian 1403/01/01 00:00:00 UTC.
        var utcNow = new DateTime(2024, 3, 20, 10, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert — implementation matches the PersianCalendar computation
        result.Should().Be(ExpectedStartOfCurrentMonth(utcNow));
        // And specifically: Persian year 1403, month 1, day 1
        Pc.GetYear(result).Should().Be(1403);
        Pc.GetMonth(result).Should().Be(1);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    [Fact]
    public void GetStartOfCurrentMonth_ForGregorian2024_08_15_ReturnsPersian1403_05_01()
    {
        // Arrange — 2024-08-15 falls in Persian month 5 (Mordad) of year 1403
        var utcNow = new DateTime(2024, 8, 15, 12, 30, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert
        result.Should().Be(ExpectedStartOfCurrentMonth(utcNow));
        Pc.GetYear(result).Should().Be(1403);
        Pc.GetMonth(result).Should().Be(5);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    [Fact]
    public void GetStartOfCurrentMonth_ForGregorian2025_03_21_ReturnsPersian1404_01_01()
    {
        // Arrange — 2025-03-21 should fall in Persian year 1404 (post-equinox)
        var utcNow = new DateTime(2025, 3, 21, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert
        result.Should().Be(ExpectedStartOfCurrentMonth(utcNow));
        Pc.GetYear(result).Should().Be(1404);
        Pc.GetMonth(result).Should().Be(1);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    [Fact]
    public void GetStartOfCurrentMonth_ForGregorian2026_08_15_ReturnsPersian1405_05_01()
    {
        // Arrange — 2026-08-15 falls in Persian month 5 of year 1405
        var utcNow = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert
        result.Should().Be(ExpectedStartOfCurrentMonth(utcNow));
        Pc.GetYear(result).Should().Be(1405);
        Pc.GetMonth(result).Should().Be(5);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    // ======================================================================
    //                          GetStartOfNextMonth
    // ======================================================================

    [Fact]
    public void GetStartOfNextMonth_ForGregorian2024_03_15_ReturnsStartOfPersian1403_01()
    {
        // Arrange — 2024-03-15 falls in Persian 1402/12. Next month
        // rolls over the year → 1403/01.
        var utcNow = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfNextMonth(utcNow);

        // Assert
        result.Should().Be(ExpectedStartOfNextMonth(utcNow));
        Pc.GetYear(result).Should().Be(1403);
        Pc.GetMonth(result).Should().Be(1);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    [Fact]
    public void GetStartOfNextMonth_ForGregorian2024_07_15_ReturnsStartOfPersian1403_05()
    {
        // Arrange — 2024-07-15 falls in Persian 1403/04. Next month → 1403/05.
        var utcNow = new DateTime(2024, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfNextMonth(utcNow);

        // Assert — same Persian year, next month
        result.Should().Be(ExpectedStartOfNextMonth(utcNow));
        Pc.GetYear(result).Should().Be(1403);
        Pc.GetMonth(result).Should().Be(5);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    [Fact]
    public void GetStartOfNextMonth_ForDateInPersianMonth12_RollsOverToNextYear()
    {
        // Arrange — a date inside Persian month 12 should roll over to
        // month 1 of the next year. Pick 2024-02-25 (which falls in Esfand
        // = Persian month 12 of year 1402).
        var utcNow = new DateTime(2024, 2, 25, 0, 0, 0, DateTimeKind.Utc);

        // Sanity: verify the precondition (it's in month 12)
        Pc.GetMonth(utcNow).Should().Be(12, "2024-02-25 should be in Persian month 12 (Esfand)");

        // Act
        var result = SalaryBudgetWindow.GetStartOfNextMonth(utcNow);

        // Assert — year rolled over from 1402 → 1403, month 12 → 1
        Pc.GetYear(result).Should().Be(1403);
        Pc.GetMonth(result).Should().Be(1);
        Pc.GetDayOfMonth(result).Should().Be(1);
    }

    // ======================================================================
    //                          Utc kind / boundary
    // ======================================================================

    [Fact]
    public void GetStartOfCurrentMonth_ReturnsDateTimeWithUtcKind()
    {
        // Arrange
        var utcNow = new DateTime(2024, 8, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert — the SUT calls DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        result.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void GetStartOfNextMonth_ReturnsDateTimeWithUtcKind()
    {
        // Arrange
        var utcNow = new DateTime(2024, 8, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfNextMonth(utcNow);

        // Assert
        result.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void GetStartOfCurrentMonth_ReturnsMidnightAtFirstDayOfMonth()
    {
        // Arrange
        var utcNow = new DateTime(2024, 8, 15, 12, 30, 45, DateTimeKind.Utc);

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow);

        // Assert — Hour=0, Minute=0, Second=0, Millisecond=0
        result.Hour.Should().Be(0);
        result.Minute.Should().Be(0);
        result.Second.Should().Be(0);
        result.Millisecond.Should().Be(0);
    }

    [Fact]
    public void GetStartOfCurrentMonth_WhenUtcNowFallsExactlyOnMonthStart_ReturnsSameDateTime()
    {
        // Arrange — construct the exact start of a Persian month
        var exactMonthStart = ExpectedStartOfCurrentMonth(
            new DateTime(2024, 8, 15, 0, 0, 0, DateTimeKind.Utc));

        // Act — pass that exact DateTime as "now"; the result must equal it
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(exactMonthStart);

        // Assert — boundary: a DateTime at the start of a Persian month
        // maps to itself
        result.Should().Be(exactMonthStart);
    }

    [Fact]
    public void GetStartOfCurrentMonth_WithNullUtcNow_DefaultsToDateTimeUtcNow()
    {
        // Arrange — no utcNow passed; SUT defaults to DateTime.UtcNow
        var before = DateTime.UtcNow;

        // Act
        var result = SalaryBudgetWindow.GetStartOfCurrentMonth(utcNow: null);

        // Assert — non-default DateTime with Utc kind; we don't assert on
        // the exact value (it would be flaky), just that it's a real UTC
        // timestamp near "now" (the start of the current Persian month
        // containing the moment this test ran)
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
        result.Should().BeOnOrAfter(before.AddDays(-31)); // sanity: within the last month
    }
}
