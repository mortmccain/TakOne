using System.Globalization;

namespace TakOne.Domain.Common;

/// <summary>
/// Pure helper that computes the boundaries of the current Persian
/// (Jalali) calendar month for salary-budget queries.
///
/// WHY THIS EXISTS:
///   The salary budget resets on the 1st of every Persian month. This
///   is a HARD BUSINESS RULE, not a configurable setting — it applies
///   to every group, every user, every month, and is the same every
///   year. Storing it as a database column (e.g. on CustomerGroup or
///   SystemSettings) would incorrectly suggest it could vary by group
///   or be admin-configurable, which it cannot.
///
///   Keeping it as a pure static helper in the Domain layer means:
///     - It is auditable in source control (a code change is required
///       to alter the reset rule — admins cannot accidentally break it
///       via the UI).
///     - It has zero DB cost — no settings lookup, no cache.
///     - It is trivially testable (pass a known DateTime, assert the
///       returned boundary).
///     - It is referenced from <c>ISalaryBudgetService</c> (Application)
///       and from <c>SubmitSaleCommandHandler</c>'s defensive re-check.
///
/// "USE IT OR LOSE IT" SEMANTICS:
///   The consumed-budget query filters by
///   <c>SubmittedAt &gt;= GetStartOfCurrentMonth(now)</c>. When the
///   calendar crosses into a new Persian month, last month's sales
///   automatically fall out of the sum — the query itself IS the
///   monthly reset. There is no batch job, no cron, no manual reset.
///
///   Consequence: a sale submitted on the last day of month 1 and
///   cancelled on the 2nd of month 2 receives NO refund in month 2.
///   The sale's <c>SubmittedAt</c> is in month 1, so it was never
///   counted in month 2's sum. The cancellation removes it from month
///   1's history (which is already closed — fiscal period is final).
///   This is intentional and matches how real procurement budgets work.
///
/// PERSIAN CALENDAR NOTE:
///   <see cref="PersianCalendar"/> is a BCL class (System.Globalization).
///   It correctly handles the Solar Hijri calendar used in Iran, including
///   leap-year month lengths. The first day of each Persian month is
///   always day 1, regardless of month length.
/// </summary>
public static class SalaryBudgetWindow
{
    /// <summary>
    /// Returns the UTC DateTime corresponding to the start (00:00:00)
    /// of the 1st day of the current Persian month, evaluated at
    /// <paramref name="utcNow"/> (defaults to <see cref="DateTime.UtcNow"/>
    /// if omitted).
    ///
    /// This value is used as the lower bound of the salary-budget
    /// "consumed so far this month" query:
    /// <code>
    ///   WHERE SubmittedAt &gt;= SalaryBudgetWindow.GetStartOfCurrentMonth()
    ///     AND Status != Cancelled
    /// </code>
    /// </summary>
    public static DateTime GetStartOfCurrentMonth(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var pc = new PersianCalendar();
        var dt = pc.ToDateTime(
            pc.GetYear(now),
            pc.GetMonth(now),
            1,
            0, 0, 0, 0);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Returns the UTC DateTime corresponding to the start (00:00:00)
    /// of the 1st day of the NEXT Persian month after the current one,
    /// evaluated at <paramref name="utcNow"/>.
    ///
    /// Useful for "your budget resets on {date}" UI hints. Not used by
    /// the budget query itself (which only needs the lower bound).
    /// </summary>
    public static DateTime GetStartOfNextMonth(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var pc = new PersianCalendar();

        var year = pc.GetYear(now);
        var month = pc.GetMonth(now);

        // Persian calendar has 12 months. Month 12 rolls over to month 1
        // of the next year.
        if (month >= 12)
        {
            year++;
            month = 1;
        }
        else
        {
            month++;
        }

        var dt = pc.ToDateTime(
            year, month, 1,
            0, 0, 0, 0);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
}
