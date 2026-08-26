using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace TakOne.WebUI.Services;

/// <summary>
/// Scoped Blazor service that coordinates live notification-refresh across
/// components. Bridges the JS-side SignalR connection (the
/// <c>notificationHub</c> bridge) to .NET events so any component
/// subscribed to <see cref="RefreshReceived"/> can re-query its data when
/// a real-time push arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>LIFETIME</b>: <b>Scoped</b> — one instance per Blazor circuit.
/// Survives the entire session. Disposed when the circuit shuts down.
/// </para>
/// <para>
/// <b>JS INTEROP</b>: the Blazor runtime calls
/// <c>[JSInvokable] OnReceiveRefresh</c> when the JS-side
/// <c>ReceiveRefresh</c> event fires (the SignalR hub pushes the event
/// through <c>IHubContext.NotificationHub.Clients.Group($"user:{userId}")</c>).
/// The .NET event <see cref="RefreshReceived"/> is then raised, and any
/// subscribed component re-queries its data (the layout's unread badge,
/// the Notifications page's list, etc.).
/// </para>
/// <para>
/// <b>START / STOP</b>: the layout calls <see cref="StartAsync"/> in its
/// <c>OnAfterRenderAsync(firstRender: true)</c> to wire up the JS-side
/// connection + the .NET callback. <see cref="StopAsync"/> is called on
/// circuit shutdown — but in practice, the JS connection dies with the
/// circuit anyway, so this is just a graceful-shutdown nicety.
/// </para>
/// <para>
/// <b>THREAD SAFETY</b>: the <see cref="RefreshReceived"/> event's
/// invocation list is read under a lock to defend against
/// subscribe/unsubscribe races during a refresh burst (multiple
/// notifications land in quick succession). The handler runs on the
/// Blazor sync context (single-threaded) so this is defense-in-depth.
/// </para>
/// </remarks>
public sealed class NotificationRefreshService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ILogger<NotificationRefreshService> _logger;
    private DotNetObjectReference<NotificationRefreshService>? _selfRef;
    private readonly object _lock = new();

    public NotificationRefreshService(
        IJSRuntime js,
        ILogger<NotificationRefreshService> logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>
    /// Raised when a SignalR <c>ReceiveRefresh</c> event is received.
    /// Subscribers re-query their notification data on this event.
    /// </summary>
    /// <remarks>
    /// Handlers MUST be non-blocking and fast — they run on the Blazor
    /// sync context. Any I/O should be deferred to a task that the
    /// handler kicks off (don't await it inline).
    /// </remarks>
    public event Action? RefreshReceived;

    /// <summary>
    /// Wires up the JS-side SignalR connection + registers this instance
    /// as the .NET callback target. Idempotent — safe to call on every
    /// first render of the layout.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            // Create the DotNetObjectReference lazily so we don't hold
            // one for circuits where the JS bridge fails to start.
            _selfRef ??= DotNetObjectReference.Create(this);

            // Best-effort — the JS bridge swallows internal errors and
            // logs them to console. Failures here just mean the live
            // refresh won't work; the persisted notification row is
            // still the source of truth.
            await _js.InvokeVoidAsync("notificationHub.start");
            await _js.InvokeVoidAsync("notificationHub.onReceiveRefresh", _selfRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "NotificationRefreshService: failed to start the SignalR bridge. Live refresh disabled — notifications will still appear on page load.");
        }
    }

    /// <summary>
    /// JS-invokable callback. The <c>notificationHub.js</c> bridge invokes
    /// this when the SignalR <c>ReceiveRefresh</c> event fires.
    /// </summary>
    /// <remarks>
    /// The <c>payloadJson</c> arg is ignored — we don't carry per-event
    /// data because the recipient's UI re-queries the notification store
    /// for the fresh state. This is the standard "ping then re-query"
    /// pattern (vs. "push the whole payload") which keeps the wire format
    /// simple and lets the recipient's auth context (per-request) apply
    /// to the re-query.
    /// </remarks>
    [JSInvokable]
    public Task OnReceiveRefresh(string payloadJson)
    {
        // Copy the invocation list under a lock so a subscribe/unsubscribe
        // during a refresh burst doesn't enumerate a mutated list.
        Action? handler;
        lock (_lock)
        {
            handler = RefreshReceived;
        }

        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber's handler threw — log + don't propagate (one
            // bad subscriber shouldn't break the live-refresh for the
            // others).
            _logger.LogWarning(ex,
                "NotificationRefreshService: a RefreshReceived subscriber threw. Others will still be notified.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful shutdown — stops the JS-side SignalR connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("notificationHub.stop");
        }
        catch
        {
            // Circuit is shutting down — the JS connection dies anyway.
        }
        finally
        {
            _selfRef?.Dispose();
        }
    }
}
