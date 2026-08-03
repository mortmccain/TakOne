using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
///
/// RESPONSIBILITY:
///   Provide the single <c>SaveChangesAsync</c> entry point that the
///   Application layer calls to commit ALL changes in a use case. By routing
///   every repository write through one DbContext and one SaveChanges, we
///   guarantee atomicity: either all changes commit together, or none do.
///
/// WHY A THIN WRAPPER:
///   The interface looks trivial (one method) — why not just inject
///   <c>ApplicationDbContext</c> directly into handlers? Three reasons:
///
///   1. DEPENDENCY INVERSION. The Application layer must NOT reference EF Core
///      or <c>ApplicationDbContext</c>. Exposing the DbContext would force the
///      Application project to take a project reference on Infrastructure,
///      which inverts the dependency direction and breaks the onion.
///
///   2. TESTABILITY. Handler unit tests can substitute a fake
///      <c>IUnitOfWork</c> that records SaveChanges calls without committing
///      to a real database. Mocking an interface is one line; mocking
///      <c>DbContext.SaveChangesAsync</c> is several (you have to mock the
///      DbSet, the ChangeTracker, etc.).
///
///   3. FUTURE HOOKS. The day we want to add cross-cutting concerns to
///      SaveChanges — domain-event dispatch (already planned for step 7e via
///      an <c>ISaveChangesInterceptor</c>), outbox message capture, audit
///      logging, optimistic-concurrency retries — there's exactly one place
///      to add them. If every handler called <c>DbContext.SaveChangesAsync</c>
///      directly, every handler would need updating.
///
/// INTERCEPTORS:
///   Domain-event dispatch and any other SaveChanges-side behaviors are
///   implemented as an <c>ISaveChangesInterceptor</c> registered on the
///   DbContext options (step 7e), NOT here. The interceptor runs inside EF
///   Core's SavingChanges event, BEFORE the actual SQL is issued — so it can
///   collect domain events from aggregates and enqueue them into Wolverine's
///   outbox as part of the same transaction.
///
///   This keeps <c>UnitOfWork</c> trivially simple — it's literally
///   <c>_db.SaveChangesAsync</c>. The complexity lives in the interceptor,
///   where it belongs.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _db;

    public UnitOfWork(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned <c>int</c> is the number of state entries written to the
    /// database. Handlers typically ignore it — they care about success vs.
    /// failure, not the row count. We surface it anyway for parity with EF
    /// Core's signature and for the rare case where a handler needs to verify
    /// "at least one row was affected" (defensive against a no-op bug).
    /// </remarks>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implements <c>ChangeTracker.Clear()</c> — detaches every tracked entity
    /// in one call. This is the standard remedy for the Blazor Server scoped-
    /// DbContext stale-tracking issue described on
    /// <see cref="IUnitOfWork.ClearChangeTracker"/>.
    /// </remarks>
    public void ClearChangeTracker()
    {
        _db.ChangeTracker.Clear();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <c>DbContext.Add(object)</c>, which starts tracking the
    /// entity in the <c>Added</c> state. The entity type must be registered
    /// on the DbContext (via DbSet or <c>ApplyConfigurationsFromAssembly</c>)
    /// — otherwise EF Core throws at runtime.
    ///
    /// Why not <c>AddAsync</c>? <c>AddAsync</c> is only needed when the
    /// entity's key generation strategy is DB-side (e.g. SQL Server IDENTITY).
    /// Our entities use client-side <c>Guid.NewGuid()</c> keys, so the
    /// synchronous <c>Add</c> is correct and slightly cheaper.
    /// </remarks>
    public void AddEntity(object entity)
    {
        _db.Add(entity);
    }
}