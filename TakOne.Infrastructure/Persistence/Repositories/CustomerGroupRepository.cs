using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Customers.Entities;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICustomerGroupRepository"/>.
///
/// TRACKING POLICY:
///   <see cref="GetByIdAsync"/> and <see cref="GetByNameAsync"/> return
///   TRACKED entities (the caller may mutate them — rename, update
///   salary, activate, deactivate). <see cref="GetByIdReadOnlyAsync"/>
///   returns an UNTRACKED entity for read-only paths (the policy check),
///   to avoid polluting the change tracker with entities we'll never
///   SaveChanges.
///
/// MONEY VALUE OBJECT AUTO-LOADED:
///   <c>CustomerGroup.Salary</c> is a ComplexProperty, which means EF Core
///   loads it automatically whenever the parent is materialized — no
///   <c>.Include()</c> needed. Same pattern as <c>Product.Price</c>.
///
/// LIFETIME:
///   Scoped — same as the other repositories. Each handler invocation
///   gets a fresh DbContext + fresh repository instances, disposed
///   together at the end of the scope.
/// </summary>
public sealed class CustomerGroupRepository : ICustomerGroupRepository
{
    private readonly ApplicationDbContext _db;

    public CustomerGroupRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<CustomerGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // FindAsync uses the PK and consults the change tracker first.
        // Salary (complex property) auto-loads.
        return await _db.CustomerGroups.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CustomerGroup?> GetByIdReadOnlyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // AsNoTracking — for read-only paths (the policy check). FirstOrDefaultAsync
        // because FindAsync always returns a tracked entity (no AsNoTracking overload).
        return await _db.CustomerGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CustomerGroup?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // SingleOrDefaultAsync because Name has a unique index — at most one match.
        // Throws if more than one (defensive against a dropped/missing unique index).
        return await _db.CustomerGroups
            .SingleOrDefaultAsync(g => g.Name == name, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<CustomerGroup>> GetAllAsync(
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        // Groups are expected to be a small list (5-15 rows), so no pagination.
        // Order by Name for stable UI rendering.
        //
        // The includeInactive filter is the only filter — the Manage Groups page
        // shows all groups (active + inactive), with active groups at the top.
        var query = _db.CustomerGroups.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(g => g.IsActive);
        }

        return await query
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(
        string name,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default)
    {
        // excludeId is used when renaming — we want to know if ANOTHER group
        // (not the one being renamed) already has this name. Null excludeId
        // means "any group with this name" (used by CreateCustomerGroup).
        if (excludeId is null)
        {
            return await _db.CustomerGroups.AnyAsync(g => g.Name == name, cancellationToken);
        }

        var excludedId = excludeId.Value;
        return await _db.CustomerGroups
            .AnyAsync(g => g.Name == name && g.Id != excludedId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetActiveUserCountAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        // Count active users currently assigned to the group. Used by the
        // delete/deactivate-group flow to warn the admin how many users will
        // be affected.
        //
        // Joins DomainUsers (Users table) on GroupId. No role check needed —
        // staff users have GroupId = null and won't match.
        return await _db.DomainUsers
            .CountAsync(u => u.GroupId == groupId && u.IsActive, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(CustomerGroup group, CancellationToken cancellationToken = default)
    {
        // AddAsync queues the entity for INSERT on the next SaveChangesAsync.
        // Does NOT hit the DB — the IUnitOfWork controls the commit.
        // Salary (complex property) is added automatically as part of the
        // CustomerGroups row.
        await _db.CustomerGroups.AddAsync(group, cancellationToken);
    }
}