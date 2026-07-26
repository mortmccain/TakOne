// notificationHub.js — Phase 0.6
// JS bridge between the Blazor circuit and the server-side NotificationHub.
//
// Why a JS bridge at all (instead of calling the hub directly from C#)?
//   - Blazor Server CAN talk to a hub from C# via IHubContext, but that's
//     server-to-server. For real-time UI refresh, the BROWSER needs a
//     persistent SignalR connection. The Blazor circuit itself is already
//     a SignalR connection, but it's a private protocol — we can't
//     piggyback on it. We open a SECOND public SignalR connection to
//     /notificationHub.
//
// What this file exposes to Blazor:
//   window.notificationHub = {
//     start: async () => void,           // connect to the hub
//     onReceiveRefresh: (dotnetRef) => void,  // register a .NET callback
//     stop: () => void                   // disconnect (called on dispose)
//   }
//
// The DotNetObjectReference passed to onReceiveRefresh must expose a
// [JSInvokable] method that the hub's "ReceiveRefresh" event invokes.
// Typical pattern: a top-level component (MainLayout) injects this ref
// and refreshes its visible data when invoked.

let connection = null;
let dotnetCallbackRef = null;

window.notificationHub =
{
    start: async function () {
        if (connection) return;
        try {
            connection = new signalR.HubConnectionBuilder()
                .withUrl('/notificationHub')
                .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                .configureLogging(signalR.LogLevel.Information)
                .build();

            connection.on
                (
                    'ReceiveRefresh', function (payload)
                {
                        if (dotnetCallbackRef)
                        {
                        dotnetCallbackRef.invokeMethodAsync('OnReceiveRefresh', JSON.stringify(payload));
                        }
                }
                );

            await connection.start();
            console.info('[notificationHub] connected.');
        } catch (err) {
            console.warn('[notificationHub] connection failed:', err);
        }
    },

    onReceiveRefresh: function (dotnetRef)
    {
        dotnetCallbackRef = dotnetRef;
    },

    stop: function ()
    {
        if (connection)
        {
            connection.stop();
            connection = null;
            dotnetCallbackRef = null;
        }
    }
};