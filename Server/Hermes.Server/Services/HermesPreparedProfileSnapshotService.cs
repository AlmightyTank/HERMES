using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

/// <summary>
/// Shares one serialized and parsed PMC profile across parallel HERMES workspace routes.
/// A short reuse window collapses the startup/post-raid request burst, while explicit rechecks
/// invalidate the entry before reading profile state again.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesPreparedProfileSnapshotService(
    ProfileHelper profileHelper,
    JsonUtil jsonUtil)
{
    private static readonly TimeSpan SharedReadWindow = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedEntry> _bySession =
        new(StringComparer.OrdinalIgnoreCase);

    public HermesPreparedProfileSnapshot? Get(MongoId sessionId, bool forceRefresh = false)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return null;
        }

        var sessionKey = sessionId.ToString();
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!forceRefresh
                && _bySession.TryGetValue(sessionKey, out var cached)
                && ReferenceEquals(cached.Profile, profile)
                && cached.ExpiresAt > now)
            {
                return cached.Snapshot;
            }

            // Keep construction under the same gate as lookup. Without this, the four parallel
            // workspace routes can all miss together and serialize the identical PMC profile four
            // times before any one of them publishes the prepared snapshot.
            string profileJson;
            JsonObject? root;
            try
            {
                profileJson = jsonUtil.Serialize(profile) ?? "{}";
                root = JsonNode.Parse(profileJson) as JsonObject;
            }
            catch
            {
                return null;
            }

            if (root is null)
            {
                return null;
            }

            var snapshot = new HermesPreparedProfileSnapshot(
                profile,
                profileJson,
                root);
            _bySession[sessionKey] = new PreparedEntry(
                profile,
                snapshot,
                now + SharedReadWindow);
            return snapshot;
        }
    }

    public void Invalidate(MongoId sessionId)
    {
        lock (_sync)
        {
            _bySession.Remove(sessionId.ToString());
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _bySession.Clear();
        }
    }

    private sealed record PreparedEntry(
        object Profile,
        HermesPreparedProfileSnapshot Snapshot,
        DateTimeOffset ExpiresAt);
}

public sealed record HermesPreparedProfileSnapshot(
    object Profile,
    string ProfileJson,
    JsonObject Root);
