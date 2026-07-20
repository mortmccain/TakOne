using System.Reflection;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;
using Wolverine.Middleware;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Wolverine middleware that runs BEFORE each command handler. If the command
/// is decorated with <see cref="RequireRolesAttribute"/>, checks whether the
/// current user is in at least one of the listed roles. If not, short-circuits
/// the pipeline and returns a failed Result.
///
/// The middleware is opt-in per command via the attribute — commands without
/// the attribute skip the role check entirely.
/// </summary>
public class AuthorizationMiddleware
{
    private readonly ICurrentUserService _currentUser;

    public AuthorizationMiddleware(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>
    /// Wolverine convention: a method named (Before|BeforeAsync) with parameters
    /// matching the message and context runs before the handler.
    /// Returning a non-null value short-circuits the pipeline and that value
    /// becomes the handler's return value.
    /// </summary>
    public object? Before(object message)
    {
        var messageType = message.GetType();

        var attr = messageType.GetCustomAttribute<RequireRolesAttribute>();
        if (attr is null)
            return null; // No role requirement — let the pipeline continue.

        if (!_currentUser.IsAuthenticated)
            return Result.Failure("Authentication required.");

        // User must be in AT LEAST ONE of the required roles.
        bool allowed = attr.Roles.Any(r => _currentUser.IsInRole(r));
        if (!allowed)
            return Result.Failure(
                $"You do not have permission to perform this action. " +
                $"Required role(s): {string.Join(", ", attr.Roles)}.");

        return null; // Continue to the handler.
    }
}
