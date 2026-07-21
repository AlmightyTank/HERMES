using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Utils;
using Hermes.Server.Models;

namespace Hermes.Server.Ws;

/// <summary>
/// Sends JSON events to a specific session's HERMES WebSocket connection, if one is open. There is
/// deliberately no queue/fallback for a disconnected session: the client resyncs over the socket
/// itself once it reconnects (see <see cref="HermesWebSocketConnectionHandler.ResyncRequested"/>).
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesWebSocketPushService(
    HermesWebSocketConnectionHandler connectionHandler,
    JsonUtil jsonUtil)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks =
        new(StringComparer.Ordinal);

    public bool IsConnected(string sessionId) => connectionHandler.TryGetSocket(sessionId, out _);

    public async Task<bool> PushAsync(string sessionId, string type, object? data)
    {
        if (!connectionHandler.TryGetSocket(sessionId, out var socket)
            || socket.State != WebSocketState.Open)
        {
            return false;
        }

        var json = jsonUtil.Serialize(new HermesWsEnvelope(type, data)) ?? "{}";
        var bytes = Encoding.UTF8.GetBytes(json);

        var sendLock = _sendLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync();
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }
}
