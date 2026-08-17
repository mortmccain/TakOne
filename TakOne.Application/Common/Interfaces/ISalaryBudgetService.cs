using TakOne.Application.Sales.DTOs;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Computes and exposes the customer's monthly salary budget info — used
/// by the CartBudgetBar UI component (Step 7) and by the 5 sale-mutating
/// handlers (Step 4 wiring) to enforce the "cart reserves budget" rule.
///
/// MONTHLY WINDOW (Persian / Jalali calendar):
///   The window resets on the 1st of each Persian month. The window's
///   start/end DateTimes are computed by <c>Domain.Common.SalaryBudgetWindow</c>
///   — a pure static helper (not stored in DB, because the rule is universal).
///
/// "USE IT OR LOSE IT" SEMANTICS:
///   - The consumed amount is computed as:
///       draft_cart_total + SUM(submitted_non_cancelled_sales_this_month)
///   - When a sale is CANCELLED, it falls out of the monthly query sum
///     automatically (the cancellation refund is implicit, no separate
///     "refund" operation needed).
///   - Cross-month cancellations do NOT refund to the new month. If a
///     customer submitted a sale in month M and cancels it in month M+1,
///     the cancellation does NOT free up budget in M+1 (the cancelled
///     sale is no longer in M+1's window — it was in M's window — so it
///     doesn't count either way). This is the correct, intended behavior:
///     the budget that was "spent" in M stays spent.
///
/// "CART RESERVES BUDGET" RULE:
///   The customer's DRAFT cart total counts as consumed IMMEDIATELY —
///   not just at submit time. This prevents the customer from adding
///   items to multiple drafts (e.g. via multi-tab) that together exceed
///   the salary budget. The reservation is implicit (no separate
///   "reservation" table) — the draft Sale's Total field IS the
///   reservation.
///
/// SUBMIT IS A BUDGET NO-OP:
///   Submitting a sale does NOT change the consumed amount — the draft
///   Sale.Total just becomes the submitted Sale.Total, and the latter
///   replaces the former in the monthly query sum. Net change = 0.
/// </summary>
public interface ISalaryBudgetService
{
    /// <summary>
    /// Returns the customer's salary budget info for the current Persian
    /// month. Includes: salary amount + currency, consumed amount,
    /// remaining amount, window start/end (UTC).
    ///
    /// Returns null if:
    ///   - the customer has no group (staff users)
    ///   - salary budget is NOT enforced (mode = CountOnly) — no point
    ///     computing consumed amount when the budget doesn't apply
    ///
    /// USED BY:
    ///   - GetActiveCartForUserQueryHandler (to enrich CartDto with budget
    ///     info for the CartBudgetBar — Step 7)
    ///   - The 5 sale-mutating handlers (to enforce the salary budget —
    ///     Step 4 wiring)
    /// </summary>
    Task<SalaryBudgetInfo?> GetBudgetInfoAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the customer's group salary (Money: amount + ISO currency),
    /// or null if the customer has no group.
    ///
    /// Used by <see cref="IPurchaseLimitPolicy.IsCurrencyMatchAsync"/> to
    /// resolve the salary currency for currency matching. Read-only —
    /// cached via ICustomerGroupRepository.GetByIdReadOnlyAsync.
    /// </summary>
    Task<TakOne.SharedKernel.ValueObjects.Money?> GetGroupSalaryAsync(Guid? groupId, CancellationToken cancellationToken = default);
}