using TakOne.Domain.Sales.ValueObjects;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Generates a unique, sequential <see cref="SaleNumber"/> for a new Sale.
/// Implementations should be backed by a database sequence or row-level locking
/// so that concurrent sale creations never produce the same SaleNumber.
/// </summary>
public interface ISaleNumberGenerator
{
    Task<SaleNumber> NextAsync(string prefix, CancellationToken cancellationToken = default);
}
