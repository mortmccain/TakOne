using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TakOne.WebUI.Hubs;

/// <summary>
/// SignalR hub for real-time UI refresh notifications.
/// </summary>
/// <remarks>
/// <para>
/// <b>GROUP MEMBERSHIP MODEL</b>: when a connection is established, the
/// hub reads the user's Id from the connection's claims (the
/// <c>ClaimTypes.NameIdentifier</c> claim, set by ASP.NET Identity at
/// login time) and adds the connection to a personal group named
/// <c>user:{userId}</c>. The <c>SignalRNotificationBroadcaster</c>
/// then targets <c>Clients.Group($"user:{userId}")</c> to ping all of
/// that user's currently-connected clients (multiple tabs, mobile + PC
/// — the broadcast goes to every live connection).
/// </para>
/// <para>
/// <b>WHY USER-GROUPS (not role-groups)</b>: per the user spec
/// ("make sure a customer doesn't get to see others creating a sale
/// or admins or managers or employees only see notifications for sales
/// they created or approved / invoiced"), each notification is targeted
/// at a specific user — not a role. So the broadcast must address the
/// specific user, not all admins. The user-group pattern delivers
/// exactly that — the broadcast only reaches the recipient.
/// </para>
/// <para>
/// <b>EVENT NAME</b>: <c>ReceiveRefresh</c>. The Blazor UI (via the
/// <c>notificationHub.js</c> bridge) subscribes to this event and, on
/// receipt, re-queries the notification store to pull fresh state.
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: only authenticated users may connect. The
/// <c>[Authorize]</c> attribute rejects anonymous connections.
/// </para>
/// </remarks>
[Authorize]
public sealed class NotificationHub : Hub
{
    private const string UserGroupPrefix = "user:";

    /// <summary>
    /// Called by SignalR when a new connection is established. Reads the
    /// user's Id from the connection's claims and adds the connection to
    /// the user's personal group.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            var groupName = $"{UserGroupPrefix}{userId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        }

        await base.OnConnectedAsync();
    }
}
