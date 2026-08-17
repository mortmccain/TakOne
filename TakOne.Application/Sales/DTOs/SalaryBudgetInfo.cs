using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Snapshot of a customer's monthly salary budget info — used by the
/// CartBudgetBar UI component (Step 7) to display "Budget: X / Spent: Y /
/// Remaining: Z" and by the 5 sale-mutating handlers (Step 4 wiring) to
/// enforce the salary budget.
///
/// LIFETIME:
///   This is a SNAPSHOT — it reflects the state at the moment
///   <c>ISalaryBudgetService.GetBudgetInfoAsync</c> was called. It is NOT
///   a live view. Sale-mutating handlers must call
///   <c>GetBudgetInfoAsync</c> on EACH mutation (no caching at the
///   handler level) to ensure they see the latest consumed amount.
///
/// IMMUTABLE:
///   All properties are <c>init</c> — once created, the snapshot doesn't
///   change. This makes it safe to pass around without defensive copies.
/// </summary>
public sealed record SalaryBudgetInfo
{
    /// <summary>
    /// The customer's monthly salary (from their CustomerGroup).
    /// Always populated when the info is non-null.
    /// </summary>
    public required Money Salary { get; init; }

    /// <summary>
    /// Total amount consumed in the current Persian month — sum of:
    ///   <list type="bullet">
    ///     <item>The customer's active DRAFT cart total (if any) —
    ///         "cart reserves budget"</item>
    ///     <item>All submitted (non-cancelled) sales with SubmittedAtUtc
    ///         in [WindowStartUtc, WindowEndUtc)</item>
    ///   </list>
    ///
    /// "Use it or lose it": cross-month cancellations do NOT refund to
    /// the new month. The cancelled sale is simply not in the new month's
    /// window, so it doesn't count either way. See
    /// <see cref="ISalaryBudgetService"/> for the full rationale.
    /// </summary>
    public required decimal Consumed { get; init; }

    /// <summary>
    /// Remaining budget = <see cref="Salary"/>.Amount - <see cref="Consumed"/>.
    /// Can be NEGATIVE if the salary was lowered mid-month after a
    /// purchase was made (in which case the customer has effectively
    /// overspent and cannot add ANYTHING to their cart until next month).
    /// </summary>
    public required decimal Remaining { get; init; }

    /// <summary>
    /// The UTC DateTime of the 1st of the current Persian month.
    /// Computed by <c>Domain.Common.SalaryBudgetWindow.GetStartOfCurrentMonth</c>
    /// — a code constant, not a DB column.
    /// </summary>
    public required DateTime WindowStartUtc { get; init; }

    /// <summary>
    /// The UTC DateTime of the 1st of the NEXT Persian month.
    /// Computed by <c>Domain.Common.SalaryBudgetWindow.GetStartOfNextMonth</c>.
    /// The CartBudgetBar displays this as "Resets on {date}".
    /// </summary>
    public required DateTime WindowEndUtc { get; init; }
}