using System.Collections.Concurrent;
using Hermes.Server.Models;
using Hermes.Server.Ws;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;

namespace Hermes.Server.Services;

/// <summary>
/// Replaces "hash the profile on every client poll" with "hash it once per connected session on
/// one server-owned interval, and push only when something actually changed." Reuses
/// <see cref="HermesChangeTrackingService"/>'s existing semantic-fingerprint diff unchanged; this
/// class only decides when to call it and where the result goes.
///
/// Only the Assistant alert feed is pushed live. Workspace tab data (Hideout, Crafts, Stash,
/// Loadout) is intentionally never pushed: recomputing and applying it in the background was the
/// main source of the client-side slowdown this coordinator replaced, so that data now refreshes
/// only when the player actually opens HERMES or switches to a workspace tab.
///
/// A session's tick is also paused entirely for as long as the client reports it is in a raid
/// (see <see cref="HandleRaidStateChangedAsync"/>), since that is when frame time matters most and
/// nothing displays these alerts by default anyway. It resumes with an immediate catch-up check
/// the moment the client reports the raid ended, the same way a resync does.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesWorkspacePushCoordinator : IOnUpdate
{
    // Widened from the original 15s: GetChanges re-serializes and re-hashes the whole profile on
    // every tick for every primed session, so a shorter interval means more sustained background
    // CPU cost on the same machine as the game. 60s keeps alerts reasonably live at a quarter the cost.
    private const long PushIntervalSeconds = 60;

    private readonly HermesWebSocketConnectionHandler _connectionHandler;
    private readonly HermesWebSocketPushService _pushService;
    private readonly HermesChangeTrackingService _changeTrackingService;

    // Populated only once a session resyncs (reports the revision it already knows), so a session
    // that has connected but not yet resynced is never treated as "everything changed."
    private readonly ConcurrentDictionary<string, long> _lastPushedRevisionBySession =
        new(StringComparer.Ordinal);

    // Presence of a key means that session last reported it is in a raid. Absence means not in a
    // raid (including sessions that have never reported a raid state, e.g. before their first raid).
    private readonly ConcurrentDictionary<string, bool> _inRaidBySession =
        new(StringComparer.Ordinal);

    private long _nextTickUnixMs;

    public HermesWorkspacePushCoordinator(
        HermesWebSocketConnectionHandler connectionHandler,
        HermesWebSocketPushService pushService,
        HermesChangeTrackingService changeTrackingService)
    {
        _connectionHandler = connectionHandler;
        _pushService = pushService;
        _changeTrackingService = changeTrackingService;
        _connectionHandler.ResyncRequested += HandleResyncRequestedAsync;
        _connectionHandler.RaidStateChanged += HandleRaidStateChangedAsync;
        _connectionHandler.Disconnected += HandleDisconnected;
    }

    public async Task<bool> OnUpdate(long timeSinceLastRun)
    {
        _ = timeSinceLastRun;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < _nextTickUnixMs)
        {
            return true;
        }

        _nextTickUnixMs = now + PushIntervalSeconds * 1000;

        // Only sessions that have already resynced have a baseline revision to diff against.
        // Sessions currently in a raid are skipped entirely, including the cheap fingerprint check.
        foreach (var sessionId in _connectionHandler.ConnectedSessionIds)
        {
            if (_lastPushedRevisionBySession.ContainsKey(sessionId) && !_inRaidBySession.GetValueOrDefault(sessionId))
            {
                await CheckAndPushAsync(sessionId);
            }
        }

        return true;
    }

    private Task HandleResyncRequestedAsync(string sessionId, long knownRevision)
    {
        _lastPushedRevisionBySession[sessionId] = knownRevision;
        return _inRaidBySession.GetValueOrDefault(sessionId) ? Task.CompletedTask : CheckAndPushAsync(sessionId);
    }

    private Task HandleRaidStateChangedAsync(string sessionId, bool inRaid)
    {
        if (inRaid)
        {
            _inRaidBySession[sessionId] = true;
            return Task.CompletedTask;
        }

        _inRaidBySession.TryRemove(sessionId, out _);

        // Catch up immediately instead of waiting for the next tick, the same way a resync does.
        return _lastPushedRevisionBySession.ContainsKey(sessionId) ? CheckAndPushAsync(sessionId) : Task.CompletedTask;
    }

    private void HandleDisconnected(string sessionId)
    {
        _lastPushedRevisionBySession.TryRemove(sessionId, out _);
        _inRaidBySession.TryRemove(sessionId, out _);
    }

    private async Task CheckAndPushAsync(string sessionId)
    {
        var lastRevision = _lastPushedRevisionBySession.GetValueOrDefault(sessionId, 0L);

        HermesChangesResponse changes;
        try
        {
            changes = _changeTrackingService.GetChanges(new MongoId(sessionId), lastRevision);
        }
        catch
        {
            // The active profile may be mid-transition (raid load/unload). Try again next tick.
            return;
        }

        if (!changes.Found)
        {
            return;
        }

        if (changes.Revision <= lastRevision || changes.Changed.Count == 0)
        {
            _lastPushedRevisionBySession[sessionId] = Math.Max(lastRevision, changes.Revision);
            return;
        }

        _lastPushedRevisionBySession[sessionId] = changes.Revision;

        // The expensive Hideout/Crafts/Stash/Loadout analysis behind the Assistant feed happens
        // here, entirely server-side, instead of being triggered by an HTTP round-trip from the
        // client. Nothing is pushed to a session that has not primed its analysis settings yet
        // (RefreshAssistantAlertsForPush returns null) by opening HERMES at least once.
        HermesAssistantAlertsResponse? alerts;
        try
        {
            alerts = _changeTrackingService.RefreshAssistantAlertsForPush(new MongoId(sessionId));
        }
        catch
        {
            // The active profile may be mid-transition (raid load/unload). Try again next tick.
            return;
        }

        if (alerts is { Found: true })
        {
            await _pushService.PushAsync(sessionId, "notification-update", alerts);
        }
    }
}
