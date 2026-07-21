using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers.Ws;

namespace Hermes.Server.Ws;

/// <summary>
/// HERMES's own WebSocket endpoint (`/hermes/ws/`), separate from the base game's
/// `/notifierServer/getwebsocket/` connection. Owns the session-to-socket registry that
/// <see cref="HermesWebSocketPushService"/> and <see cref="Services.HermesWorkspacePushCoordinator"/>
/// read from; the base game's own notification pipeline is never touched.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesWebSocketConnectionHandler : IWebSocketConnectionHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _socketsBySession =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<WebSocket, string> _sessionsBySocket = new();

    /// <summary>
    /// Raised when a connected client asks HERMES to resync (typically right after connecting or
    /// reconnecting), so the push coordinator can answer with an immediate change check instead of
    /// waiting for its next gated tick.
    /// </summary>
    public event Func<string, long, Task>? ResyncRequested;

    /// <summary>
    /// Raised when a connected client reports a raid start/end transition, so the push coordinator
    /// can pause its background Assistant alert recompute for that session while a raid is active
    /// and catch up immediately once it ends.
    /// </summary>
    public event Func<string, bool, Task>? RaidStateChanged;

    /// <summary>Raised once a session's socket has fully closed, so per-session state can be dropped.</summary>
    public event Action<string>? Disconnected;

    public string GetHookUrl() => "/hermes/ws/";

    public string GetSocketId() => "hermes";

    public Task OnConnection(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        _socketsBySession[sessionIdContext] = ws;
        _sessionsBySocket[ws] = sessionIdContext;
        return Task.CompletedTask;
    }

    public Task OnMessage(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        if (messageType != WebSocketMessageType.Text
            || rawData.Length == 0
            || !_sessionsBySocket.TryGetValue(ws, out var sessionId))
        {
            return Task.CompletedTask;
        }

        Models.HermesWsClientMessage? message;
        try
        {
            message = System.Text.Json.JsonSerializer.Deserialize<Models.HermesWsClientMessage>(
                Encoding.UTF8.GetString(rawData));
        }
        catch
        {
            return Task.CompletedTask;
        }

        switch (message)
        {
            case { Type: "resync" }:
            {
                var handler = ResyncRequested;
                return handler is null
                    ? Task.CompletedTask
                    : handler.Invoke(sessionId, Math.Max(0L, message.Revision));
            }
            case { Type: "raid-state", InRaid: not null }:
            {
                var handler = RaidStateChanged;
                return handler is null
                    ? Task.CompletedTask
                    : handler.Invoke(sessionId, message.InRaid.Value);
            }
            default:
                return Task.CompletedTask;
        }
    }

    public Task OnClose(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (_sessionsBySocket.TryRemove(ws, out var sessionId))
        {
            // Only drop the session->socket entry if it still points at the socket that just
            // closed. A reconnect can register a new socket for the same session before the old
            // one finishes closing; that race must not evict the newer live connection.
            if (_socketsBySession.TryRemove(new KeyValuePair<string, WebSocket>(sessionId, ws)))
            {
                Disconnected?.Invoke(sessionId);
            }
        }

        return Task.CompletedTask;
    }

    public bool TryGetSocket(string sessionId, out WebSocket socket)
        => _socketsBySession.TryGetValue(sessionId, out socket!);

    public ICollection<string> ConnectedSessionIds => _socketsBySession.Keys;
}
