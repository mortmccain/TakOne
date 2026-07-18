namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Coordinates persistence across multiple repositories.
/// Ensures all changes in a single use case are saved atomically.
/// </summary>
public interface IUnitOfWork
{
    Task AddAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class;
    Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T?> GetByIdAsync<T>(Guid id,
        IEnumerable<string>? includes = null,
        CancellationToken cancellationToken = default) where T : class;
}