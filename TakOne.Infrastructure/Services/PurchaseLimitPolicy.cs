using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Infrastructure implementation of <see cref="IPurchaseLimitPolicy"/>.
///
/// Resolves the system's current <see cref="LimitMode"/> from the cached
/// <see cref="ISystemSettingsService"/> (zero DB hits in steady state) and
/// applies the count-limit + currency-matching rules to a sale-mutating
/// request.
///
/// SEE <see cref="IPurchaseLimitPolicy"/> for the full policy rationale and
/// the customer-visibility rule (no "group" word in any error string).
/// </summary>
public sealed class PurchaseLimitPolicy : IPurchaseLimitPolicy
{
    private readonly ISystemSettingsService _systemSettings;
    private readonly ICustomerGroupRepository _customerGroupRepository;
    private readonly IProductRepository _productRepository;
    private readonly ILogger<PurchaseLimitPolicy> _logger;

    public PurchaseLimitPolicy(
        ISystemSettingsService systemSettings,
        ICustomerGroupRepository customerGroupRepository,
        IProductRepository productRepository,
        ILogger<PurchaseLimitPolicy> logger)
    {
        _systemSettings = systemSettings;
        _customerGroupRepository = customerGroupRepository;
        _productRepository = productRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsCountLimitEnforcedAsync(CancellationToken cancellationToken = default)
    {
        var mode = await _systemSettings.GetLimitModeAsync(cancellationToken);
        return mode is LimitMode.CountOnly or LimitMode.Both;
    }

    /// <inheritdoc />
    public async Task<bool> IsSalaryBudgetEnforcedAsync(CancellationToken cancellationToken = default)
    {
        var mode = await _systemSettings.GetLimitModeAsync(cancellationToken);
        return mode is LimitMode.SalaryOnly or LimitMode.Both;
    }

    /// <inheritdoc />
    public async Task<int?> GetCountLimitAsync(Guid productId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Short-circuits:
        //   1. If the system's mode is SalaryOnly, count limits are NOT
        //      enforced — return null regardless of the product/group.
        //   2. If the customer has no group (staff), no per-product cap
        //      applies — return null.
        //
        // Only when BOTH conditions are satisfied (mode = CountOnly or Both
        // AND groupId is non-null) do we hit the DB to resolve the limit.
        // ------------------------------------------------------------------
        if (groupId is null)
        {
            return null;
        }

        var mode = await _systemSettings.GetLimitModeAsync(cancellationToken);
        if (mode == LimitMode.SalaryOnly)
        {
            // Count limits are off — only salary budget is enforced.
            return null;
        }

        // ------------------------------------------------------------------
        // Load the product (AsNoTracking — we only READ its PurchaseLimits
        // collection; we never mutate the Product here). The Product
        // aggregate owns the CustomerGroupPurchaseLimit value objects, so
        // GetByIdReadOnlyAsync includes them automatically.
        //
        // If the product doesn't exist (defensive — the handler should have
        // already validated this), return null (no limit).
        // ------------------------------------------------------------------
        var product = await _productRepository.GetByIdReadOnlyAsync(productId, cancellationToken);
        if (product is null)
        {
            _logger.LogWarning(
                "PurchaseLimitPolicy.GetCountLimitAsync: product {ProductId} not found. " +
                "Returning null (no limit). The caller should have validated product existence first.",
                productId);
            return null;
        }

        // Delegate to the Product aggregate's lookup method.
        var limitVo = product.GetPurchaseLimitForGroup(groupId.Value);
        return limitVo?.Limit;
    }

    /// <inheritdoc />
    public async Task<bool> IsCurrencyMatchAsync(Guid productId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Currency matching ALWAYS applies — regardless of LimitMode.
        // See IPurchaseLimitPolicy.IsCurrencyMatchAsync for rationale.
        //
        // Short-circuits:
        //   1. If groupId is null (staff), no currency constraint applies.
        //   2. If the group doesn't exist (defensive — shouldn't happen),
        //      return true (no constraint) — the handler will fail later
        //      at AddLineItem with a clearer error.
        // ------------------------------------------------------------------
        if (groupId is null)
        {
            return true;
        }

        var group = await _customerGroupRepository.GetByIdReadOnlyAsync(groupId.Value, cancellationToken);
        if (group is null)
        {
            _logger.LogWarning(
                "PurchaseLimitPolicy.IsCurrencyMatchAsync: customer group {GroupId} not found. " +
                "Returning true (no constraint) — handler will fail later at AddLineItem.",
                groupId);
            return true;
        }

        // ------------------------------------------------------------------
        // Load the product (AsNoTracking — we only READ its Price.Currency).
        // If the product doesn't exist, return true (no constraint) — the
        // handler will fail later with a clearer "product not found" error.
        // ------------------------------------------------------------------
        var product = await _productRepository.GetByIdReadOnlyAsync(productId, cancellationToken);
        if (product is null)
        {
            _logger.LogWarning(
                "PurchaseLimitPolicy.IsCurrencyMatchAsync: product {ProductId} not found. " +
                "Returning true (no constraint) — handler will fail later.",
                productId);
            return true;
        }

        // String equality on ISO 4217 currency codes (case-sensitive —
        // Money's constructor enforces uppercase). Both sides come from
        // Money value objects, so they're already normalized.
        return string.Equals(
            product.Price.Currency,
            group.Salary.Currency,
            StringComparison.Ordinal);
    }
}