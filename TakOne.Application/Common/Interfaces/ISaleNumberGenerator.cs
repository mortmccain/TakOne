using TakOne.Domain.Sales.ValueObjects;

namespace TakOne.Application.Common.Interfaces;

public interface ISaleNumberGenerator
{
    Task<SaleNumber> NextAsync(string prefix, CancellationToken cancellationToken = default);
}