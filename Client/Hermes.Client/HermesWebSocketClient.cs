using System.Net.WebSockets;
using System.Text;
using Hermes.Client.Models;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

/// <summary>
/// HERMES's own WebSocket connection to the server (separate from EFT's built-in notifier
/// connection). Replaces the old timer-driven Assistant-alert poll: the server pushes a
/// "notification-update" event with the current Assistant alert feed only when something actually
/// changed, and this class just delivers it to whoever is listening. Workspace tab data
/// (Hideout/Crafts/Stash/Loadout) is never pushed over this connection; it is fetched only when
/// the player opens HERMES or switches tabs. There is no HTTP polling fallback if the socket is
/// down — instead it reconnects with backoff and asks the server for a resync as soon as it's back.
/// </summary>
internal static class HermesWebSocketClient
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private static CancellationTokenSource? _lifetime;
    private static bool _running;
    private static ClientWebSocket? _activeSocket;

    /// <summary>Raised on a background thread whenever the server pushes a notification-update event.</summary>
    internal static event Action<HermesAssistantAlertsResponse>? NotificationsUpdated;

    /// <summary>True while the socket is currently connected (not just started/reconnecting).</summary>
    internal static bool IsConnected { get; private set; }

    internal static void Start()
    {
        lock (Sync)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _lifetime = new CancellationTokenSource();
            _ = RunAsync(_lifetime.Token);
        }
    }

    internal static void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (Sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            lifetime = _lifetime;
            _lifetime = null;
        }

        IsConnected = false;
        lifetime?.Cancel();
    }

    private static async Task RunAsync(CancellationToken lifetimeToken)
    {
        var backoff = InitialBackoff;
        while (!lifetimeToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(lifetimeToken);
                backoff = InitialBackoff;
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogWarning($"HERMES WebSocket disconnected: {ex.Message}");
                }
            }
            finally
            {
                IsConnected = false;
                _activeSocket = null;
            }

            if (lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(backoff, lifetimeToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private static async Task ConnectAndPumpAsync(CancellationToken lifetimeToken)
    {
        var uri = BuildWebSocketUri();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, lifetimeToken);
        IsConnected = true;
        _activeSocket = socket;
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogDebug($"HERMES WebSocket connected: {uri}");
        }

        await SendResyncAsync(socket, lifetimeToken);

        var buffer = new byte[ReceiveBufferBytes];
        while (socket.State == WebSocketState.Open && !lifetimeToken.IsCancellationRequested)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetimeToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            HandleMessage(Encoding.UTF8.GetString(messageStream.ToArray()));
        }
    }

    private static void HandleMessage(string json)
    {
        try
        {
            var envelope = JToken.Parse(json);
            var type = envelope["type"]?.ToString();
            var data = envelope["data"];
            if (data is null || data.Type is JTokenType.Null or JTokenType.Undefined)
            {
                return;
            }

            if (string.Equals(type, "notification-update", StringComparison.OrdinalIgnoreCase))
            {
                var alerts = data.ToObject<HermesAssistantAlertsResponse>();
                if (alerts is not null)
                {
                    NotificationsUpdated?.Invoke(alerts);
                }
            }
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES WebSocket received an unreadable message: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tells the server what revision this client already knows, right after (re)connecting, so it
    /// can catch up on anything that changed while the socket was down instead of waiting up to the
    /// next server-side push interval.
    /// </summary>
    private static Task SendResyncAsync(ClientWebSocket socket, CancellationToken token)
    {
        var revision = HermesWorkspaceSnapshotCoordinator.Current?.CurrentRevision ?? 0;
        var payload = $"{{\"type\":\"resync\",\"revision\":{revision}}}";
        return SendAsync(socket, payload, token);
    }

    /// <summary>
    /// Tells the server whether the player is currently in a raid, so the server-owned Assistant
    /// alert recompute (see HermesWorkspacePushCoordinator) can pause entirely while frame time
    /// matters most and catch up the instant the raid ends, instead of continuing to recompute in
    /// the background for a session nobody is watching.
    /// </summary>
    internal static async Task SendRaidStateAsync(bool inRaid)
    {
        var socket = _activeSocket;
        if (socket is not { State: WebSocketState.Open })
        {
            return;
        }

        var payload = $"{{\"type\":\"raid-state\",\"inRaid\":{(inRaid ? "true" : "false")}}}";
        try
        {
            await SendAsync(socket, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES could not send its raid state to the server: {ex.Message}");
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await SendLock.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally
        {
            SendLock.Release();
        }
    }

    private static Uri BuildWebSocketUri()
    {
        var host = RequestHandler.Host;
        var schemeSeparator = host.IndexOf("://", StringComparison.Ordinal);
        var authority = schemeSeparator >= 0 ? host[(schemeSeparator + 3)..] : host;
        var useTls = schemeSeparator >= 0
                     && host[..schemeSeparator].Equals("https", StringComparison.OrdinalIgnoreCase);
        var scheme = useTls ? "wss" : "ws";
        return new Uri($"{scheme}://{authority}/hermes/ws/{Uri.EscapeDataString(RequestHandler.SessionId)}");
    }
}
