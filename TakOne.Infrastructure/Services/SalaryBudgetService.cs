using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.Domain.Common;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Infrastructure implementation of <see cref="ISalaryBudgetService"/>.
///
/// Orchestrates the monthly salary budget computation:
///   1. Resolve the customer's group (via <c>IUserRepository</c>) →
///      CustomerGroup.Salary (Money: amount + ISO currency).
///   2. If the system's LimitMode doesn't enforce salary budget
///      (mode = CountOnly), return null — no point computing consumed
///      amount when the budget doesn't apply.
///   3. Compute the window's start/end (UTC DateTime of 1st of current
///      Persian month + 1st of next Persian month) via the pure
///      <c>SalaryBudgetWindow</c> helper.
///   4. Query the consumed amount in that window via
///      <c>ISaleRepository.GetConsumedAmountForCustomerInWindowAsync</c>.
///   5. Return a <see cref="SalaryBudgetInfo"/> snapshot.
///
/// SEE <see cref="ISalaryBudgetService"/> for the full policy rationale
/// (monthly reset, cart-reserves-budget, use-it-or-lose-it, submit-is-noop).
/// </summary>
public sealed class SalaryBudgetService : ISalaryBudgetService
{
    private readonly IUserRepository _userRepository;
    private readonly ICustomerGroupRepository _customerGroupRepository;
    private readonly ISaleRepository _saleRepository;
    private readonly ISystemSettingsService _systemSettings;
    private readonly ILogger<SalaryBudgetService> _logger;

    public SalaryBudgetService(
        IUserRepository userRepository,
        ICustomerGroupRepository customerGroupRepository,
        ISaleRepository saleRepository,
        ISystemSettingsService systemSettings,
        ILogger<SalaryBudgetService> logger)
    {
        _userRepository = userRepository;
        _customerGroupRepository = customerGroupRepository;
        _saleRepository = saleRepository;
        _systemSettings = systemSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SalaryBudgetInfo?> GetBudgetInfoAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        if (customerId == Guid.Empty)
        {
            return null;
        }

        // ------------------------------------------------------------------
        // 1. Load the customer's User row to resolve their GroupId.
        //    We use GetByIdAsync (tracked) — but we never mutate the User
        //    here, so this is effectively a read. (AsNoTracking isn't
        //    available on IUserRepository.GetByIdAsync, but the cost is
        //    negligible — one User row cached in the change tracker.)
        // ------------------------------------------------------------------
        var user = await _userRepository.GetByIdAsync(customerId, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning(
                "SalaryBudgetService.GetBudgetInfoAsync: user {CustomerId} not found.",
                customerId);
            return null;
        }

        if (user.GroupId is null)
        {
            // Staff user — no group, no salary, no budget.
            return null;
        }

        // ------------------------------------------------------------------
        // 2. Load the customer's group to resolve their Salary.
        //    GetByIdReadOnlyAsync (AsNoTracking) — we never mutate the
        //    CustomerGroup here.
        // ------------------------------------------------------------------
        var group = await _customerGroupRepository.GetByIdReadOnlyAsync(user.GroupId.Value, cancellationToken);
        if (group is null)
        {
            // Defensive — User.GroupId is a FK, so the group should always
            // exist. If it's been hard-deleted (shouldn't happen since
            // CustomerGroup uses soft-delete via IsActive=false), treat
            // the customer as having no budget.
            _logger.LogWarning(
                "SalaryBudgetService.GetBudgetInfoAsync: customer {CustomerId} has GroupId {GroupId} " +
                "but the group was not found in the repository.",
                customerId, user.GroupId);
            return null;
        }

        // ------------------------------------------------------------------
        // 3. Check whether salary budget is enforced. If mode = CountOnly,
        //    return null — the UI hides the CartBudgetBar in that case,
        //    and the sale-mutating handlers skip the salary check.
        // ------------------------------------------------------------------
        var mode = await _systemSettings.GetLimitModeAsync(cancellationToken);
        if (mode == Domain.Common.Enums.LimitMode.CountOnly)
        {
            return null;
        }

        // ------------------------------------------------------------------
        // 4. Compute the window's start/end (UTC DateTime).
        //    Persian-calendar monthly window — see SalaryBudgetWindow for
        //    the rationale (code constant, not a DB column).
        // ------------------------------------------------------------------
        var nowUtc = DateTime.UtcNow;
        var windowStartUtc = SalaryBudgetWindow.GetStartOfCurrentMonth(nowUtc);
        var windowEndUtc = SalaryBudgetWindow.GetStartOfNextMonth(nowUtc);

        // ------------------------------------------------------------------
        // 5. Query the consumed amount (single round-trip via
        //    ISaleRepository). Includes draft cart total + submitted
        //    non-cancelled sales in [windowStartUtc, windowEndUtc).
        // ------------------------------------------------------------------
        var consumed = await _saleRepository.GetConsumedAmountForCustomerInWindowAsync(
            customerId,
            windowStartUtc,
            windowEndUtc,
            cancellationToken);

        // ------------------------------------------------------------------
        // 6. Build the snapshot. Remaining can be negative (salary was
        //    lowered mid-month after a purchase was made) — that's a
        //    valid state; the caller will block any further additions.
        // ------------------------------------------------------------------
        return new SalaryBudgetInfo
        {
            Salary = group.Salary,
            Consumed = consumed,
            Remaining = group.Salary.Amount - consumed,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowEndUtc
        };
    }

    /// <inheritdoc />
    public async Task<Money?> GetGroupSalaryAsync(Guid? groupId, CancellationToken cancellationToken = default)
    {
        if (groupId is null)
        {
            return null;
        }

        var group = await _customerGroupRepository.GetByIdReadOnlyAsync(groupId.Value, cancellationToken);
        return group?.Salary;
    }
}