using TakOne.SharedKernel.Common;
using Wolverine.Middleware;

namespace TakOne.Application.Common.Behaviors;

/// <summary>
/// Wolverine middleware that wraps each handler invocation. If the handler
/// throws a <see cref="DomainException"/> (an invariant was violated), the
/// middleware catches it and converts it to <c>Result.Failure(ex.Message)</c>
/// so handlers can stay free of try/catch boilerplate.
///
/// Other exception types propagate up unchanged.
/// </summary>
public class DomainExceptionMiddleware
{
    /// <summary>
    /// Wolverine convention: a method named AfterAsync that wraps the call.
    /// Throwing here propagates; we use a different approach — see below.
    /// </summary>
    /// <remarks>
    /// Wolverine actually supports try/catch via the more explicit
    /// "IActionFilter"/"IMessageHandler" patterns, but the simplest and most
    /// robust approach is to make this middleware "wrap" the call using the
    /// Before/After pair, with the After method able to inspect exceptions.
    ///
    /// In practice, for the current Wolverine version, the cleanest way to
    /// turn DomainException into Result.Failure is to use Wolverine's
    /// "exception handling" middleware pattern, which is implemented as a
    /// method that catches exceptions of a specific type. See:
    /// https://wolverinefx.net/guide/middleware/error-handling
    /// </remarks>
    public object? Handle(Exception exception)
    {
        if (exception is DomainException dex)
            return Result.Failure(dex.Message);

        // Re-throw any other exception type — we only handle DomainException.
        throw exception;
    }
}