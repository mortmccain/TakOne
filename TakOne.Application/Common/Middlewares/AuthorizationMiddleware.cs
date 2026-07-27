using System.Reflection;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Wolverine middleware that runs BEFORE each command handler. If the command
/// is decorated with <see cref="RequireRolesAttribute"/>, checks whether the
/// current user is in at least one of the listed roles. If not, short-circuits
/// the pipeline and returns a failed Result.
///
/// The middleware is opt-in per command via the attribute -- commands without
/// the attribute skip the role check entirely.
/// </summary>
/// <remarks>
/// WOLVERINE MIDDLEWARE PARAMETER CONVENTION (CRITICAL):
///   The <c>Before</c> method's parameter MUST be <c>Envelope envelope</c>
///   (or a concrete message type, or <c>CancellationToken</c>, or services
///   from DI). It MUST NOT be <c>object message</c>.
///
///   If you use <c>object message</c>, Wolverine 6.x's code generator gets
///   confused and generates broken code:
///
///     <code>
///       var result_of_Before = authorizationMiddleware.Before(result_of_Before);
///     </code>
///
///   It passes the <c>result_of_Before</c> variable (which is being declared
///   on this very line) as the <c>message</c> argument -- a circular
///   reference. The generated code fails to compile with:
///
///     <c>CS0841: Cannot use local variable 'result_of_Before' before it is
///     declared</c>
///
///   This is because Wolverine treats the return value of <c>Before</c> as
///   something to "chain" to the next middleware, and when the parameter is
///   <c>object</c> it can't infer a concrete message variable to pass -- so
///   it falls back to the result variable.
///
///   Using <c>Envelope envelope</c> (the same pattern as
///   <see cref="LoggingMiddleware"/> and <see cref="PerformanceMiddleware"/>)
///   gives Wolverine a concrete, well-known parameter to pass
///   (<c>context.Envelope</c>), and we read the message from
///   <c>envelope.Message</c> inside the method.
/// </remarks>
public class AuthorizationMiddleware
{
    private readonly ICurrentUserService _currentUser;

    public AuthorizationMiddleware(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>
    /// Wolverine convention: a method named <c>Before</c> (or
    /// <c>BeforeAsync</c>) runs before the handler. Returning a non-null
    /// value short-circuits the pipeline and that value becomes the
    /// handler's return value.
    /// </summary>
    /// <param name="envelope">
    /// The Wolverine envelope containing the message being handled. We read
    /// <c>envelope.Message</c> to get the command/query object.
    /// </param>
    /// <returns>
    /// <c>null</c> to continue to the handler, or a
    /// <see cref="Result"/> failure to short-circuit.
    /// </returns>
    public object? Before(Envelope envelope)
    {
        var message = envelope.Message;
        if (message is null)
            return null; // Nothing to authorize -- let the handler deal with it.

        var messageType = message.GetType();

        var attr = messageType.GetCustomAttribute<RequireRolesAttribute>();
        if (attr is null)
            return null; // No role requirement -- let the pipeline continue.

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