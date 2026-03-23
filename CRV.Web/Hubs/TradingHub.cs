namespace CRV.Web.Hubs;

using Microsoft.AspNetCore.SignalR;

// ─────────────────────────────────────────────────────────────
// TradingHub — SignalR hub for real-time dashboard updates
// Clients call: connection.on("Update", data => {...})
//
// Clients can join named groups to receive targeted messages:
//   await connection.invoke("JoinGroup", "dashboard");
//   await connection.invoke("JoinGroup", "alerts");
// ─────────────────────────────────────────────────────────────

public class TradingHub : Hub
{
    // Client-callable: subscribe to a named group
    public async Task JoinGroup(string groupName)
        => await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

    // Client-callable: unsubscribe from a named group
    public async Task LeaveGroup(string groupName)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

    // Server → All clients: push full dashboard state
    public static async Task PushUpdateAsync(IHubContext<TradingHub> hub, object state)
        => await hub.Clients.All.SendAsync("Update", state);

    // Server → All clients: push a new alert log entry
    public static async Task PushAlertAsync(IHubContext<TradingHub> hub, object alert)
        => await hub.Clients.All.SendAsync("Alert", alert);
}
