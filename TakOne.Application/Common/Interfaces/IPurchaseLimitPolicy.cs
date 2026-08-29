using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Centralizes the system's purchase-limit policy (count-limit enforcement,
/// salary-budget enforcement, currency matching) into a single service.
///
/// Encapsulates the global <see cref="Domain.Common.Enums.LimitMode"/>
/// (CountOnly / SalaryOnly / Both) and exposes simple primitives that
/// sale-mutating handlers can call without each having to re-implement
/// the policy logic.
///
/// DESIGN NOTES:
///   - The current LimitMode is read from <see cref="ISystemSettingsService"/>,
///     which caches in-process (IMemoryCache). Zero DB hits in steady state.
///   - Per-product count limits are looked up via Product's
///     <c>GetPurchaseLimitForGroup(groupId)</c> method — but ONLY when
///     the mode is CountOnly or Both.
///   - Currency matching ALWAYS applies (regardless of mode). A customer
///     whose group's salary is in IRR cannot buy a product priced in USD,
///     even when the system is in CountOnly mode. This is the universal
///     "mixed-currencies are not allowed" rule.
///   - Staff users (GroupId == null) bypass ALL limits and currency checks.
///
/// CUSTOMER-VISIBILITY RULE:
///   Errors produced via this policy must use the stable-code pattern
///   (see PurchaseLimitErrors, SalaryBudgetExceededErrors,
///   CurrencyMismatchErrors). The customer must NEVER see the word "group"
///   in any UI string — all customer-facing messages are localized by the
///   UI layer using IStringLocalizer.
/// </summary>
public interface IPurchaseLimitPolicy
{
    /// <summary>
    /// Returns true if the system's current LimitMode enforces per-product
    /// count limits (i.e. CountOnly or Both).
    ///
    /// Reads from cached SystemSettingsService — zero DB hits in steady state.
    /// </summary>
    Task<bool> IsCountLimitEnforcedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the system's current LimitMode enforces the monthly
    /// salary budget (i.e. SalaryOnly or Both).
    ///
    /// Reads from cached SystemSettingsService — zero DB hits in steady state.
    /// </summary>
    Task<bool> IsSalaryBudgetEnforcedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the per-product count limit for the given customer's group.
    /// Returns null if:
    ///   - count limits are NOT enforced (mode = SalaryOnly) — caller should
    ///     skip the count check entirely
    ///   - groupId is null (staff — no per-product cap)
    ///   - the product has no limit set for the customer's group
    ///
    /// Reads Product's owned CustomerGroupPurchaseLimit collection. The
    /// caller is responsible for loading the Product (tracked or
    /// AsNoTracking) before calling this method.
    /// </summary>
    Task<int?> GetCountLimitAsync(Guid productId, Guid? groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// BATCHED variant of <see cref="GetCountLimitAsync"/> — resolves the
    /// per-product count limits for a WHOLE PAGE of products in one
    /// round-trip (single GetByIdsReadOnlyAsync batch load + one cached
    /// LimitMode read).
    ///
    /// WHY THIS EXISTS: the customer-facing Products grid called
    /// <see cref="GetCountLimitAsync"/> once PER PRODUCT on the page (up
    /// to 100 sequential DB round-trips per page render — an N+1). This
    /// variant collapses those into one query.
    ///
    /// Returns a dictionary keyed by product Id. Missing keys (product
    /// not found) and null values both mean "no limit".
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int?>> GetCountLimitsAsync(
        IReadOnlyCollection<Guid> productIds,
        Guid? groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates currency matching. Returns true if the customer's group
    /// salary currency matches the product's price currency.
    ///
    /// Currency matching ALWAYS applies — regardless of LimitMode. A
    /// customer whose salary is in IRR cannot buy a product priced in USD,
    /// even when LimitMode is CountOnly.
    ///
    /// Returns true (no constraint) if:
    ///   - groupId is null (staff can buy anything)
    ///   - the group doesn't exist (defensive — shouldn't happen since
    ///     User.GroupId is a FK)
    ///   - the group's salary currency matches the product's price currency
    ///
    /// Used by the 5 sale-mutating handlers BEFORE adding/updating a line.
    /// On mismatch, the handler returns
    /// <see cref="CurrencyMismatchErrors.Format"/>.
    /// </summary>
    Task<bool> IsCurrencyMatchAsync(Guid productId, Guid? groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// BATCHED variant of <see cref="IsCurrencyMatchAsync"/> — resolves,
    /// for a whole set of products in ONE round-trip (single group load +
    /// single product batch load), WHICH products' price currencies do
    /// NOT match the given group's salary currency.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS: the cart-path handlers (QuickReorderLastSale,
    /// SubmitSale) previously called <see cref="IsCurrencyMatchAsync"/>
    /// once PER LINE — each call re-loaded the (identical) group AND
    /// re-loaded a product the handler had ALREADY batch-loaded — an N+1
    /// that scaled with the number of cart lines on the hottest write
    /// paths. This variant collapses the per-line calls into one query.
    ///
    /// SEMANTICS (identical to the single-product variant, applied to a
    /// batch):
    ///   - groupId null (staff)                → empty set (no constraint)
    ///   - group missing (defensive)           → empty set (no constraint)
    ///   - product missing from the result set  → NOT in the mismatch set
    ///     (the single-product variant returns true for a missing product;
    ///     here the product simply isn't reported as mismatched)
    ///   - otherwise                            → product Id is in the set
    ///     iff its price currency differs from the group's salary currency.
    /// </remarks>
    Task<IReadOnlyCollection<Guid>> GetCurrencyMismatchedProductIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        Guid? groupId,
        CancellationToken cancellationToken = default);
}