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
    HermesProfileScopeService profileScopeService,
    HermesPreparedProfileSnapshotService preparedProfiles,
    RagfairOfferService ragfairOfferService,
    JsonUtil jsonUtil,
    HermesCatalogService catalogService,
    HermesCacheService cacheService,
    HermesStashAnalysisService stashAnalysisService,
    HermesLoadoutService loadoutService,
    HermesHideoutService hideoutService)
{
    private const long StaticDatabaseCheckSeconds = 60;
    private const long MarketCheckSeconds = 30;
    private const int ActiveWatchSeconds = 30;
    private const int InactiveWatchSeconds = 60;

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
        "profile", "stash", "loadout", "raidPlanner", "hideout", "crafts", "assistant"
    ];

    private static readonly string[] QuestDomains =
    [
        "profile", "hideout", "crafts", "raidPlanner", "assistant"
    ];

    private static readonly string[] HideoutDomains =
    [
        "profile", "hideout", "crafts", "assistant"
    ];

    private static readonly string[] VitalsDomains =
    [
        "profile", "loadout", "raidPlanner", "assistant"
    ];

    private static readonly string[] ProgressionDomains =
    [
        "profile", "market", "hideout", "crafts", "loadout", "raidPlanner", "assistant"
    ];

    private static readonly string[] ProfileMarketDomains =
    [
        "market", "stash", "loadout", "assistant"
    ];

    private static readonly string[] LiveMarketDomains =
    [
        "market", "stash", "crafts", "loadout", "assistant"
    ];

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeScopeBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PreparedAssistantFeed> _preparedAssistantSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _staticFingerprintSync = new();
    private string? _staticDatabaseFingerprint;

    public HermesWorkspaceSnapshotResponse GetSnapshot(
        MongoId sessionId,
        HermesStashAnalysisSettings stashSettings,
        HermesLoadoutAnalysisSettings loadoutSettings)
    {
        // A large snapshot can take several seconds. Resolve the concrete PMC identity before and
        // after the build so HERMES can never return a response assembled across two profiles.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var scope = profileScopeService.Resolve(sessionId);
            if (scope is null)
            {
                return MissingSnapshot(sessionId, stashSettings, loadoutSettings);
            }

            var state = RefreshState(scope, forceSlowSources: true);

            // Warm the static catalog index once at snapshot time without downloading the catalog.
            _ = catalogService.GetStatus();

            var hideout = hideoutService.GetSummary(sessionId);
            var crafts = hideoutService.GetCrafts(sessionId);
            var stash = stashAnalysisService.GetSummary(sessionId, stashSettings);
            var loadout = loadoutService.GetSummary(sessionId, loadoutSettings);

            var confirmedScope = profileScopeService.Resolve(sessionId);
            if (confirmedScope is not null
                && string.Equals(confirmedScope.ContextToken, scope.ContextToken, StringComparison.Ordinal))
            {
                var revision = ReadRevision(state);
                var response = new HermesWorkspaceSnapshotResponse(
                    true,
                    null,
                    scope.ContextToken,
                    revision.Revision,
                    revision.Domains,
                    hideout,
                    crafts,
                    stash,
                    loadout);
                StorePreparedAssistantFeed(scope.ScopeKey, response);
                return response;
            }

            RetireScope(scope, "Active PMC profile changed while HERMES was building a snapshot");
        }

        return new HermesWorkspaceSnapshotResponse(
            false,
            "The active PMC profile changed while HERMES was loading. Retrying with the new profile.",
            profileScopeService.Resolve(sessionId)?.ContextToken ?? string.Empty,
            0,
            EmptyDomains(),
            hideoutService.GetSummary(sessionId),
            hideoutService.GetCrafts(sessionId),
            stashAnalysisService.GetSummary(sessionId, stashSettings),
            loadoutService.GetSummary(sessionId, loadoutSettings));
    }

    public HermesAssistantPrepareResponse PrepareAssistantFeed(
        MongoId sessionId,
        HermesStashAnalysisSettings stashSettings,
        HermesLoadoutAnalysisSettings loadoutSettings)
    {
        // The four workspace routes have already materialized these responses during startup,
        // post-raid preparation, or a strong manual Refresh. This route only joins those server
        // caches and stores the display-ready Assistant feed; it does not return the four large
        // workspace payloads to Unity a second time.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var scope = profileScopeService.Resolve(sessionId);
            if (scope is null)
            {
                return new HermesAssistantPrepareResponse(
                    false,
                    "HERMES could not read the active PMC profile.",
                    string.Empty,
                    0,
                    EmptyDomains());
            }

            var state = RefreshState(scope, forceSlowSources: false);
            var revision = ReadRevision(state);
            var hideout = hideoutService.GetSummary(sessionId);
            var crafts = hideoutService.GetCrafts(sessionId);
            var stash = stashAnalysisService.GetSummary(sessionId, stashSettings);
            var loadout = loadoutService.GetSummary(sessionId, loadoutSettings);

            // This route is also the server-side join point for materializations that outlive a
            // client timeout. Never publish a prepared Assistant feed until every source model
            // is complete; otherwise profitable-craft or readiness alerts can silently disappear.
            if (!hideout.Found || !crafts.Found || !stash.Found || !loadout.Found)
            {
                var missing = new List<string>();
                if (!hideout.Found) missing.Add("Hideout");
                if (!crafts.Found) missing.Add("Crafts");
                if (!stash.Found) missing.Add("Stash");
                if (!loadout.Found) missing.Add("Loadout");
                return new HermesAssistantPrepareResponse(
                    false,
                    "The prepared Assistant feed is waiting for: " + string.Join(", ", missing) + ".",
                    scope.ContextToken,
                    revision.Revision,
                    revision.Domains);
            }

            var response = new HermesWorkspaceSnapshotResponse(
                true,
                null,
                scope.ContextToken,
                revision.Revision,
                revision.Domains,
                hideout,
                crafts,
                stash,
                loadout);

            var confirmedScope = profileScopeService.Resolve(sessionId);
            if (confirmedScope is not null
                && string.Equals(confirmedScope.ContextToken, scope.ContextToken, StringComparison.Ordinal))
            {
                StorePreparedAssistantFeed(scope.ScopeKey, response);
                return new HermesAssistantPrepareResponse(
                    true,
                    null,
                    response.ContextToken,
                    response.Revision,
                    response.Domains);
            }

            RetireScope(scope, "Active PMC profile changed while HERMES was preparing Assistant alerts");
        }

        return new HermesAssistantPrepareResponse(
            false,
            "The active PMC profile changed while HERMES was preparing Assistant alerts.",
            profileScopeService.Resolve(sessionId)?.ContextToken ?? string.Empty,
            0,
            EmptyDomains());
    }

    private void StorePreparedAssistantFeed(
        string scopeKey,
        HermesWorkspaceSnapshotResponse response)
    {
        var preparedAlerts = BuildAssistantAlerts(response)
            .OrderByDescending(alert => alert.SeverityRank)
            .ThenByDescending(alert => alert.NumericValue)
            .ThenBy(alert => alert.Title, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        _preparedAssistantSnapshots[scopeKey] = new PreparedAssistantFeed(response, preparedAlerts);
    }

    public HermesAssistantAlertsResponse GetAssistantAlerts(MongoId sessionId)
    {
        var scope = profileScopeService.ResolveIdentity(sessionId);
        if (scope is null)
        {
            return new HermesAssistantAlertsResponse(
                false,
                "HERMES could not read the active PMC profile.",
                string.Empty,
                0,
                false,
                0,
                []);
        }

        // Opening Assistant is a pure prepared-cache read. Source fingerprint scans happen during
        // the one warmup or an explicit Refresh, never because the screen was opened.
        var state = ResolveState(scope);
        var currentRevision = ReadRevision(state);
        if (!_preparedAssistantSnapshots.TryGetValue(scope.ScopeKey, out var prepared)
            || !prepared.Snapshot.Found
            || !string.Equals(prepared.Snapshot.ContextToken, scope.ContextToken, StringComparison.Ordinal))
        {
            return new HermesAssistantAlertsResponse(
                false,
                "The prepared Assistant feed is waiting for the one-time workspace warmup.",
                scope.ContextToken,
                currentRevision.Revision,
                false,
                0,
                []);
        }

        var snapshot = prepared.Snapshot;
        var alerts = prepared.Alerts;

        return new HermesAssistantAlertsResponse(
            true,
            alerts.Count == 0
                ? "No actionable conditions were found in the prepared workspace snapshot."
                : null,
            scope.ContextToken,
            snapshot.Revision,
            currentRevision.Domains.Assistant > snapshot.Domains.Assistant,
            alerts.Count,
            alerts);
    }

    private static IReadOnlyList<HermesAssistantAlertSummary> BuildAssistantAlerts(
        HermesWorkspaceSnapshotResponse snapshot)
    {
        var alerts = new List<HermesAssistantAlertSummary>();
        AddLoadoutAlerts(snapshot.Loadout, alerts);
        AddHideoutAlerts(snapshot.Hideout, alerts);
        AddCraftAlerts(snapshot.Crafts, alerts);
        AddStashAlerts(snapshot.Stash, alerts);

        return alerts
            .GroupBy(alert => alert.Fingerprint, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(alert => alert.SeverityRank)
                .ThenByDescending(alert => alert.NumericValue)
                .First())
            .ToList();
    }

    private static void AddLoadoutAlerts(
        HermesLoadoutSummaryResponse loadout,
        ICollection<HermesAssistantAlertSummary> output)
    {
        if (!loadout.Found)
        {
            return;
        }

        if (loadout.CriticalCount > 0)
        {
            var warnings = loadout.Warnings
                .Where(warning => !warning.Category.Equals("Insurance", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(warning => AssistantSeverityRank(warning.Severity))
                .Take(2)
                .Select(warning => warning.Message)
                .ToList();
            if (warnings.Count > 0)
            {
                output.Add(new HermesAssistantAlertSummary(
                    "loadout-critical|" + string.Join("|", warnings),
                    "loadout-critical",
                    "Critical",
                    "Loadout",
                    $"Loadout: {loadout.CriticalCount:N0} critical issue(s)",
                    $"Readiness {loadout.ReadinessScore}% • {string.Join(" • ", warnings)}",
                    "Loadout/Overview",
                    loadout.CriticalCount,
                    3));
            }
        }

        if (loadout.ValueSummary.Found
            && loadout.ValueSummary.UninsuredItemCount > 0
            && loadout.ValueSummary.UninsuredReplacementValue > 0)
        {
            var top = loadout.ValueSummary.Items
                .Where(item => item.InsuranceStatus.Equals("Uninsured", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.BestReplacementValue ?? 0L)
                .FirstOrDefault();
            var detail = top is null
                ? $"{loadout.ValueSummary.UninsuredItemCount:N0} uninsured item(s)"
                : $"Highest-value item: {top.Name} (₽{(top.BestReplacementValue ?? 0L):N0})";
            output.Add(new HermesAssistantAlertSummary(
                $"uninsured|{top?.ProfileItemId}|{loadout.ValueSummary.UninsuredItemCount}",
                "uninsured",
                "Warning",
                "Insurance",
                $"Insurance: ₽{loadout.ValueSummary.UninsuredReplacementValue:N0} uninsured",
                $"At-risk uninsured value: ₽{loadout.ValueSummary.UninsuredReplacementValue:N0}. {detail}.",
                "Loadout/Value & Insurance",
                loadout.ValueSummary.UninsuredReplacementValue,
                2));
        }
    }

    private static void AddHideoutAlerts(
        HermesHideoutSummaryResponse hideout,
        ICollection<HermesAssistantAlertSummary> output)
    {
        if (!hideout.Found)
        {
            return;
        }

        var completed = hideout.ActiveProductions
            .Where(production => production.IsComplete)
            .OrderBy(production => production.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (completed.Count > 0)
        {
            var names = string.Join(", ", completed.Take(3).Select(production =>
                $"{production.OutputName} ×{production.OutputQuantity:N0}"));
            output.Add(new HermesAssistantAlertSummary(
                "production-complete|" + string.Join("|", completed.Select(production =>
                    $"{production.StationName}:{production.OutputTemplateId}:{production.OutputQuantity}")),
                "production-complete",
                "Information",
                "Hideout",
                $"Hideout: {completed.Count:N0} production(s) ready",
                $"{completed.Count:N0} completed production(s) can be collected: {names}.",
                "Hideout",
                completed.Count,
                1));
        }

        if (hideout.ReadyAreaCount > 0)
        {
            var readyAreas = hideout.Areas
                .Where(area => area.Status.Contains("ready", StringComparison.OrdinalIgnoreCase))
                .OrderBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(area => area.TargetLevel.HasValue
                    ? $"{area.Name} level {area.TargetLevel.Value}"
                    : area.Name)
                .ToList();
            output.Add(new HermesAssistantAlertSummary(
                "hideout-ready|" + string.Join("|", readyAreas),
                "hideout-ready",
                "Information",
                "Hideout",
                $"Hideout: {hideout.ReadyAreaCount:N0} upgrade(s) ready",
                readyAreas.Count > 0
                    ? $"{hideout.ReadyAreaCount:N0} area(s) are ready: {string.Join(", ", readyAreas)}."
                    : $"{hideout.ReadyAreaCount:N0} hideout area(s) are ready to upgrade.",
                "Hideout",
                hideout.ReadyAreaCount,
                1));
        }
    }

    private static void AddCraftAlerts(
        HermesCraftsResponse crafts,
        ICollection<HermesAssistantAlertSummary> output)
    {
        if (!crafts.Found)
        {
            return;
        }

        var best = crafts.Crafts
            .Where(craft => craft.CanStartNow
                            && !craft.IsActive
                            && !craft.IsComplete
                            && craft.EstimatedBestSaleProfit > 0)
            .OrderByDescending(craft => craft.EstimatedBestSaleProfitPerHour)
            .ThenByDescending(craft => craft.EstimatedBestSaleProfit)
            .FirstOrDefault();
        if (best is null)
        {
            return;
        }

        var sale = string.Equals(best.BestSaleSource, "Flea Market", StringComparison.OrdinalIgnoreCase)
            ? "sell on Flea"
            : string.IsNullOrWhiteSpace(best.BestSaleSource)
              || string.Equals(best.BestSaleSource, "No available buyer", StringComparison.OrdinalIgnoreCase)
                ? "no available buyer"
                : $"sell to {best.BestSaleSource}";
        output.Add(new HermesAssistantAlertSummary(
            $"craft-ready|{best.CraftKey}",
            "craft-ready",
            "Information",
            "Crafts",
            $"Craft ready: {best.OutputName}",
            $"{best.StationName} • {sale} • estimated profit ₽{best.EstimatedBestSaleProfit:N0} (₽{best.EstimatedBestSaleProfitPerHour:N0}/h).",
            "Crafts",
            best.EstimatedBestSaleProfit,
            1));
    }

    private static void AddStashAlerts(
        HermesStashSummaryResponse stash,
        ICollection<HermesAssistantAlertSummary> output)
    {
        if (!stash.Found)
        {
            return;
        }

        if (stash.CleanupCandidateInstanceCount > 0 && stash.CleanupBestSaleValue > 0)
        {
            output.Add(new HermesAssistantAlertSummary(
                $"stash-cleanup|{stash.CleanupCandidateInstanceCount}|{stash.RecoverableCells}",
                "stash-cleanup",
                "Information",
                "Stash",
                $"Stash: {stash.CleanupCandidateInstanceCount:N0} cleanup item(s)",
                $"{stash.CleanupCandidateInstanceCount:N0} cleanup candidate(s) could recover {stash.RecoverableCells:N0} cells and about ₽{stash.CleanupBestSaleValue:N0}.",
                "Stash",
                stash.CleanupBestSaleValue,
                1));
            return;
        }

        if (stash.SafeToSellInstanceCount > 0 && stash.PotentialBestSaleValue > 0)
        {
            output.Add(new HermesAssistantAlertSummary(
                $"stash-surplus|{stash.SafeToSellInstanceCount}|{Math.Round(stash.PotentiallySellQuantity, 2)}",
                "stash-surplus",
                "Information",
                "Stash",
                $"Stash: {stash.SafeToSellInstanceCount:N0} surplus item(s)",
                $"{stash.SafeToSellInstanceCount:N0} item instance(s) are potentially sellable for about ₽{stash.PotentialBestSaleValue:N0} after reservations.",
                "Stash",
                stash.PotentialBestSaleValue,
                1));
        }
    }

    private static int AssistantSeverityRank(string severity)
        => severity.Trim().ToLowerInvariant() switch
        {
            "critical" or "error" => 3,
            "warning" => 2,
            _ => 1
        };

    public HermesChangesResponse GetChanges(MongoId sessionId, long knownRevision)
    {
        var scope = profileScopeService.Resolve(sessionId);
        if (scope is null)
        {
            return new HermesChangesResponse(
                false,
                "HERMES could not read the active PMC profile.",
                string.Empty,
                Math.Max(0, knownRevision),
                EmptyDomains(),
                [],
                null);
        }

        var state = RefreshState(scope, forceSlowSources: false);
        var revision = ReadRevision(state);
        var changed = revision.DomainValues
            .Where(pair => pair.Value > knownRevision)
            .Select(pair => pair.Key)
            .OrderBy(DomainOrder)
            .ToList();

        return new HermesChangesResponse(
            true,
            null,
            scope.ContextToken,
            revision.Revision,
            revision.Domains,
            changed,
            revision.Reason);
    }

    /// <summary>
    /// Holds one client request on the server instead of making the client repeatedly ask for
    /// changes. Revision state and the wake signal are scoped to the concrete active PMC profile,
    /// not merely the launcher/account session.
    /// </summary>
    public async ValueTask<HermesChangesResponse> WaitForChangesAsync(
        MongoId sessionId,
        long knownRevision,
        bool hermesOpen)
    {
        var scope = profileScopeService.Resolve(sessionId);
        if (scope is null)
        {
            return GetChanges(sessionId, knownRevision);
        }

        var state = ResolveState(scope);
        Task changeSignal;
        lock (state.Sync)
        {
            changeSignal = state.ChangeSignal.Task;
        }

        var immediate = GetChanges(sessionId, knownRevision);
        if (HasTrueUpdate(immediate, knownRevision) || !immediate.Found
            || !string.Equals(immediate.ContextToken, scope.ContextToken, StringComparison.Ordinal))
        {
            return immediate;
        }

        var holdSeconds = hermesOpen ? ActiveWatchSeconds : InactiveWatchSeconds;
        var holdDelay = Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        _ = await Task.WhenAny(changeSignal, holdDelay);

        return GetChanges(sessionId, knownRevision);
    }

    private static bool HasTrueUpdate(HermesChangesResponse response, long knownRevision)
        => response.Found
           && response.Revision > Math.Max(0L, knownRevision)
           && response.Changed.Count > 0;

    private HermesWorkspaceSnapshotResponse MissingSnapshot(
        MongoId sessionId,
        HermesStashAnalysisSettings stashSettings,
        HermesLoadoutAnalysisSettings loadoutSettings)
        => new(
            false,
            "HERMES could not read the active PMC profile.",
            string.Empty,
            0,
            EmptyDomains(),
            hideoutService.GetSummary(sessionId),
            hideoutService.GetCrafts(sessionId),
            stashAnalysisService.GetSummary(sessionId, stashSettings),
            loadoutService.GetSummary(sessionId, loadoutSettings));

    private SessionState ResolveState(HermesProfileScope scope)
    {
        var sessionKey = scope.SessionId.ToString();
        while (true)
        {
            if (!_activeScopeBySession.TryGetValue(sessionKey, out var previousScopeKey))
            {
                if (_activeScopeBySession.TryAdd(sessionKey, scope.ScopeKey))
                {
                    break;
                }

                continue;
            }

            if (string.Equals(previousScopeKey, scope.ScopeKey, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (_activeScopeBySession.TryUpdate(sessionKey, scope.ScopeKey, previousScopeKey))
            {
                _preparedAssistantSnapshots.TryRemove(previousScopeKey, out _);
                if (_sessions.TryRemove(previousScopeKey, out var previousState))
                {
                    lock (previousState.Sync)
                    {
                        Advance(previousState, AllDomains, "Active PMC profile changed");
                    }
                }

                break;
            }
        }

        return _sessions.GetOrAdd(scope.ScopeKey, _ => new SessionState());
    }

    private void RetireScope(HermesProfileScope scope, string reason)
    {
        _preparedAssistantSnapshots.TryRemove(scope.ScopeKey, out _);
        if (_sessions.TryRemove(scope.ScopeKey, out var state))
        {
            lock (state.Sync)
            {
                Advance(state, AllDomains, reason);
            }
        }

        var sessionKey = scope.SessionId.ToString();
        if (_activeScopeBySession.TryGetValue(sessionKey, out var activeScope)
            && string.Equals(activeScope, scope.ScopeKey, StringComparison.OrdinalIgnoreCase))
        {
            _activeScopeBySession.TryRemove(sessionKey, out _);
        }
    }

    public void MarkAllSessionsDirty(string? reason = null)
    {
        preparedProfiles.Clear();
        _preparedAssistantSnapshots.Clear();
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Manual HERMES refresh"
            : reason.Trim();

        hideoutService.InvalidateMaterializedSummaries(
            reason: normalizedReason);
        stashAnalysisService.Clear(normalizedReason);
        loadoutService.Clear(normalizedReason);
        cacheService.Clear(normalizedReason);

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

    public HermesRecheckResponse InvalidatePreparedWorkspace(
        MongoId sessionId,
        string workspace,
        string? reason = null)
    {
        var scope = profileScopeService.ResolveIdentity(sessionId);
        if (scope is null)
        {
            return new HermesRecheckResponse(
                false,
                "HERMES could not resolve the active PMC profile scope.",
                string.Empty);
        }

        var normalized = (workspace ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? $"Manual {workspace} workspace refresh"
            : reason.Trim();

        // A manual workspace invalidation must also discard the short-lived shared profile
        // snapshot. Otherwise the immediately following summary can rebuild from the profile
        // image captured just before the player moved medicine, food, drink, or equipment.
        profileScopeService.InvalidatePrepared(sessionId);

        switch (normalized)
        {
            case "craft":
            case "crafts":
                hideoutService.InvalidateMaterializedSummaries(
                    hideout: false,
                    crafts: true,
                    reason: normalizedReason);
                break;
            case "stash":
                stashAnalysisService.Clear(normalizedReason);
                break;
            case "loadout":
            case "raidplanner":
            case "raid planner":
                loadoutService.Clear(normalizedReason);
                break;
            case "assistant":
            case "chat":
                hideoutService.InvalidateMaterializedSummaries(
                    hideout: true,
                    crafts: true,
                    reason: normalizedReason);
                stashAnalysisService.Clear(normalizedReason);
                loadoutService.Clear(normalizedReason);
                break;
            default:
                hideoutService.InvalidateMaterializedSummaries(
                    hideout: true,
                    crafts: false,
                    reason: normalizedReason);
                break;
        }

        _preparedAssistantSnapshots.TryRemove(scope.ScopeKey, out _);
        return new HermesRecheckResponse(
            true,
            $"The prepared {workspace} workspace response was invalidated.",
            scope.ContextToken);
    }

    public HermesRecheckResponse RequestRecheck(MongoId sessionId, string? reason = null)
    {
        profileScopeService.InvalidatePrepared(sessionId);
        var scope = profileScopeService.Resolve(sessionId);
        if (scope is null)
        {
            return new HermesRecheckResponse(
                false,
                "HERMES could not read the active PMC profile.",
                string.Empty);
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Manual HERMES source recheck"
            : reason.Trim();
        var state = ResolveState(scope);
        lock (state.Sync)
        {
            state.NextStaticCheckUnix = 0;
            state.NextMarketCheckUnix = 0;
            state.LastReason = normalizedReason;
            Pulse(state);
        }

        return new HermesRecheckResponse(
            true,
            "The server is checking the active PMC sources. Workspace revisions advance only when semantic data changed.",
            scope.ContextToken);
    }

    public void RequestRecheckAllSessions(string? reason = null)
    {
        preparedProfiles.Clear();
        _preparedAssistantSnapshots.Clear();
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Manual HERMES source recheck"
            : reason.Trim();

        hideoutService.InvalidateMaterializedSummaries(reason: normalizedReason);

        foreach (var state in _sessions.Values)
        {
            lock (state.Sync)
            {
                state.NextStaticCheckUnix = 0;
                state.NextMarketCheckUnix = 0;
                state.LastReason = normalizedReason;
                Pulse(state);
            }
        }
    }

    private SessionState RefreshState(HermesProfileScope scope, bool forceSlowSources)
    {
        var state = ResolveState(scope);
        var profileRoot = preparedProfiles.Get(scope.SessionId)?.Root
                          ?? ParseObject(scope.ProfileJson);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        var clearMarketCache = false;
        var clearStashCache = false;
        var clearLoadoutCache = false;
        var clearHideoutSummaryCache = false;
        var clearCraftsSummaryCache = false;
        var resetStaticIndexes = false;

        lock (state.Sync)
        {
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inventory"] = HashJsonSections(profileRoot, "Inventory"),
                ["quests"] = HashJsonSections(profileRoot, "Quests"),
                ["hideout"] = HashJsonSections(profileRoot, "Hideout"),
                ["hideoutMilestones"] = HashHideoutMilestones(profileRoot, now),
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
            DetectChange(state, current, "hideoutMilestones", HideoutDomains, "hideout completion milestone changed", changedDomains, reasons);
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
            clearHideoutSummaryCache = changedDomains.Contains("hideout");
            clearCraftsSummaryCache = changedDomains.Contains("crafts")
                                      || liveMarketChanged
                                      || staticChanged
                                      || changedDomains.Contains("market");
            clearMarketCache = liveMarketChanged || staticChanged || changedDomains.Contains("market");
            resetStaticIndexes = staticChanged;
            Advance(state, changedDomains, string.Join("; ", reasons.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        // Clear derived response caches after the source revision has advanced. These operations do
        // not create a new source revision, so they cannot form an invalidation loop.
        if (clearHideoutSummaryCache || clearCraftsSummaryCache)
        {
            hideoutService.InvalidateMaterializedSummaries(
                hideout: clearHideoutSummaryCache,
                crafts: clearCraftsSummaryCache,
                reason: "HERMES source revision changed");
        }

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
        // EFT database tables are immutable after SPT finishes loading mods. Serializing every
        // item, trader, quest, handbook row, and hideout table on each recheck was one of the
        // largest avoidable server costs. Compute this fingerprint once for the server lifetime.
        if (!string.IsNullOrWhiteSpace(_staticDatabaseFingerprint))
        {
            return _staticDatabaseFingerprint;
        }

        lock (_staticFingerprintSync)
        {
            if (!string.IsNullOrWhiteSpace(_staticDatabaseFingerprint))
            {
                return _staticDatabaseFingerprint;
            }

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

            _staticDatabaseFingerprint = Hash(builder.ToString());
            return _staticDatabaseFingerprint;
        }
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

    private static string HashHideoutMilestones(JsonObject? root, long nowUnixSeconds)
    {
        if (root is null)
        {
            return Hash("hideout-milestones-unavailable");
        }

        var hideout = root
            .FirstOrDefault(pair => pair.Key.Equals("Hideout", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (hideout is null)
        {
            return Hash("hideout-milestones-missing");
        }

        var milestones = new List<string>();
        CollectHideoutMilestones(hideout, "Hideout", nowUnixSeconds, milestones);
        milestones.Sort(StringComparer.Ordinal);
        return Hash(string.Join("|", milestones));
    }

    private static void CollectHideoutMilestones(
        JsonNode? node,
        string path,
        long nowUnixSeconds,
        ICollection<string> milestones)
    {
        if (node is JsonObject obj)
        {
            var identity = ReadJsonText(obj, "id", "_id", "recipeId", "RecipeId", "type", "areaType", "key")
                           ?? path;

            if (TryReadJsonNumber(obj, out var completeTime, "completeTime", "CompleteTime", "endTime", "EndTime")
                && completeTime > 0d)
            {
                milestones.Add($"complete:{identity}:{nowUnixSeconds >= NormalizeUnixSeconds(completeTime)}");
            }

            if (TryReadJsonNumber(obj, out var startTime, "startTimestamp", "StartTimestamp", "startTime", "StartTime")
                && TryReadJsonNumber(obj, out var productionTime, "productionTime", "ProductionTime")
                && startTime > 0d
                && productionTime > 0d)
            {
                var completion = NormalizeUnixSeconds(startTime) + productionTime;
                milestones.Add($"production-time:{identity}:{nowUnixSeconds >= completion}");
            }

            if (TryReadJsonNumber(obj, out var progress, "progress", "Progress")
                && TryReadJsonNumber(obj, out var duration, "productionTime", "ProductionTime", "duration", "Duration")
                && duration > 0d)
            {
                milestones.Add($"production-progress:{identity}:{progress >= duration}");
            }

            if (path.Contains("Counter", StringComparison.OrdinalIgnoreCase)
                && TryReadJsonNumber(obj, out var counterValue, "value", "Value"))
            {
                milestones.Add($"counter-empty:{identity}:{counterValue <= 0d}");
            }

            foreach (var pair in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CollectHideoutMilestones(pair.Value, path + "." + pair.Key, nowUnixSeconds, milestones);
            }
            return;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                CollectHideoutMilestones(array[index], path + "[]", nowUnixSeconds, milestones);
            }
        }
    }

    private static string? ReadJsonText(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var pair = obj.FirstOrDefault(candidate => candidate.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (pair.Value is not null)
            {
                var value = pair.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static bool TryReadJsonNumber(JsonObject obj, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            var pair = obj.FirstOrDefault(candidate => candidate.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (pair.Value is null)
            {
                continue;
            }

            if (double.TryParse(
                    pair.Value.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value))
            {
                return true;
            }
        }

        value = 0d;
        return false;
    }

    private static double NormalizeUnixSeconds(double value)
        => value > 10_000_000_000d ? value / 1000d : value;

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
                       || normalized.Equals("productionTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("startTimestamp", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("startTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("endTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("completeTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("skipTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("lastTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("sptLastTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("sptUpdateLast", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("sptLastTimeUpdated", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("hideoutCounters", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("fuelCounter", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("airFilterCounter", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("waterFilterCounter", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("resourceRemaining", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("value", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("elapsed", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("elapsedSeconds", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("remainingTime", StringComparison.OrdinalIgnoreCase)
                       || normalized.Equals("remainingSeconds", StringComparison.OrdinalIgnoreCase)
                       || normalized.EndsWith("Timestamp", StringComparison.OrdinalIgnoreCase)
                       || normalized.EndsWith("Time", StringComparison.OrdinalIgnoreCase)
                       || normalized.Contains("Counter", StringComparison.OrdinalIgnoreCase)));
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

    private static void Pulse(SessionState state)
    {
        var previousSignal = state.ChangeSignal;
        state.ChangeSignal = CreateChangeSignal();
        previousSignal.TrySetResult(true);
    }

    private static void Advance(SessionState state, IEnumerable<string> domains, string reason)
    {
        state.Revision++;
        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            state.DomainRevisions[domain] = state.Revision;
        }

        state.LastReason = reason;

        // Wake the single server-held client watch only after a real domain revision advances.
        // RunContinuationsAsynchronously prevents the router continuation from running inside
        // the state lock used by RefreshState and manual cache invalidation.
        Pulse(state);
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

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TaskCompletionSource<bool> CreateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public TaskCompletionSource<bool> ChangeSignal { get; set; } = CreateChangeSignal();
    }

    private sealed record PreparedAssistantFeed(
        HermesWorkspaceSnapshotResponse Snapshot,
        IReadOnlyList<HermesAssistantAlertSummary> Alerts);

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
