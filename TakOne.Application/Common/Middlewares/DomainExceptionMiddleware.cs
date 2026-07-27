using Microsoft.Extensions.Logging;
using TakOne.SharedKernel.Common;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Wolverine middleware that runs AFTER each handler invocation via the
/// <c>FinallyAsync</c> convention. Currently used only for logging
/// <see cref="DomainException"/> instances that propagate out of handlers.
///
/// WHY THIS IS NOT REGISTERED IN THE PIPELINE:
///   In Wolverine 6.x, middleware methods must be named
///   Before/BeforeAsync/After/AfterAsync/Finally/FinallyAsync. The
///   Finally/FinallyAsync methods CAN receive an Exception parameter, but
///   they CANNOT return a value to replace the handler's output. That means
///   we cannot do "catch DomainException -> return Result.Failure(message)"
///   in middleware the way the original design intended.
///
///   Handlers that call aggregate methods which may throw DomainException
///   (e.g. <c>sale.AddLineItem</c>) should wrap those calls in a
///   <c>try/catch</c> and return <c>Result.Failure</c> themselves. This is
///   the recommended Wolverine 6.x pattern for exception-to-Result
///   conversion -- see https://wolverinefx.net/guide/handlers/error-handling.
///
///   This class is kept (as valid Wolverine middleware using FinallyAsync)
///   so it can be re-enabled later if either:
///     - Wolverine adds support for exception-to-Result conversion in
///       middleware, OR
///     - We decide we want DomainException logging as a cross-cutting
///       concern (just register it via
///       <c>opts.Policies.AddMiddleware&lt;DomainExceptionMiddleware&gt;()</c>).
///
/// WOLVERINE MIDDLEWARE CONVENTION:
///   Wolverine recognizes method names Before/BeforeAsync/After/AfterAsync/
///   Finally/FinallyAsync by case-sensitive convention. NO attribute or
///   interface is required on the middleware class itself. The method
///   signature can include any parameters Wolverine knows how to provide
///   (Envelope, CancellationToken, Exception, IMessageContext, etc.).
///
///   <c>FinallyAsync</c> runs AFTER the handler completes, whether the
///   handler succeeded or threw. If the handler threw, the
///   <c>exception</c> parameter is non-null. Returning from
///   <c>FinallyAsync</c> does NOT suppress the exception -- Wolverine
///   re-throws it after the method returns. To suppress, you'd need to use
///   a different pattern (custom IMessageHandler, [WrapExceptions], etc.).
/// </summary>
public class DomainExceptionMiddleware
{
    private readonly ILogger<DomainExceptionMiddleware> _logger;

    public DomainExceptionMiddleware(ILogger<DomainExceptionMiddleware> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Wolverine FinallyAsync convention. Runs after the handler completes
    /// (success or exception). If the handler threw a
    /// <see cref="DomainException"/>, logs it at Warning level so we have
    /// an audit trail of domain invariant violations.
    ///
    /// This method does NOT convert the exception to a Result value --
    /// Wolverine 6.x FinallyAsync cannot return a value to replace the
    /// handler's output. The exception will propagate to the caller
    /// (Wolverine message bus -> Blazor component) unless the handler
    /// itself catches it and returns Result.Failure.
    /// </summary>
    /// <param name="envelope">
    /// The Wolverine envelope containing the message being handled. Used
    /// here to log the message type for diagnostic context.
    /// </param>
    /// <param name="exception">
    /// The exception thrown by the handler, or null if the handler
    /// completed successfully.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the handler invocation.
    /// </param>
    public Task FinallyAsync(Envelope envelope, Exception? exception, CancellationToken cancellationToken)
    {
        if (exception is DomainException dex)
        {
            var requestName = envelope.Message?.GetType().Name ?? "Unknown";
            _logger.LogWarning(
                "Domain invariant violated in {RequestName}: {Message}",
                requestName,
                dex.Message);
        }

        return Task.CompletedTask;
    }
}