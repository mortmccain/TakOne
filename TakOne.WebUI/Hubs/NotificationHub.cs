using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TakOne.WebUI.Hubs;

/// <summary>
/// SignalR hub for real-time UI refresh notifications. Phase 0.13.
/// </summary>
/// <remarks>
/// <para>
/// The hub itself is intentionally empty. Per roadmap concern H, the
/// broadcast pattern is: an Application-layer event handler (running
/// inside Wolverine's transactional outbox) calls
/// <c>IHubContext&lt;NotificationHub&gt;.Clients.Group(role).SendAsync("ReceiveRefresh", payload)</c>
/// to ping only the users who care about that event.
/// </para>
/// <para>
/// <b>Group membership:</b> when a Blazor circuit connects, the JS bridge
/// (notificationHub.js) opens a SECOND SignalR connection to this hub.
/// A future OnConnectedAsync override will read the user's role from the
/// connection's claims and call <c>Groups.AddToGroupAsync</c> so the
/// user is in the right role-group(s). For Phase 0, we leave the hub
/// empty — actual broadcasts start in Phase 2 (Dashboard).
/// </para>
/// <para>
/// <b>Authorization:</b> only authenticated users may connect. Anonymous
/// connections are rejected by the [Authorize] attribute.
/// </para>
/// </remarks>
[Authorize]
public sealed class NotificationHub : Hub
{
    // Intentionally empty for Phase 0.
    //
    // Future methods (added when Phase 2 needs them):
    //   public override async Task OnConnectedAsync()
    //     - Read Context.User?.FindFirst(ClaimTypes.Role) and add the
    //       connection to the matching role-group(s).
    //   public Task JoinGroup(string groupName)
    //     - For arbitrary grouping (e.g. group-by-customer-group-id).
}