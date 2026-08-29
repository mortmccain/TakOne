using System.Runtime.Serialization;

namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Thrown by <see cref="Middlewares.AuthorizationMiddleware"/> when a Wolverine
/// message (command/query) fails its authorization policy: the caller is not
/// authenticated, is missing a required role, or the message type carries no
/// authorization attribute at all (fail-closed).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN EXCEPTION (and not a <c>Result</c> return)?</b>
/// </para>
/// <para>
/// This follows the exact pattern of Wolverine's own FluentValidation
/// integration: its middleware's default
/// <c>IFailureAction&lt;T&gt;</c> THROWS a <see cref="FluentValidation.ValidationException"/>
/// on validation failure, and that exception propagates cleanly to the
/// <c>InvokeAsync</c> caller (Blazor pages already catch it and surface a
/// localized toast). Returning a <c>Result</c> from a Wolverine middleware
/// <c>Before</c> method does NOT short-circuit the pipeline unless the return
/// type is registered via <c>UseResultType&lt;T&gt;</c> — and even then,
/// short-circuited <c>InvokeAsync&lt;Result&lt;T&gt;&gt;</c> calls come back as
/// <c>null</c>, causing NullReferenceExceptions in every calling page.
/// Throwing is the only mechanism that (a) reliably stops the handler from
/// running and (b) preserves a meaningful payload for the caller.
/// </para>
/// <para>
/// <b>WHO CATCHES IT:</b> Razor pages/interactive components that dispatch
/// commands via <c>IMessageBus.InvokeAsync</c> wrap dispatches in try/catch
/// (the same blocks that handle <c>ValidationException</c>); the exception's
/// <see cref="Exception.Message"/> is a user-presentable, non-sensitive
/// denial reason. In the normal application flow this exception should never
/// fire for a legitimate user — page-level <c>[Authorize(Policy=...)]</c>
/// policies are aligned with each command's roles; this middleware is the
/// fail-closed DEFENSE-IN-DEPTH layer for dispatch paths that bypass page
/// authorization (tampered circuits, future HTTP endpoints, background jobs
/// dispatching user commands without a user context).
/// </para>
/// <para>
/// <b>SECURITY NOTE:</b> the message deliberately does NOT echo back any
/// information the caller didn't already supply. Role names listed in the
/// denial are the message's own declared policy (public metadata on the
/// message type), not the caller's role inventory.
/// </para>
/// </remarks>
[Serializable]
public sealed class MessageAuthorizationException : Exception
{
    /// <summary>
    /// Creates a denial exception with a user-presentable reason.
    /// </summary>
    public MessageAuthorizationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a denial exception with a reason and an inner cause
    /// (used when authorization context resolution itself failed).
    /// </summary>
    public MessageAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

#pragma warning disable SYSLIB0051 // Legacy serialization support — kept for netstandard-compatible callers
    /// <summary>
    /// Serialization constructor (required for the [Serializable] contract).
    /// </summary>
    private MessageAuthorizationException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
#pragma warning restore SYSLIB0051
}
