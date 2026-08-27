using TakOne.Domain.Customers.Entities;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Read/write repository for the <see cref="CustomerGroup"/> aggregate.
///
/// Used by the Manage Groups page (read all, by Id, by name) and by
/// the new-group flow (write). Lookups by Id are the hot path — they're
/// called from <c>IPurchaseLimitPolicy</c> on every cart mutation (to
/// resolve the customer's salary + currency + active status).
///
/// TRACKING POLICY:
///   Read methods return TRACKED entities when the caller may mutate them
///   (e.g. <see cref="GetByIdAsync"/> for rename / update-salary). For
///   pure read-only paths (the policy check), the caller should use
///   <see cref="GetByIdReadOnlyAsync"/> to avoid polluting the change
///   tracker with entities we'll never SaveChanges.
/// </summary>
public interface ICustomerGroupRepository
{
    /// <summary>
    /// Loads a customer group by Id, tracked. Used by command handlers
    /// that will mutate the group (rename, update salary, activate,
    /// deactivate).
    /// </summary>
    Task<CustomerGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a customer group by Id, NOT tracked (AsNoTracking). Used by
    /// read-only callers — most importantly <c>IPurchaseLimitPolicy</c>,
    /// which only needs the salary/currency/active fields and never
    /// mutates the group.
    /// </summary>
    Task<CustomerGroup?> GetByIdReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch read-only load of customer groups by Id, AsNoTracking. Used by
    /// <c>GetBroadcastNotificationsQueryHandler</c> to resolve target-group
    /// names for a page of audit rows in a single round-trip (instead of one
    /// <c>GetByIdReadOnlyAsync</c> call per row). Empty input returns an empty
    /// list without hitting the DB; missing Ids are simply absent from the
    /// result.
    /// </summary>
    Task<List<CustomerGroup>> GetByIdsReadOnlyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all customer groups, ordered by Name. Used by the Manage
    /// Groups page. The list is expected to be small (5-15 rows), so no
    /// pagination. Optionally include inactive groups (default: include).
    /// </summary>
    Task<List<CustomerGroup>> GetAllAsync(
        bool includeInactive = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if a group with the given name already exists.
    /// Used by the create-group command validator to enforce uniqueness
    /// BEFORE attempting to create the group.
    /// </summary>
    /// <param name="excludeId">
    /// Optional group Id to exclude from the check — used when renaming
    /// (we want to know if ANOTHER group has the target name, not the
    /// group being renamed).
    /// </param>
    Task<bool> NameExistsAsync(
        string name,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of active users currently assigned to the given
    /// group. Used by the delete/deactivate-group flow to warn the admin
    /// how many users will be affected.
    /// </summary>
    Task<int> GetActiveUserCountAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new group to the DbContext. Does NOT commit — the caller
    /// controls the transaction via <c>IUnitOfWork.SaveChangesAsync</c>.
    /// </summary>
    Task AddAsync(CustomerGroup group, CancellationToken cancellationToken = default);
}