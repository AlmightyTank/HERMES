using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantNoticeService
{
    private const int MaximumRetainedNotices = 8;
    private const long PreparedFeedReuseMilliseconds = 2000;

    private readonly List<HermesAssistantNotice> _notices = [];
    private readonly HashSet<string> _dismissedFingerprints = new(StringComparer.Ordinal);
    private HashSet<string> _activeFingerprints = new(StringComparer.Ordinal);
    private bool _checking;
    private string _status = "Alerts are waiting for the initial HERMES snapshot.";
    private string? _profileToken;
    private bool _hermesVisible;
    private bool _assistantVisible;
    private HermesLoadoutSummaryResponse? _cachedLoadout;
    private HermesHideoutSummaryResponse? _cachedHideout;
    private HermesCraftsResponse? _cachedCrafts;
    private HermesStashSummaryResponse? _cachedStash;
    private Task? _serverRefreshTask;
    private long _serverRevision;
    private bool _serverSnapshotStale;
    private long _lastPreparedFeedFetchUnixMilliseconds;
    private readonly object _pushedAlertsSync = new();
    private (HermesAssistantAlertsResponse Alerts, bool Manual)? _pendingPushedAlerts;

    public int ActiveNoticeCount => _notices.Count(notice => !notice.Dismissed);

    public HermesAssistantNoticeService()
    {
        HermesNativeNotificationBridge.Configure(
            HandleNativeNotificationClick,
            HandleNativeNotificationDismiss);
    }

    public string GetDiagnosticsSummary()
    {
        return $"enabled={Plugin.Settings.EnableProactiveAssistantNotices.Value}, active={ActiveNoticeCount}, "
               + $"native-active={HermesNativeNotificationBridge.ActiveCount}, retained={_notices.Count}, "
               + $"checking={_checking}, source=server-prepared-assistant-feed, "
               + $"server-revision={_serverRevision}, server-stale={_serverSnapshotStale}, "
               + $"profile-context={(!string.IsNullOrWhiteSpace(_profileToken) ? "available" : "unavailable")}";
    }

    public void Tick(bool hermesVisible, bool assistantVisible)
    {
        _hermesVisible = hermesVisible;
        _assistantVisible = assistantVisible;
        ApplyPendingPushedAlerts();
        PublishPendingNativeNotices();
    }

    public Task RefreshNowAsync()
        => RefreshFromPreparedServerAsync(manual: true);

    /// <summary>
    /// Called from the WebSocket receive thread (via HermesWorkspaceSnapshotCoordinator) when the
    /// server pushes a recomputed Assistant alert feed. Only stores the payload; applying it
    /// touches Unity APIs (Time.realtimeSinceStartup, native notification UI) and must happen on
    /// the main thread from Tick() instead.
    /// </summary>
    internal void EnqueuePushedAlerts(HermesAssistantAlertsResponse alerts)
        => EnqueuePendingAlerts(alerts, manual: false);

    /// <summary>
    /// Called from a background workspace-refresh task (see
    /// HermesWorkspaceSnapshotCoordinator.RefreshWorkspaceCoreAsync) right after
    /// /hermes/assistant/prepare/ returns, since that response now carries the freshly prepared
    /// alerts directly. This lets a manual tab refresh display current alerts without a separate
    /// follow-up call to /hermes/assistant/alerts for data the server just handed over. Only stores
    /// the payload for the same main-thread-only reason as <see cref="EnqueuePushedAlerts"/>.
    /// </summary>
    internal void EnqueuePreparedFeedAlerts(HermesAssistantPrepareResponse prepared, bool manual)
    {
        var alerts = new HermesAssistantAlertsResponse
        {
            Found = prepared.Prepared,
            Message = prepared.Message,
            ContextToken = prepared.ContextToken,
            Revision = prepared.Revision,
            IsStale = prepared.IsStale,
            TotalAlerts = prepared.TotalAlerts,
            Alerts = prepared.Alerts
        };
        EnqueuePendingAlerts(alerts, manual);
    }

    private void EnqueuePendingAlerts(HermesAssistantAlertsResponse alerts, bool manual)
    {
        lock (_pushedAlertsSync)
        {
            _pendingPushedAlerts = (alerts, manual);
        }
    }

    private void ApplyPendingPushedAlerts()
    {
        (HermesAssistantAlertsResponse Alerts, bool Manual)? pending;
        lock (_pushedAlertsSync)
        {
            pending = _pendingPushedAlerts;
            _pendingPushedAlerts = null;
        }

        if (pending is null || !Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            return;
        }

        ApplyAlertsResponse(pending.Value.Alerts, pending.Value.Manual);
    }

    public void UpdateFromWorkspaceData(
        string? profileToken,
        HermesLoadoutSummaryResponse? loadout,
        HermesHideoutSummaryResponse? hideout,
        HermesCraftsResponse? crafts,
        HermesStashSummaryResponse? stash,
        bool manual = false,
        bool skipServerRefresh = false)
    {
        if (!string.IsNullOrWhiteSpace(_profileToken)
            && !string.IsNullOrWhiteSpace(profileToken)
            && !string.Equals(_profileToken, profileToken, StringComparison.Ordinal))
        {
            HermesNativeNotificationBridge.DismissAll();
            _notices.Clear();
            _activeFingerprints.Clear();
            _dismissedFingerprints.Clear();
            _cachedLoadout = null;
            _cachedHideout = null;
            _cachedCrafts = null;
            _cachedStash = null;
            _serverRevision = 0;
            _serverSnapshotStale = false;
            _lastPreparedFeedFetchUnixMilliseconds = 0;
        }

        if (!string.IsNullOrWhiteSpace(profileToken))
        {
            _profileToken = profileToken;
        }

        if (loadout is not null)
        {
            _cachedLoadout = loadout;
        }
        if (hideout is not null)
        {
            _cachedHideout = hideout;
        }
        if (crafts is not null)
        {
            _cachedCrafts = crafts;
        }
        if (stash is not null)
        {
            _cachedStash = stash;
        }

        // The server owns alert construction in the materialized workspace pipeline. The client keeps these summaries
        // only as an offline/transport fallback and for native workspace rendering.
        // skipServerRefresh is set when the caller already applied a freshly prepared alert feed
        // for this exact refresh (see EnqueuePreparedFeedAlerts), so fetching again here would just
        // repeat the same /hermes/assistant/alerts round-trip for data already in hand.
        if (!skipServerRefresh && (manual || (_serverRevision == 0 && HasCompleteFallbackSnapshot())))
        {
            _ = RefreshFromPreparedServerAsync(manual);
        }
    }

    public Task RefreshFromPreparedServerAsync(bool manual)
    {
        if (_serverRefreshTask is { IsCompleted: false })
        {
            return _serverRefreshTask;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!manual
            && _serverRevision > 0
            && now - _lastPreparedFeedFetchUnixMilliseconds < PreparedFeedReuseMilliseconds)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogDebug("HERMES reused the recently downloaded prepared Assistant feed.");
            }
            return Task.CompletedTask;
        }

        _serverRefreshTask = RefreshFromPreparedServerCoreAsync(manual);
        return _serverRefreshTask;
    }

    private async Task RefreshFromPreparedServerCoreAsync(bool manual)
    {
        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            _status = "Alerts are disabled in F12 configuration.";
            return;
        }

        _checking = true;
        try
        {
            var response = await HermesRevisionApiClient.GetAssistantAlertsAsync();
            ApplyAlertsResponse(response, manual);
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES prepared Assistant feed used the local fallback: {ex.Message}");
            }

            RefreshFromCachedWorkspaceData(manual);
        }
        finally
        {
            _checking = false;
            _serverRefreshTask = null;
        }
    }

    /// <summary>
    /// Applies a prepared Assistant alert feed, whether it arrived from an explicit HTTP fetch
    /// (<see cref="RefreshFromPreparedServerCoreAsync"/>) or a live WebSocket push
    /// (<see cref="ApplyPendingPushedAlerts"/>). Must run on the Unity main thread.
    /// </summary>
    private void ApplyAlertsResponse(HermesAssistantAlertsResponse response, bool manual)
    {
        if (!response.Found)
        {
            var hasFallback = HasCompleteFallbackSnapshot();
            RefreshFromCachedWorkspaceData(manual);
            if (!hasFallback && !string.IsNullOrWhiteSpace(response.Message))
            {
                _status = response.Message;
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(_profileToken)
            && !string.IsNullOrWhiteSpace(response.ContextToken)
            && !string.Equals(_profileToken, response.ContextToken, StringComparison.Ordinal))
        {
            HermesNativeNotificationBridge.DismissAll();
            _notices.Clear();
            _activeFingerprints.Clear();
            _dismissedFingerprints.Clear();
        }

        if (!string.IsNullOrWhiteSpace(response.ContextToken))
        {
            _profileToken = response.ContextToken;
        }

        _serverRevision = response.Revision;
        _serverSnapshotStale = response.IsStale;
        _lastPreparedFeedFetchUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (response.IsStale && HasCompleteFallbackSnapshot())
        {
            RefreshFromCachedWorkspaceData(manual);
            _status += " • server feed will be rematerialized by the next full or manual Refresh";
            _status = _status.Replace(
                "server feed will be rematerialized by the next full or manual Refresh",
                "server feed is being rematerialized by live sync");
            return;
        }

        var candidates = response.Alerts
            .Where(IsServerAlertEnabled)
            .Select(alert => new HermesAssistantNoticeCandidate(
                alert.Fingerprint,
                alert.Severity,
                alert.Category,
                alert.Title,
                alert.Message,
                alert.TargetTab))
            .ToList();
        ApplyCandidates(candidates);
        UpdateStatusFromCandidates(candidates, manual, response.IsStale, response.Message);
    }

    private bool IsServerAlertEnabled(HermesAssistantAlertSummary alert)
        => alert.Kind.Trim().ToLowerInvariant() switch
        {
            "loadout-critical" => Plugin.Settings.NotifyLoadoutReadiness.Value,
            "uninsured" => Plugin.Settings.NotifyHighValueUninsuredItems.Value
                           && alert.NumericValue >= Plugin.Settings.GetHighValueUninsuredThreshold(),
            "production-complete" => Plugin.Settings.NotifyCompletedHideoutProduction.Value,
            "hideout-ready" => Plugin.Settings.NotifyReadyHideoutUpgrades.Value,
            "craft-ready" => Plugin.Settings.NotifyReadyProfitableCrafts.Value
                             && alert.NumericValue >= Plugin.Settings.GetMinimumAssistantNoticeCraftProfit(),
            "stash-cleanup" or "stash-surplus" => Plugin.Settings.NotifyStashOpportunities.Value
                                                    && alert.NumericValue >= Plugin.Settings.GetMinimumAssistantNoticeStashValue(),
            _ => true
        };

    private bool HasCompleteFallbackSnapshot()
        => _cachedLoadout is { Found: true }
           && _cachedHideout is { Found: true }
           && _cachedCrafts is { Found: true }
           && _cachedStash is { Found: true };

    private void UpdateStatusFromCandidates(
        IReadOnlyCollection<HermesAssistantNoticeCandidate> candidates,
        bool manual,
        bool stale,
        string? serverMessage)
    {
        var ordered = candidates
            .OrderByDescending(candidate => SeverityRank(candidate.Severity))
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0)
        {
            _status = !string.IsNullOrWhiteSpace(serverMessage)
                ? serverMessage
                : manual
                    ? "Checked the prepared server snapshot • no actionable conditions found."
                    : "No actionable conditions in the prepared server snapshot.";
            return;
        }

        var top = ordered[0];
        var suffix = ordered.Count > 1 ? $" • +{ordered.Count - 1:N0} more" : string.Empty;
        var staleSuffix = stale ? " • prepared snapshot may need Refresh" : string.Empty;
        _status = $"{ordered.Count:N0} actionable • {top.Title} — {top.Message}{suffix}{staleSuffix}";
    }

    private void RefreshFromCachedWorkspaceData(bool manual)
    {
        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            _status = "Alerts are disabled in F12 configuration.";
            return;
        }

        _checking = true;
        try
        {
            var candidates = new List<HermesAssistantNoticeCandidate>();
            AddLoadoutCandidates(_cachedLoadout, candidates);
            AddHideoutCandidates(_cachedHideout, candidates);
            AddCraftCandidates(_cachedCrafts, candidates);
            AddStashCandidates(_cachedStash, candidates);
            ApplyCandidates(candidates);

            var ordered = candidates
                .OrderByDescending(candidate => SeverityRank(candidate.Severity))
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count == 0)
            {
                _status = manual
                    ? "Checked the loaded profile snapshot • no actionable conditions found."
                    : "No actionable conditions in the current profile snapshot.";
            }
            else
            {
                var top = ordered[0];
                var suffix = ordered.Count > 1 ? $" • +{ordered.Count - 1:N0} more" : string.Empty;
                _status = $"{ordered.Count:N0} actionable • {top.Title} — {top.Message}{suffix}";
            }
        }
        finally
        {
            _checking = false;
        }
    }

    public void Clear()
    {
        HermesNativeNotificationBridge.DismissAll();
        _notices.Clear();
        _activeFingerprints.Clear();
        _dismissedFingerprints.Clear();
        _serverRevision = 0;
        _serverSnapshotStale = false;
        _lastPreparedFeedFetchUnixMilliseconds = 0;
        _status = "Alerts cleared.";
    }

    public void DrawInbox(Action<string> navigate)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(
            ActiveNoticeCount == 0 ? "ALERTS" : $"ALERTS  {ActiveNoticeCount:N0}",
            GUILayout.Width(120f));
        GUILayout.FlexibleSpace();
        GUI.enabled = !_checking && Plugin.Settings.EnableProactiveAssistantNotices.Value;
        if (GUILayout.Button(_checking ? "Checking" : "Check", GUILayout.Width(72f)))
        {
            _ = RefreshNowAsync();
        }
        GUI.enabled = ActiveNoticeCount > 0;
        if (GUILayout.Button("Clear", GUILayout.Width(62f)))
        {
            Clear();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            GUILayout.Label("Disabled in F12 → Assistant Alerts.");
            GUILayout.EndVertical();
            return;
        }

        var rows = _notices
            .OrderByDescending(notice => notice.SeverityRank)
            .ThenByDescending(notice => notice.CreatedAt)
            .Take(MaximumRetainedNotices)
            .ToList();
        if (rows.Count == 0)
        {
            GUILayout.Label(_checking ? "Checking current profile..." : "No active alerts.");
            GUILayout.EndVertical();
            return;
        }

        foreach (var notice in rows)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(GetSeverityMarker(notice.Severity), GUILayout.Width(18f));
            if (GUILayout.Button(
                    notice.Title,
                    GUI.skin.label,
                    GUILayout.ExpandWidth(true)))
            {
                OpenNotice(notice, navigate);
            }
            if (GUILayout.Button("Open", GUILayout.Width(58f)))
            {
                OpenNotice(notice, navigate);
            }
            if (GUILayout.Button("×", GUILayout.Width(28f)))
            {
                DismissNotice(notice);
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
    }

    private void AddLoadoutCandidates(
        HermesLoadoutSummaryResponse? loadout,
        ICollection<HermesAssistantNoticeCandidate> output)
    {
        if (loadout is null || !loadout.Found)
        {
            return;
        }

        if (Plugin.Settings.NotifyLoadoutReadiness.Value && loadout.CriticalCount > 0)
        {
            var warnings = loadout.Warnings
                .Where(warning => !warning.Category.Equals("Insurance", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(warning => SeverityRank(warning.Severity))
                .Take(2)
                .Select(warning => warning.Message)
                .ToList();
            if (warnings.Count > 0)
            {
                output.Add(new HermesAssistantNoticeCandidate(
                    "loadout-critical|" + string.Join("|", warnings),
                    "Critical",
                    "Loadout",
                    $"Loadout: {loadout.CriticalCount:N0} critical issue(s)",
                    $"Readiness {loadout.ReadinessScore}% • {string.Join(" • ", warnings)}",
                    "Loadout/Overview"));
            }
        }

        if (Plugin.Settings.NotifyHighValueUninsuredItems.Value
            && loadout.ValueSummary.Found
            && loadout.ValueSummary.UninsuredItemCount > 0
            && loadout.ValueSummary.UninsuredReplacementValue >= Plugin.Settings.GetHighValueUninsuredThreshold())
        {
            var top = loadout.ValueSummary.Items
                .Where(item => item.InsuranceStatus.Equals("Uninsured", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.BestReplacementValue ?? 0L)
                .FirstOrDefault();
            var detail = top is null
                ? $"{loadout.ValueSummary.UninsuredItemCount:N0} uninsured item(s)"
                : $"Highest-value item: {top.Name} (₽{(top.BestReplacementValue ?? 0L):N0})";
            output.Add(new HermesAssistantNoticeCandidate(
                $"uninsured|{top?.ProfileItemId}|{loadout.ValueSummary.UninsuredItemCount}",
                "Warning",
                "Insurance",
                $"Insurance: ₽{loadout.ValueSummary.UninsuredReplacementValue:N0} uninsured",
                $"At-risk uninsured value: ₽{loadout.ValueSummary.UninsuredReplacementValue:N0}. {detail}.",
                "Loadout/Value & Insurance"));
        }
    }

    private void AddHideoutCandidates(
        HermesHideoutSummaryResponse? hideout,
        ICollection<HermesAssistantNoticeCandidate> output)
    {
        if (hideout is null || !hideout.Found)
        {
            return;
        }

        if (Plugin.Settings.NotifyCompletedHideoutProduction.Value)
        {
            var completed = hideout.ActiveProductions
                .Where(production => production.IsComplete)
                .OrderBy(production => production.StationName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (completed.Count > 0)
            {
                var names = string.Join(", ", completed.Take(3).Select(production =>
                    $"{production.OutputName} ×{production.OutputQuantity:N0}"));
                output.Add(new HermesAssistantNoticeCandidate(
                    "production-complete|" + string.Join("|", completed.Select(production =>
                        $"{production.StationName}:{production.OutputTemplateId}:{production.OutputQuantity}")),
                    "Information",
                    "Hideout",
                    $"Hideout: {completed.Count:N0} production(s) ready",
                    $"{completed.Count:N0} completed production(s) can be collected: {names}.",
                    "Hideout"));
            }
        }

        if (Plugin.Settings.NotifyReadyHideoutUpgrades.Value && hideout.ReadyAreaCount > 0)
        {
            var readyAreas = hideout.Areas
                .Where(area => area.Status.Contains("ready", StringComparison.OrdinalIgnoreCase))
                .OrderBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(area => area.TargetLevel.HasValue
                    ? $"{area.Name} level {area.TargetLevel.Value}"
                    : area.Name)
                .ToList();
            output.Add(new HermesAssistantNoticeCandidate(
                "hideout-ready|" + string.Join("|", readyAreas),
                "Information",
                "Hideout",
                $"Hideout: {hideout.ReadyAreaCount:N0} upgrade(s) ready",
                readyAreas.Count > 0
                    ? $"{hideout.ReadyAreaCount:N0} area(s) are ready: {string.Join(", ", readyAreas)}."
                    : $"{hideout.ReadyAreaCount:N0} hideout area(s) are ready to upgrade.",
                "Hideout"));
        }
    }

    private void AddCraftCandidates(
        HermesCraftsResponse? crafts,
        ICollection<HermesAssistantNoticeCandidate> output)
    {
        if (!Plugin.Settings.NotifyReadyProfitableCrafts.Value || crafts is null || !crafts.Found)
        {
            return;
        }

        var minimumProfit = Plugin.Settings.GetMinimumAssistantNoticeCraftProfit();
        var best = crafts.Crafts
            .Where(craft => craft.CanStartNow
                            && !craft.IsActive
                            && !craft.IsComplete
                            && craft.EstimatedBestSaleProfit >= minimumProfit)
            .OrderByDescending(craft => craft.EstimatedBestSaleProfitPerHour)
            .ThenByDescending(craft => craft.EstimatedBestSaleProfit)
            .FirstOrDefault();
        if (best is null)
        {
            return;
        }

        output.Add(new HermesAssistantNoticeCandidate(
            $"craft-ready|{best.CraftKey}",
            "Information",
            "Crafts",
            $"Craft ready: {best.OutputName}",
            $"{best.StationName} • {CraftSaleRecommendation(best)} • estimated profit ₽{best.EstimatedBestSaleProfit:N0} (₽{best.EstimatedBestSaleProfitPerHour:N0}/h).",
            "Crafts"));
    }

    private static string CraftSaleRecommendation(HermesCraftSummary craft)
    {
        if (string.Equals(craft.BestSaleSource, "Flea Market", StringComparison.OrdinalIgnoreCase))
        {
            return "sell on Flea";
        }

        return string.IsNullOrWhiteSpace(craft.BestSaleSource)
               || string.Equals(craft.BestSaleSource, "No available buyer", StringComparison.OrdinalIgnoreCase)
            ? "no available buyer"
            : $"sell to {craft.BestSaleSource}";
    }

    private void AddStashCandidates(
        HermesStashSummaryResponse? stash,
        ICollection<HermesAssistantNoticeCandidate> output)
    {
        if (!Plugin.Settings.NotifyStashOpportunities.Value || stash is null || !stash.Found)
        {
            return;
        }

        var minimumValue = Plugin.Settings.GetMinimumAssistantNoticeStashValue();
        if (stash.CleanupCandidateInstanceCount > 0 && stash.CleanupBestSaleValue >= minimumValue)
        {
            output.Add(new HermesAssistantNoticeCandidate(
                $"stash-cleanup|{stash.CleanupCandidateInstanceCount}|{stash.RecoverableCells}",
                "Information",
                "Stash",
                $"Stash: {stash.CleanupCandidateInstanceCount:N0} cleanup item(s)",
                $"{stash.CleanupCandidateInstanceCount:N0} cleanup candidate(s) could recover {stash.RecoverableCells:N0} cells and about ₽{stash.CleanupBestSaleValue:N0}.",
                "Stash"));
            return;
        }

        if (stash.SafeToSellInstanceCount > 0 && stash.PotentialBestSaleValue >= minimumValue)
        {
            output.Add(new HermesAssistantNoticeCandidate(
                $"stash-surplus|{stash.SafeToSellInstanceCount}|{Math.Round(stash.PotentiallySellQuantity, 2)}",
                "Information",
                "Stash",
                $"Stash: {stash.SafeToSellInstanceCount:N0} surplus item(s)",
                $"{stash.SafeToSellInstanceCount:N0} item instance(s) are potentially sellable for about ₽{stash.PotentialBestSaleValue:N0} after reservations.",
                "Stash"));
        }
    }

    private void ApplyCandidates(IReadOnlyCollection<HermesAssistantNoticeCandidate> candidates)
    {
        var now = Time.realtimeSinceStartup;
        var currentFingerprints = candidates
            .Select(candidate => candidate.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stale in _notices
                     .Where(notice => !currentFingerprints.Contains(notice.Fingerprint))
                     .ToList())
        {
            RemoveNotice(stale, rememberDismissal: false);
        }
        _notices.RemoveAll(notice => notice.Dismissed);
        _dismissedFingerprints.RemoveWhere(fingerprint => !currentFingerprints.Contains(fingerprint));

        foreach (var candidate in candidates
                     .OrderByDescending(candidate => SeverityRank(candidate.Severity)))
        {
            if (_dismissedFingerprints.Contains(candidate.Fingerprint)
                || _notices.Any(notice => notice.Fingerprint.Equals(candidate.Fingerprint, StringComparison.Ordinal)))
            {
                continue;
            }

            _notices.Add(new HermesAssistantNotice(
                Guid.NewGuid().ToString("N"),
                candidate.Fingerprint,
                candidate.Severity,
                candidate.Category,
                candidate.Title,
                candidate.Message,
                candidate.TargetTab,
                now));
        }

        _activeFingerprints = currentFingerprints;
        if (_notices.Count > MaximumRetainedNotices)
        {
            foreach (var overflow in _notices
                         .OrderByDescending(notice => notice.SeverityRank)
                         .ThenByDescending(notice => notice.CreatedAt)
                         .Skip(MaximumRetainedNotices)
                         .ToList())
            {
                DismissNotice(overflow);
            }
            _notices.RemoveAll(notice => notice.Dismissed);
        }
    }

    private void PublishPendingNativeNotices()
    {
        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value
            || (_hermesVisible && _assistantVisible)
            || (!Plugin.Settings.ShowAssistantNoticesDuringRaid.Value && HermesWorkspaceSnapshotCoordinator.IsRaidActive())
            || (!_hermesVisible && !Plugin.Settings.ShowAssistantNoticesWhenClosed.Value))
        {
            return;
        }

        if (HermesNativeNotificationBridge.ActiveCount > 0)
        {
            return;
        }

        var notice = _notices
            .Where(candidate => !candidate.Dismissed && !candidate.NativePublished)
            .OrderByDescending(candidate => candidate.SeverityRank)
            .ThenByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();
        if (notice is null)
        {
            return;
        }

        if (!HermesNativeNotificationBridge.TryShow(
                notice.Id,
                notice.Severity,
                notice.Category,
                notice.Title,
                notice.Message,
                notice.TargetTab,
                out var description))
        {
            return;
        }

        notice.NativePublished = true;
        notice.NativeDescription = description;
    }

    private void HandleNativeNotificationClick(string noticeId, string targetTab)
    {
        var notice = _notices.FirstOrDefault(candidate => candidate.Id == noticeId);
        if (notice is not null)
        {
            RemoveNotice(notice, rememberDismissal: true);
        }

        Plugin.Instance?.OpenNoticeTarget("Assistant");
    }

    private void HandleNativeNotificationDismiss(string noticeId)
    {
        var notice = _notices.FirstOrDefault(candidate => candidate.Id == noticeId);
        if (notice is not null)
        {
            RemoveNotice(notice, rememberDismissal: true);
        }
    }

    private void OpenNotice(HermesAssistantNotice notice, Action<string> navigate)
    {
        DismissNotice(notice);
        navigate("Assistant");
    }

    private void DismissNotice(HermesAssistantNotice notice)
    {
        RemoveNotice(notice, rememberDismissal: true);
    }

    private void RemoveNotice(HermesAssistantNotice notice, bool rememberDismissal)
    {
        notice.Dismissed = true;
        if (rememberDismissal)
        {
            _dismissedFingerprints.Add(notice.Fingerprint);
        }
        if (notice.NativePublished)
        {
            HermesNativeNotificationBridge.Dismiss(notice.Id);
            notice.NativePublished = false;
            notice.NativeDescription = null;
        }
        _notices.Remove(notice);
    }

    private static string GetSeverityMarker(string severity)
    {
        return SeverityRank(severity) switch
        {
            3 => "!",
            2 => "•",
            _ => "·"
        };
    }

    private static int SeverityRank(string severity)
    {
        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" or "error" => 3,
            "warning" => 2,
            _ => 1
        };
    }

    private sealed class HermesAssistantNotice
    {
        public HermesAssistantNotice(
            string id,
            string fingerprint,
            string severity,
            string category,
            string title,
            string message,
            string targetTab,
            float createdAt)
        {
            Id = id;
            Fingerprint = fingerprint;
            Severity = severity;
            Category = category;
            Title = title;
            Message = message;
            TargetTab = targetTab;
            CreatedAt = createdAt;
        }

        public string Id { get; }
        public string Fingerprint { get; }
        public string Severity { get; }
        public string Category { get; }
        public string Title { get; }
        public string Message { get; }
        public string TargetTab { get; }
        public float CreatedAt { get; }
        public bool Dismissed { get; set; }
        public bool NativePublished { get; set; }
        public string? NativeDescription { get; set; }
        public int SeverityRank => HermesAssistantNoticeService.SeverityRank(Severity);
    }

    private sealed record HermesAssistantNoticeCandidate(
        string Fingerprint,
        string Severity,
        string Category,
        string Title,
        string Message,
        string TargetTab);
}
