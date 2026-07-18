using TakOne.Domain.Customers.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Customer>> GetBySpecificationAsync(
    Specification<Customer> specification,
    CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);
    // we don't delete customers we just deactivate them
}
