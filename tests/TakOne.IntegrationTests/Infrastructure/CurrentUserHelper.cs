using TakOne.Application.Common.Interfaces;

namespace TakOne.IntegrationTests.Infrastructure;

/// <summary>
/// A real, code-only <see cref="ICurrentUserService"/> implementation used
/// by integration tests that need to drive the application-layer handlers
/// end-to-end against a real DB. NOT a mock — the handlers receive a
/// fully-functional instance whose properties are test-controlled.
/// </summary>
/// <remarks>
/// <b>WHY A REAL IMPLEMENTATION (vs. NSubstitute):</b>
/// The integration tests deliberately avoid mocking the persistence
/// collaborators (ApplicationDbContext, repositories, UnitOfWork). The
/// ICurrentUserService is the one collaborator that has NO persistence
/// representation — it's just ambient user-state. A real implementation
/// is simpler to reason about than a mock: there's no
/// "Did I configure the received call correctly?" indirection.
/// </remarks>
public sealed class CurrentUserHelper : ICurrentUserService
{
    private readonly HashSet<string> _roles;

    /// <summary>
    /// Creates a current user for the test. All properties are mutable
    /// after construction via their setters if a test needs to mutate
    /// state mid-test (e.g. simulate a role change).
    /// </summary>
    /// <param name="userId">The authenticated user's Id.</param>
    /// <param name="isAuthenticated">Whether the user is authenticated
    /// (useful for the auth-rejection-path tests).</param>
    /// <param name="fullName">Full name for audit/display.</param>
    /// <param name="groupId">Customer-group Id (null for staff).</param>
    /// <param name="roles">Identity roles to claim (e.g. Roles.Admin).</param>
    public CurrentUserHelper(
        Guid userId,
        bool isAuthenticated,
        string fullName = "Test User",
        Guid? groupId = null,
        params string[] roles)
    {
        UserId = userId;
        IsAuthenticated = isAuthenticated;
        FullName = fullName;
        GroupId = groupId;
        _roles = new HashSet<string>(roles, StringComparer.Ordinal);

        // WorkerId: the test convention is the first 8 hex chars of the
        // user's Guid (matches how the production WebUI's
        // CurrentUserService derives a worker-id from claims when the
        // claim isn't set). Stable, deterministic, easy to assert on.
        WorkerId = userId == Guid.Empty
            ? string.Empty
            : userId.ToString("N").Substring(0, 8);

        Gender = "Male";
    }

    /// <inheritdoc />
    public Guid UserId { get; set; }

    /// <inheritdoc />
    public string WorkerId { get; set; }

    /// <inheritdoc />
    public string FullName { get; set; }

    /// <inheritdoc />
    public Guid? GroupId { get; set; }

    /// <inheritdoc />
    public string? Gender { get; set; }

    /// <inheritdoc />
    public bool IsAuthenticated { get; set; }

    /// <inheritdoc />
    public bool IsInRole(string role)
    {
        // Simple set-contains check — no Identity abstractions involved.
        // Matches the production ICurrentUserService.IsInRole semantics
        // (case-sensitive role-name match against the claim set).
        return _roles.Contains(role);
    }

    /// <summary>
    /// Adds a role to this current user's claim set. Useful for tests
    /// that need to mutate the user mid-test (e.g. grant Admin to verify
    /// the broadcast handler accepts the elevated caller).
    /// </summary>
    public void AddRole(string role)
    {
        _roles.Add(role);
    }
}
