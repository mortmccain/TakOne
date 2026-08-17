namespace TakOne.Domain.Common.Enums;

/// <summary>
/// The system-wide mode that controls how purchase limits are enforced
/// for every customer in the application.
///
/// This is a GLOBAL setting — it applies to every group, every user, every
/// product. It is stored in a single-row <c>SystemSettings</c> table and
/// cached in-process by the <c>ISystemSettingsService</c> (Infrastructure
/// layer). Changing it via the admin UI invalidates the cache and the new
/// mode takes effect on the next purchase-limit check — no app restart
/// required.
///
/// VALUES:
///   <see cref="CountOnly"/>   — Only the per-product count limits are
///                              enforced. Salary is ignored entirely.
///                              The salary <b>currency</b> is still
///                              enforced (see <see cref="CustomerGroup"/>).
///
///   <see cref="SalaryOnly"/>  — Only the monthly salary budget is
///                              enforced. Per-product count limits are
///                              still configured (so the admin can switch
///                              modes later) but are not checked.
///
///   <see cref="Both"/>        — Both checks must pass. A user cannot buy
///                              100 units of a product whose count limit
///                              is 1 just because their salary allows it,
///                              and they cannot exceed their salary just
///                              because the count limit allows it.
///
/// CURRENCY MATCHING (always enforced regardless of mode):
///   Even when mode = CountOnly, a user may only buy products priced in
///   the same currency as their group's salary currency. This is enforced
///   by <c>IPurchaseLimitPolicy</c> on every cart mutation, in every mode.
///   The system has no exchange-rate table and cannot compare amounts
///   across currencies, so mismatched currencies are hard-rejected.
///
/// PERSISTENCE:
///   Stored as an <c>int</c> column on the <c>SystemSettings</c> table.
///   EF Core maps the enum to its underlying int automatically.
/// </summary>
public enum LimitMode
{
    /// <summary>
    /// Only per-product count limits are enforced. Salary amount is ignored
    /// (but salary currency is still enforced). Default value for a fresh
    /// install — preserves the original pre-salary-feature behaviour.
    /// </summary>
    CountOnly = 1,

    /// <summary>
    /// Only the monthly salary budget is enforced. Per-product count limits
    /// are configured but not checked. Used when the admin wants pure
    /// monetary budgeting without unit caps.
    /// </summary>
    SalaryOnly = 2,

    /// <summary>
    /// Both count and salary limits must pass. Recommended for the strictest
    /// enforcement — prevents both hoarding of high-demand items and
    /// overspending the monthly budget.
    /// </summary>
    Both = 3
}
