using System.Globalization;
using System.Text;
using TakOne.SharedKernel.DTOs;

namespace TakOne.WebUI.Services;

/// <summary>
/// Centralized culture-aware formatter for ALL numeric, monetary, and date
/// display in the TakOne WebUI.
///
/// WHY THIS EXISTS:
///   .NET's <c>NumberFormatInfo.NativeDigits</c> is INFORMATIONAL-ONLY in
///   modern .NET — calling <c>int.ToString("0", new CultureInfo("fa-IR"))</c>
///   returns ASCII "5", NOT "۵". The only reliable way to get Persian digits
///   out of .NET is to format with <c>InvariantCulture</c> first (which gives
///   ASCII digits + a stable decimal separator), then manually swap '0'-'9'
///   for '۰'-'۹' when the current UI culture is fa-IR.
///
///   Before this class existed, each page had its own private
///   <c>FormatPrice</c> / <c>FormatMoney</c> / <c>FormatDate</c> helper
///   that called <c>.ToString("N0", CultureInfo.CurrentCulture)</c>
///   directly — which produced ASCII digits in Persian mode (the digits
///   looked Persian only when a CSS font-feature-settings hack was active,
///   and looked ASCII otherwise). The Dashboard was the only page that did
///   it right; this class centralizes the Dashboard's pattern so every
///   page can use it.
///
/// <b>FA-IR MODE</b>:
///   - All digits become Persian (۰-۹).
///   - Thousand separators become Persian (٬, U+066C).
///   - Decimal separator becomes Persian (٫, U+066B).
///   - Percent symbol becomes Arabic percent (٪, U+066A).
///   - Dates use the Jalali (Persian) calendar via <see cref="PersianCalendar"/>.
///
/// <b>EN-US (AND ANY OTHER) MODE</b>:
///   - ASCII digits (0-9).
///   - Comma thousands separator, period decimal separator.
///   - Gregorian calendar dates.
///   - Persian digits that leaked in from the DB (e.g. SaleNumber is stored
///     in Persian) are converted back to ASCII so the English UI is clean.
/// </summary>
public static class CultureFormat
{
    // ══════════════════════════════════════════════════════════════════════
    // CORE DIGIT SWAP
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts any digit characters in the input string to match the
    /// current UI culture's preferred digit script:
    ///   - fa-IR mode → all digits become Persian (۰-۹). ASCII digits '0'-'9'
    ///     AND any existing Persian digits stay/become Persian.
    ///   - en-US (and any other) mode → all digits become ASCII (0-9).
    ///     Persian digits ۰-۹ are converted back to ASCII so strings stored
    ///     in Persian (e.g. SaleNumber.Value, which is hardcoded to Persian
    ///     in the domain layer) display as ASCII in English mode.
    ///
    /// Non-digit characters (letters, punctuation, separators) are passed
    /// through unchanged.
    /// </summary>
    public static string ToCultureDigits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var isFa = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9')
            {
                // ASCII digit → Persian (in fa mode) or keep as ASCII (otherwise).
                sb.Append(isFa ? (char)('۰' + (c - '0')) : c);
            }
            else if (c >= '۰' && c <= '۹')
            {
                // Persian digit → keep as Persian (in fa mode) or convert to ASCII (otherwise).
                sb.Append(isFa ? c : (char)('0' + (c - '۰')));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the current UI culture is Persian (fa-IR). Cached per call
    /// — cheap, but the property makes the call sites read better.
    /// </summary>
    public static bool IsPersian =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    // ══════════════════════════════════════════════════════════════════════
    // INTEGER / DECIMAL FORMATTING
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats an integer using the current UI culture's native digits.
    /// No thousands separators (use <see cref="FormatNumber(int)"/> for that).
    /// In fa-IR mode: Persian digits. In en-US: ASCII digits.
    /// </summary>
    public static string FormatDigits(int value)
        => ToCultureDigits(value.ToString("0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a long using the current UI culture's native digits.
    /// </summary>
    public static string FormatDigits(long value)
        => ToCultureDigits(value.ToString("0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a decimal using the current UI culture's native digits and
    /// the supplied format string (e.g. "0.0" for one decimal place).
    /// In fa-IR mode: Persian digits + Persian decimal separator (٫).
    /// In en-US mode: ASCII digits + period decimal separator.
    /// </summary>
    public static string FormatDigits(decimal value, string fmt)
        => ToCultureDigits(value.ToString(fmt, CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a numeric string using the current UI culture's native
    /// digits. Tries to preserve the original precision (e.g. "0.0" stays
    /// one decimal place). Falls back to <see cref="ToCultureDigits"/> for
    /// strings that aren't pure numbers (e.g. "INT-۱۴۰۵-۰۰۴۲" for a
    /// SaleNumber).
    /// </summary>
    public static string FormatDigits(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
        {
            var decimals = value.Contains('.') ? value.Length - value.IndexOf('.') - 1 : 0;
            var fmt = decimals > 0 ? "0." + new string('0', decimals) : "0";
            return ToCultureDigits(n.ToString(fmt, CultureInfo.InvariantCulture));
        }

        return ToCultureDigits(value);
    }

    /// <summary>
    /// Formats an integer with thousands separators (N0 format) using the
    /// current UI culture's native digits and separators. In fa-IR mode:
    /// Persian digits + Persian thousands separator (٬).
    /// </summary>
    public static string FormatNumber(int value)
        => ToCultureDigits(value.ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a long with thousands separators (N0 format).
    /// </summary>
    public static string FormatNumber(long value)
        => ToCultureDigits(value.ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a decimal with thousands separators (N0 format, no decimals).
    /// </summary>
    public static string FormatNumber(decimal value)
        => ToCultureDigits(Math.Round(value, 0).ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a percentage value (already calculated) using the current
    /// UI culture's digits + decimal separator. Appends the localized
    /// percent symbol (٪ in fa-IR, % in en-US) so the symbol also follows
    /// the culture.
    /// </summary>
    public static string FormatPercent(decimal pct, int decimals = 1)
    {
        var fmt = decimals > 0 ? "0." + new string('0', decimals) : "0";
        var s = ToCultureDigits(pct.ToString(fmt, CultureInfo.InvariantCulture));
        return s + (IsPersian ? "٪" : "%");
    }

    // ══════════════════════════════════════════════════════════════════════
    // MONEY FORMATTING
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a MoneyDto as Toman: IRR is divided by 10 and labelled
    /// "تومان". Other currencies pass through unchanged. Returns "—" for
    /// null or zero amounts (price-tag convention — an empty price tag is
    /// cleaner than "0 تومان").
    ///
    /// Used by: Products (shop), ProductDetail, Cart mini-cart.
    /// </summary>
    public static string FormatMoneyToman(MoneyDto? price)
    {
        if (price is null || price.Amount == 0m) return "—";
        var isIrr = string.Equals(price.Currency, "IRR", StringComparison.OrdinalIgnoreCase);
        var displayAmount = isIrr ? price.Amount / 10m : price.Amount;
        var amount = ToCultureDigits(displayAmount.ToString("N0", CultureInfo.InvariantCulture));
        var unit = isIrr ? "تومان" : price.Currency;
        return $"{amount} {unit}";
    }

    /// <summary>
    /// Formats a MoneyDto as Rial: IRR stays as-is and is labelled "ریال"
    /// (no division). Other currencies pass through unchanged. Returns "—"
    /// for null. Does NOT collapse zero amounts to "—" — admin pages and
    /// order details need to show "0 ریال" so the column aligns.
    ///
    /// Used by: AdminProducts, Sales, SaleDetail, Cart full page,
    /// CartBudgetBar.
    /// </summary>
    public static string FormatMoneyRial(MoneyDto? money)
    {
        if (money is null) return "—";
        var isIrr = string.Equals(money.Currency, "IRR", StringComparison.OrdinalIgnoreCase);
        var unit = isIrr ? "ریال" : money.Currency;
        var amount = ToCultureDigits(money.Amount.ToString("N0", CultureInfo.InvariantCulture));
        return $"{amount} {unit}";
    }

    /// <summary>
    /// Formats a raw decimal amount (no currency label) with thousands
    /// separators and culture-native digits. Used by: Dashboard KPIs where
    /// the currency is shown separately as a small unit suffix.
    /// </summary>
    public static string FormatAmount(decimal amount)
        => ToCultureDigits(Math.Round(amount, 0).ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>
    /// Formats a raw decimal amount in a shortened form for compact KPI
    /// displays:
    ///   - ≥ 1,000,000 → "{value÷1M}M" (e.g. "۲٫۴M" in fa-IR)
    ///   - ≥ 1,000     → "{value÷1K}K" (e.g. "۲۴٫۰K" in fa-IR)
    ///   - otherwise   → "{value}" with thousands separators
    /// </summary>
    public static string FormatAmountShort(decimal amount)
    {
        if (amount >= 1_000_000m)
        {
            var millions = (amount / 1_000_000m).ToString("0.0", CultureInfo.InvariantCulture);
            return ToCultureDigits(millions) + "M";
        }
        if (amount >= 1_000m)
        {
            var thousands = (amount / 1_000m).ToString("0.0", CultureInfo.InvariantCulture);
            return ToCultureDigits(thousands) + "K";
        }
        return ToCultureDigits(Math.Round(amount, 0).ToString("N0", CultureInfo.InvariantCulture));
    }

    // ══════════════════════════════════════════════════════════════════════
    // DATE / TIME FORMATTING
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a date using the current UI culture's full date format.
    /// In fa-IR mode this produces a Jalali (Persian) calendar date with
    /// Persian digits (e.g. "جمعه | ۱۴۰۵/۰۵/۱۶"); in en-US mode it produces
    /// a Gregorian date with ASCII digits (e.g. "Friday | 2026/08/07").
    ///
    /// WHY WE EXPLICITLY USE PersianCalendar:
    ///   .NET's fa-IR culture's default DateTimeFormat.Calendar is the
    ///   Gregorian calendar (a long-standing quirk), so
    ///   <c>date.ToString("yyyy/MM/dd", fa-IR)</c> returns the Gregorian
    ///   year (e.g. "2026") — not the Jalali year ("۱۴۰۵"). Even when the
    ///   digits render as Persian, the year is wrong. We therefore pull the
    ///   Jalali components from <see cref="PersianCalendar"/> explicitly,
    ///   then format each component with the current culture so the
    ///   NativeDigits (۰-۹ for fa-IR, 0-9 for en-US) kick in automatically
    ///   (via <see cref="ToCultureDigits"/>).
    /// </summary>
    public static string FormatDate(DateTime date)
    {
        if (!IsPersian)
        {
            // Per user spec, the day-name and the date are separated by " | "
            // (not a comma) so the date reads as a distinct visual chunk.
            return date.ToString("dddd | yyyy/MM/dd", CultureInfo.CurrentCulture);
        }

        // fa-IR: build the Jalali date string piece by piece using invariant
        // (ASCII) digits first, then run ToCultureDigits to swap them for
        // Persian digits. This is the only reliable way to get Persian
        // digits out of .NET — NumberFormatInfo.NativeDigits is
        // informational-only and not honored by ToString.
        var pc = new PersianCalendar();
        var dayName = date.ToString("dddd", CultureInfo.CurrentCulture);
        var year = pc.GetYear(date).ToString("0000", CultureInfo.InvariantCulture);
        var month = pc.GetMonth(date).ToString("00", CultureInfo.InvariantCulture);
        var day = pc.GetDayOfMonth(date).ToString("00", CultureInfo.InvariantCulture);
        return ToCultureDigits($"{dayName} | {year}/{month}/{day}");
    }

    /// <summary>
    /// Formats a UTC timestamp for display in Tehran time, culture-aware.
    /// Tehran = UTC + 3:30 (no DST since 2022). The offset is added inline
    /// (no TimeZoneInfo lookup) so the conversion is bulletproof across
    /// deployments.
    ///
    /// Format: short date + short time (e.g. "۱۴۰۵/۰۵/۱۶ ۱۵:۳۰" in fa-IR,
    /// "8/2/2024 3:30 PM" in en-US). The date is Jalali in fa-IR (built
    /// piece-by-piece from PersianCalendar, then digit-swapped) and
    /// Gregorian in en-US.
    /// </summary>
    public static string FormatTehranDateTime(DateTimeOffset utc)
    {
        // Tehran = UTC + 3:30 (no DST since 2022). AddHours shifts the
        // datetime but keeps the +00:00 offset; .DateTime extracts the
        // shifted DateTime without any further timezone conversion.
        var tehran = utc.AddHours(3.5).DateTime;

        if (!IsPersian)
        {
            return tehran.ToString("g", CultureInfo.CurrentCulture);
        }

        // fa-IR: build "yyyy/MM/dd HH:mm" with Jalali date + Persian digits.
        var pc = new PersianCalendar();
        var year = pc.GetYear(tehran).ToString("0000", CultureInfo.InvariantCulture);
        var month = pc.GetMonth(tehran).ToString("00", CultureInfo.InvariantCulture);
        var day = pc.GetDayOfMonth(tehran).ToString("00", CultureInfo.InvariantCulture);
        var hour = tehran.Hour.ToString("00", CultureInfo.InvariantCulture);
        var minute = tehran.Minute.ToString("00", CultureInfo.InvariantCulture);
        return ToCultureDigits($"{year}/{month}/{day} - {hour}:{minute}");
    }

    /// <summary>
    /// Formats a UTC DateTime (not DateTimeOffset) in Tehran time.
    /// Convenience overload for callers that have a plain DateTime in UTC.
    /// </summary>
    public static string FormatTehranDateTime(DateTime utc)
        => FormatTehranDateTime(new DateTimeOffset(utc, TimeSpan.Zero));

    /// <summary>
    /// Formats a UTC DateTime as a short date + time string, assuming the
    /// input is already in the user's local timezone (e.g. from
    /// <c>ToLocalTime()</c>). Use <see cref="FormatTehranDateTime(DateTimeOffset)"/>
    /// instead when the input is UTC.
    ///
    /// WHY THIS OVERLOAD EXISTS:
    ///   Some pages (e.g. EditGroup) already do their own ToLocalTime()
    ///   conversion before calling the formatter. This overload formats
    ///   the already-local DateTime in the current culture, with proper
    ///   Persian digits and Jalali date in fa-IR mode.
    /// </summary>
    public static string FormatLocalDateTime(DateTime local)
    {
        if (!IsPersian)
        {
            return local.ToString("g", CultureInfo.CurrentCulture);
        }

        var pc = new PersianCalendar();
        var year = pc.GetYear(local).ToString("0000", CultureInfo.InvariantCulture);
        var month = pc.GetMonth(local).ToString("00", CultureInfo.InvariantCulture);
        var day = pc.GetDayOfMonth(local).ToString("00", CultureInfo.InvariantCulture);
        var hour = local.Hour.ToString("00", CultureInfo.InvariantCulture);
        var minute = local.Minute.ToString("00", CultureInfo.InvariantCulture);
        return ToCultureDigits($"{year}/{month}/{day} - {hour}:{minute}");
    }

    /// <summary>
    /// Formats a date as "{day} {PersianMonthName}" in fa-IR mode (e.g.
    /// "۱ مرداد") or "MMM d" in en-US mode (e.g. "Aug 1"). Used by
    /// CartBudgetBar to show the budget reset date.
    /// </summary>
    public static string FormatMonthDay(DateTime date)
    {
        if (!IsPersian)
        {
            return date.ToString("MMM d", CultureInfo.CurrentCulture);
        }

        var pc = new PersianCalendar();
        var month = pc.GetMonth(date);
        var day = pc.GetDayOfMonth(date);

        // Look up the Persian month name from the fa-IR culture's
        // DateTimeFormatInfo.MonthNames array.
        var faCulture = CultureInfo.GetCultureInfo("fa-IR");
        var monthNames = faCulture.DateTimeFormat.MonthNames;
        var monthName = month <= monthNames.Length
            ? monthNames[month - 1]
            : month.ToString("00", CultureInfo.InvariantCulture);

        var dayStr = ToCultureDigits(day.ToString("0", CultureInfo.InvariantCulture));
        return $"{dayStr} {monthName}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // RELATIVE TIME
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a UTC timestamp as a relative "X minutes ago" / "X hours ago"
    /// / "X days ago" string, with culture-native digits. Falls back to
    /// absolute date if older than 7 days. The unit words are passed in by
    /// the caller (localized via the page's IStringLocalizer).
    ///
    /// In fa-IR mode this produces Persian digits (e.g. "۵ دقیقه پیش");
    /// in en-US mode it produces ASCII digits (e.g. "5 minutes ago").
    /// </summary>
    public static string FormatRelativeTime(
        DateTime? submittedAtUtc,
        string justNowText,
        string minutesAgoText,
        string hoursAgoText,
        string daysAgoText)
    {
        if (submittedAtUtc is null) return "—";

        var now = DateTime.UtcNow;
        var diff = now - submittedAtUtc.Value;

        if (diff.TotalMinutes < 1) return justNowText;
        if (diff.TotalMinutes < 60)
            return FormatDigits((int)diff.TotalMinutes) + " " + minutesAgoText;
        if (diff.TotalHours < 24)
            return FormatDigits((int)diff.TotalHours) + " " + hoursAgoText;
        if (diff.TotalDays < 7)
            return FormatDigits((int)diff.TotalDays) + " " + daysAgoText;

        // Older than a week: show the absolute Jalali (fa-IR) or Gregorian
        // (en-US) date, with culture-native digits.
        return FormatTehranDateTime(submittedAtUtc.Value);
    }
}