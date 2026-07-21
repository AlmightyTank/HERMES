namespace Hermes.Server.Models;

/// <summary>
/// Envelope for every message HERMES sends over its dedicated WebSocket connection. `Type`
/// discriminates the payload shape carried in `Data` (currently only "notification-update",
/// carrying a `HermesAssistantAlertsResponse`). Workspace tab data is deliberately never pushed;
/// it is only ever fetched on demand when the player opens HERMES or switches tabs.
/// </summary>
public sealed record HermesWsEnvelope(string Type, object? Data);

/// <summary>
/// Shape of an inbound message HERMES accepts on its WebSocket connection.
/// `{"type":"resync","revision":N}` and `{"type":"raid-state","inRaid":true|false}` are
/// understood; anything else is ignored. `Revision` and `InRaid` are each only populated on the
/// message type that uses them.
/// </summary>
public sealed record HermesWsClientMessage(string? Type, long Revision, bool? InRaid);
