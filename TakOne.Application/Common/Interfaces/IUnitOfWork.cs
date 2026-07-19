namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Coordinates persistence across multiple repositories.
/// Ensures all changes in a single use case are saved atomically.
///
/// Why no Update method?
///   EF Core tracks changes to entities loaded from the database. When a handler
///   loads an aggregate, modifies it, and calls SaveChangesAsync(), EF Core
///   automatically detects the changes and generates UPDATE statements.
///   An explicit Update method is redundant.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
