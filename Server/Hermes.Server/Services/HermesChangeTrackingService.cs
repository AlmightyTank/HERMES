using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

/// <summary>
/// Keeps the expensive HERMES workspace responses on the server side and exposes tiny semantic
/// revision checks to the client. The service fingerprints source data, not rendered responses.
/// A revision is advanced only when a source section that affects a HERMES domain changes.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesChangeTrackingService(
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    RagfairOfferService ragfairOfferService,
    JsonUtil jsonUtil,
    HermesCatalogService catalogService,
    HermesCacheService cacheService,
    HermesStashAnalysisService stashAnalysisService,
    HermesLoadoutService loadoutService,
    HermesHideoutService hideoutService)
{
    private const long StaticDatabaseCheckSeconds = 300;
    private const long MarketCheckSeconds = 60;

    private static readonly string[] AllDomains =
    [
        "catalog",
        "market",
        "profile",
        "stash",
        "hideout",
        "crafts",
        "loadout",
        "raidPlanner",
        "assistant"
    ];

    private static readonly string[] InventoryDomains =
    [
        "profile", "stash", "loadout", "raidPlanner", "hideout", "crafts"
    ];

    private static readonly string[] QuestDomains =
    [
        "profile", "hideout", "crafts", "raidPlanner"
    ];

    private static readonly string[] HideoutDomains =
    [
        "profile", "hideout", "crafts"
    ];

    private static readonly string[] VitalsDomains =
    [
        "profile", "loadout", "raidPlanner"
    ];

    private static readonly string[] ProgressionDomains =
    [
        "profile", "market", "hideout", "crafts", "loadout", "raidPlanner"
    ];

    private static readonly string[] ProfileMarketDomains =
    [
        "market", "stash", "loadout"
    ];

    private static readonly string[] LiveMarketDomains =
    [
        "market"
    ];

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public HermesWorkspaceSnapshotResponse GetSnapshot(
        MongoId sessionId,
        HermesStashAnalysisSettings stashSettings,
        HermesLoadoutAnalysisSettings loadoutSettings)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return new HermesWorkspaceSnapshotResponse(
                false,
                "HERMES could not read the active PMC profile.",
                string.Empty,
                0,
                EmptyDomains(),
                hideoutService.GetSummary(sessionId),
                hideoutService.GetCrafts(sessionId),
                stashAnalysisService.GetSummary(sessionId, stashSettings),
                loadoutService.GetSummary(sessionId, loadoutSettings));
        }

        var state = RefreshState(sessionId, forceSlowSources: true);

        // Warm the static catalog index once at snapshot time without downloading the catalog.
        _ = catalogService.GetStatus();

        var hideout = hideoutService.GetSummary(sessionId);
        var crafts = hideoutService.GetCrafts(sessionId);
        var stash = stashAnalysisService.GetSummary(sessionId, stashSettings);
        var loadout = loadoutService.GetSummary(sessionId, loadoutSettings);
        var revision = ReadRevision(state);

        return new HermesWorkspaceSnapshotResponse(
            true,
            null,
            CreateContextToken(sessionId),
            revision.Revision,
            revision.Domains,
            hideout,
            crafts,
            stash,
            loadout);
    }

    public HermesChangesResponse GetChanges(MongoId sessionId, long knownRevision)
    {
        if (profileHelper.GetPmcProfile(sessionId) is null)
        {
            return new HermesChangesResponse(
                false,
                "HERMES could not read the active PMC profile.",
                CreateContextToken(sessionId),
                Math.Max(0, knownRevision),
                EmptyDomains(),
                [],
                null);
        }

        var state = RefreshState(sessionId, forceSlowSources: false);
        var revision = ReadRevision(state);
        var changed = revision.DomainValues
            .Where(pair => pair.Value > knownRevision)
            .Select(pair => pair.Key)
            .OrderBy(DomainOrder)
            .ToList();

        return new HermesChangesResponse(
            true,
            null,
            CreateContextToken(sessionId),
            revision.Revision,
            revision.Domains,
            changed,
            revision.Reason);
    }

    public void MarkAllSessionsDirty(string? reason = null)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Manual HERMES refresh"
            : reason.Trim();

        foreach (var state in _sessions.Values)
        {
            lock (state.Sync)
            {
                Advance(state, AllDomains, normalizedReason);
                state.NextStaticCheckUnix = 0;
                state.NextMarketCheckUnix = 0;
            }
        }
    }

    private SessionState RefreshState(MongoId sessionId, bool forceSlowSources)
    {
        var key = sessionId.ToString();
        var state = _sessions.GetOrAdd(key, _ => new SessionState());
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return state;
        }

        var profileJson = jsonUtil.Serialize(profile) ?? "{}";
        var profileRoot = ParseObject(profileJson);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        var clearMarketCache = false;
        var clearStashCache = false;
        var clearLoadoutCache = false;
        var resetStaticIndexes = false;

        lock (state.Sync)
        {
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inventory"] = HashJsonSections(profileRoot, "Inventory"),
                ["quests"] = HashJsonSections(profileRoot, "Quests"),
                ["hideout"] = HashJsonSections(profileRoot, "Hideout"),
                ["vitals"] = HashJsonSections(profileRoot, "Health"),
                ["progression"] = HashProfileProgression(profileRoot),
                ["profileMarket"] = HashJsonSections(profileRoot, "RagfairInfo", "RagFairInfo", "InsuredItems")
            };

            var checkStatic = forceSlowSources || now >= state.NextStaticCheckUnix;
            if (checkStatic)
            {
                current["staticDatabase"] = BuildStaticDatabaseFingerprint();
                state.NextStaticCheckUnix = now + StaticDatabaseCheckSeconds;
            }

            var checkMarket = forceSlowSources || now >= state.NextMarketCheckUnix;
            if (checkMarket)
            {
                current["liveMarket"] = BuildLiveMarketFingerprint();
                state.NextMarketCheckUnix = now + MarketCheckSeconds;
            }

            if (!state.Initialized)
            {
                foreach (var pair in current)
                {
                    state.Fingerprints[pair.Key] = pair.Value;
                }

                state.Initialized = true;
                state.LastReason = "Initial HERMES server snapshot";
                return state;
            }

            DetectChange(state, current, "inventory", InventoryDomains, "inventory changed", changedDomains, reasons);
            DetectChange(state, current, "quests", QuestDomains, "quest progress changed", changedDomains, reasons);
            DetectChange(state, current, "hideout", HideoutDomains, "hideout state changed", changedDomains, reasons);
            DetectChange(state, current, "vitals", VitalsDomains, "health or raid vitals changed", changedDomains, reasons);
            DetectChange(state, current, "progression", ProgressionDomains, "profile progression changed", changedDomains, reasons);
            DetectChange(state, current, "profileMarket", ProfileMarketDomains, "profile trader, insurance, or flea state changed", changedDomains, reasons);

            var staticChanged = DetectChange(
                state,
                current,
                "staticDatabase",
                AllDomains,
                "SPT database tables changed",
                changedDomains,
                reasons);

            var liveMarketChanged = DetectChange(
                state,
                current,
                "liveMarket",
                LiveMarketDomains,
                "market price table changed",
                changedDomains,
                reasons);

            if (changedDomains.Count == 0)
            {
                return state;
            }

            clearStashCache = changedDomains.Contains("stash");
            clearLoadoutCache = changedDomains.Contains("loadout") || changedDomains.Contains("raidPlanner");
            clearMarketCache = liveMarketChanged || staticChanged || changedDomains.Contains("market");
            resetStaticIndexes = staticChanged;
            Advance(state, changedDomains, string.Join("; ", reasons.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        // Clear derived response caches after the source revision has advanced. These operations do
        // not create a new source revision, so they cannot form an invalidation loop.
        if (clearStashCache)
        {
            stashAnalysisService.Clear("HERMES source revision changed");
        }

        if (clearLoadoutCache)
        {
            loadoutService.Clear("HERMES source revision changed");
        }

        if (clearMarketCache)
        {
            cacheService.Clear("HERMES market source revision changed");
        }

        if (resetStaticIndexes)
        {
            ResetKnownStaticIndexes(catalogService);
            ResetKnownStaticIndexes(hideoutService);
        }

        return state;
    }

    private bool DetectChange(
        SessionState state,
        IReadOnlyDictionary<string, string> current,
        string source,
        IEnumerable<string> affectedDomains,
        string reason,
        ISet<string> changedDomains,
        ICollection<string> reasons)
    {
        if (!current.TryGetValue(source, out var fingerprint))
        {
            return false;
        }

        if (state.Fingerprints.TryGetValue(source, out var previous)
            && string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        state.Fingerprints[source] = fingerprint;
        foreach (var domain in affectedDomains)
        {
            changedDomains.Add(domain);
        }

        reasons.Add(reason);
        return true;
    }

    private string BuildStaticDatabaseFingerprint()
    {
        var builder = new StringBuilder(4096);
        AppendSerialized(builder, "items", databaseService.GetItems());
        AppendSerialized(builder, "traders", databaseService.GetTraders());

        object? tables = null;
        try
        {
            tables = databaseService.GetTables();
        }
        catch
        {
            // Individual service accessors above remain sufficient for a safe fallback.
        }

        if (tables is not null)
        {
            AppendSerialized(builder, "handbook", ReadPath(tables, "Templates.Handbook"));
            AppendSerialized(builder, "quests", ReadPath(tables, "Templates.Quests"));
            AppendSerialized(builder, "hideout", ReadPath(tables, "Hideout"));
        }

        return Hash(builder.ToString());
    }

    private string BuildLiveMarketFingerprint()
    {
        // Generated flea offers naturally churn as timers expire and offers rotate. Treating the
        // entire offer store as a source fingerprint caused constant false-positive revisions.
        // HERMES now watches the stable server price table here; player flea state is tracked
        // separately through the PMC profile, and an item-specific market view remains demand-loaded.
        var builder = new StringBuilder(4096);
        try
        {
            var tables = databaseService.GetTables();
            AppendSerialized(builder, "dynamicPrices", ReadPath(tables, "Templates.Prices"));
        }
        catch
        {
            builder.Append("dynamicPrices:unavailable;");
        }

        return Hash(builder.ToString());
    }

    private void AppendSerialized(StringBuilder builder, string label, object? value)
    {
        builder.Append(label).Append(':');
        if (value is null)
        {
            builder.Append("null;");
            return;
        }

        try
        {
            builder.Append(jsonUtil.Serialize(value) ?? "null").Append(';');
        }
        catch
        {
            builder.Append(value.GetType().FullName).Append(':');
            AppendCompact(value, builder, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), 10_000);
            builder.Append(';');
        }
    }

    private static void AppendCompact(
        object? value,
        StringBuilder builder,
        int depth,
        ISet<object> visited,
        int remainingBudget)
    {
        if (value is null || remainingBudget <= 0)
        {
            builder.Append("null");
            return;
        }

        var type = value.GetType();
        if (value is string text)
        {
            builder.Append(text);
            return;
        }

        if (type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid)
        {
            builder.Append(value);
            return;
        }

        if (depth >= 5)
        {
            builder.Append(type.FullName);
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            builder.Append("<cycle>");
            return;
        }

        if (value is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= remainingBudget)
                {
                    break;
                }

                AppendCompact(entry.Key, builder, depth + 1, visited, remainingBudget - count);
                builder.Append('=');
                AppendCompact(entry.Value, builder, depth + 1, visited, remainingBudget - count);
                builder.Append('|');
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= remainingBudget)
                {
                    break;
                }

                AppendCompact(item, builder, depth + 1, visited, remainingBudget - count);
                builder.Append('|');
            }

            return;
        }

        var members = type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member is PropertyInfo or FieldInfo)
            .Where(member => IsFingerprintMember(member.Name))
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .Take(24);

        foreach (var member in members)
        {
            builder.Append(member.Name).Append('=');
            try
            {
                var memberValue = member switch
                {
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(value),
                    FieldInfo field => field.GetValue(value),
                    _ => null
                };
                AppendCompact(memberValue, builder, depth + 1, visited, remainingBudget - 1);
            }
            catch
            {
                builder.Append("<unreadable>");
            }

            builder.Append(';');
        }
    }

    private static bool IsFingerprintMember(string name)
    {
        var normalized = name.TrimStart('_');
        return normalized.Equals("Id", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Key", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Value", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Root", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Quantity", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("EndTime", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("StartTime", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Locked", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Items", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Requirements", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("TemplateId", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Tpl", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Count", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Price", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("NextResupply", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("offer", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }


    private static string HashProfileProgression(JsonObject? root)
    {
        if (root is null)
        {
            return Hash("profile-unavailable");
        }

        var builder = new StringBuilder();
        var info = root.FirstOrDefault(pair => pair.Key.Equals("Info", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        if (info is not null)
        {
            foreach (var name in new[] { "Level", "Experience", "Side", "GameVersion", "MemberCategory" })
            {
                var node = info.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                builder.Append("Info.").Append(name).Append(':');
                AppendCanonicalJson(node, builder, ignoreRuntimeProgress: false);
                builder.Append(';');
            }
        }

        foreach (var name in new[] { "Skills", "TradersInfo" })
        {
            var node = root.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            builder.Append(name).Append(':');
            AppendCanonicalJson(node, builder, ignoreRuntimeProgress: false);
            builder.Append(';');
        }

        return Hash(builder.ToString());
    }

    private static string HashJsonSections(JsonObject? root, params string[] names)
    {
        if (root is null)
        {
            return Hash("profile-unavailable");
        }

        var builder = new StringBuilder();
        foreach (var name in names)
        {
            var node = root.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            builder.Append(name).Append(':');
            AppendCanonicalJson(
                node,
                builder,
                ignoreRuntimeProgress: name.Equals("Hideout", StringComparison.OrdinalIgnoreCase));
            builder.Append(';');
        }

        return Hash(builder.ToString());
    }

    private static void AppendCanonicalJson(JsonNode? node, StringBuilder builder, bool ignoreRuntimeProgress)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonValue value:
                builder.Append(value.ToJsonString());
                return;
            case JsonObject obj:
                builder.Append('{');
                foreach (var pair in obj
                             .Where(pair => !IsVolatileJsonProperty(pair.Key, ignoreRuntimeProgress))
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append(pair.Key).Append(':');
                    AppendCanonicalJson(pair.Value, builder, ignoreRuntimeProgress);
                    builder.Append(';');
                }
                builder.Append('}');
                return;
            case JsonArray array:
            {
                // Profile arrays such as inventory items, quests, skills and hideout areas are
                // semantic sets for HERMES. Sorting their canonical representations prevents a
                // dictionary/list enumeration reorder from looking like a real profile change.
                var values = new List<string>(array.Count);
                foreach (var item in array)
                {
                    var itemBuilder = new StringBuilder();
                    AppendCanonicalJson(item, itemBuilder, ignoreRuntimeProgress);
                    values.Add(itemBuilder.ToString());
                }

                values.Sort(StringComparer.Ordinal);
                builder.Append('[');
                foreach (var valueText in values)
                {
                    builder.Append(valueText).Append('|');
                }
                builder.Append(']');
                return;
            }
            default:
                builder.Append(node.ToJsonString());
                return;
        }
    }

    private static bool IsVolatileJsonProperty(string name, bool ignoreRuntimeProgress)
    {
        var normalized = name.Trim().TrimStart('_');
        return normalized.Equals("lastUpdate", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("lastUpdated", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("lastUpdateTimestamp", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("updatedAt", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("currentTime", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("serverTime", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("sptUpdateLast", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("sptLastUpdate", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("lastRefresh", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("refreshTime", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("lastCheck", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("checkedAt", StringComparison.OrdinalIgnoreCase)
               || (ignoreRuntimeProgress
                   && (normalized.Equals("progress", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("progressTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("elapsed", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("elapsedSeconds", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("remainingTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("remainingSeconds", StringComparison.OrdinalIgnoreCase)));
    }

    private static object? ReadPath(object root, string path)
    {
        object? current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is null)
            {
                return null;
            }

            current = ReadMember(current, segment);
        }

        return current;
    }

    private static object? ReadMember(object target, params string[] names)
    {
        var type = target.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field is not null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch
                {
                    // Try the next candidate.
                }
            }
        }

        return null;
    }

    private static void ResetKnownStaticIndexes(object service)
    {
        var type = service.GetType();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_catalog", "_byKey", "_byTemplate", "_traderJson", "_hideoutJson", "_questJson",
            "_areasByKey", "_areasByType", "_craftsByKey", "_craftsById", "_traderNames",
            "_questUsesByTemplate"
        };

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (!candidates.Contains(field.Name) || field.FieldType.IsValueType)
            {
                continue;
            }

            try
            {
                field.SetValue(service, null);
            }
            catch
            {
                // A future SPT/HERMES build may make a cache readonly; the next process restart still
                // rebuilds it, and revision tracking remains safe.
            }
        }
    }

    private static void Advance(SessionState state, IEnumerable<string> domains, string reason)
    {
        state.Revision++;
        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            state.DomainRevisions[domain] = state.Revision;
        }

        state.LastReason = reason;
    }

    private static RevisionSnapshot ReadRevision(SessionState state)
    {
        lock (state.Sync)
        {
            var values = new Dictionary<string, long>(state.DomainRevisions, StringComparer.OrdinalIgnoreCase);
            return new RevisionSnapshot(
                state.Revision,
                ToDomains(values),
                values,
                state.LastReason);
        }
    }

    private static HermesDomainRevisions ToDomains(IReadOnlyDictionary<string, long> values)
        => new(
            Read(values, "catalog"),
            Read(values, "market"),
            Read(values, "profile"),
            Read(values, "stash"),
            Read(values, "hideout"),
            Read(values, "crafts"),
            Read(values, "loadout"),
            Read(values, "raidPlanner"),
            Read(values, "assistant"));

    private static HermesDomainRevisions EmptyDomains() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static long Read(IReadOnlyDictionary<string, long> values, string key)
        => values.TryGetValue(key, out var value) ? value : 0;

    private static int DomainOrder(string domain)
    {
        var index = Array.FindIndex(
            AllDomains,
            item => item.Equals(domain, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : int.MaxValue;
    }

    private static string CreateContextToken(MongoId sessionId)
        => Hash($"HERMES:PROFILE:{sessionId}");

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class SessionState
    {
        public object Sync { get; } = new();
        public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> DomainRevisions { get; } =
            AllDomains.ToDictionary(domain => domain, _ => 1L, StringComparer.OrdinalIgnoreCase);
        public long Revision { get; set; } = 1;
        public long NextStaticCheckUnix { get; set; }
        public long NextMarketCheckUnix { get; set; }
        public string LastReason { get; set; } = "Server startup";
        public bool Initialized { get; set; }
    }

    private sealed record RevisionSnapshot(
        long Revision,
        HermesDomainRevisions Domains,
        IReadOnlyDictionary<string, long> DomainValues,
        string Reason);

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
