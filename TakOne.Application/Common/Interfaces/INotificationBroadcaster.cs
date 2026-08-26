namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Abstraction over the real-time push channel (SignalR) for notifying
/// a user's UI that a new notification has been persisted.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN INTERFACE (not <c>IHubContext</c> directly)</b>: the
/// Application layer must NOT reference <c>Microsoft.AspNetCore.SignalR</c>
/// (clean architecture — Application stays UI-tech-agnostic). The
/// interface lives here; the SignalR-backed implementation lives in WebUI
/// (where the <c>NotificationHub</c> is defined) and is registered in
/// <c>Program.cs</c>. Wolverine resolves the implementation via DI
/// whenever a handler asks for <see cref="INotificationBroadcaster"/>.
/// </para>
/// <para>
/// <b>WHY THE BROADCAST IS A BEST-EFFORT FIRE-AND-FORGET</b>: the
/// persisted <c>Notification</c> row is the source of truth — if the
/// real-time push fails (user's circuit is down, hub is restarting,
/// network blip), the user simply sees the notification on next page
/// load. The broadcast is a UX nicety, not a correctness requirement.
/// </para>
/// <para>
/// <b>TRANSACTIONAL INVARIANT</b>: callers should only invoke
/// <see cref="BroadcastToUserAsync"/> from within a Wolverine handler
/// that is enrolled in the same EF Core transaction as the business
/// mutation. Wolverine's transactional outbox ensures the broadcast
/// message is only DELIVERED to the SignalR handler after the
/// surrounding SaveChangesAsync commits — so the user never receives
/// a real-time ping for a notification whose business mutation rolled
/// back.
/// </para>
/// </remarks>
public interface INotificationBroadcaster
{
    /// <summary>
    /// Push a "ReceiveRefresh" SignalR message to all of the recipient
    /// user's currently-connected clients. The Blazor UI subscribes to
    /// "ReceiveRefresh" and, on receipt, re-queries the notification
    /// store for the new state.
    /// </summary>
    /// <param name="userId">The recipient's user Id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task BroadcastToUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
