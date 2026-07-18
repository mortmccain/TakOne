using TakOne.Application.Users.DTOs;

namespace TakOne.Application.Common.Interfaces;

public interface IUserRepository
{
    Task<List<UserDto>> GetAllUsersAsync(CancellationToken cancellationToken = default);
    Task<UserDto?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string username, string? email, string? fullName, string password, string role, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}