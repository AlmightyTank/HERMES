using System.Reflection;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantNoticeService
{
    private const int MaximumRetainedNotices = 8;

    private readonly List<HermesAssistantNotice> _notices = [];
    private HashSet<string> _activeFingerprints = new(StringComparer.Ordinal);
    private bool _checking;
    private float _nextCheckAt = 8f;
    private string _status = "Alerts are waiting for the first profile check.";
    private string? _profileToken;
    private int _requestVersion;
    private bool _hermesVisible;
    private bool _assistantVisible;

    public int ActiveNoticeCount => _notices.Count(notice => !notice.Dismissed);

    public HermesAssistantNoticeService()
    {
        HermesNativeNotificationBridge.Configure(HandleNativeNotificationClick);
    }

    public string GetDiagnosticsSummary()
    {
        var nextSeconds = Math.Max(0, Convert.ToInt32(Math.Ceiling(_nextCheckAt - Time.realtimeSinceStartup)));
        return $"enabled={Plugin.Settings.EnableProactiveAssistantNotices.Value}, active={ActiveNoticeCount}, "
               + $"native-active={HermesNativeNotificationBridge.ActiveCount}, retained={_notices.Count}, "
               + $"checking={_checking}, next-check-seconds={nextSeconds}, "
               + $"profile-context={(!string.IsNullOrWhiteSpace(_profileToken) ? "available" : "unavailable")}";
    }

    public void Tick(bool hermesVisible, bool assistantVisible)
    {
        _hermesVisible = hermesVisible;
        _assistantVisible = assistantVisible;
        PublishPendingNativeNotices();

        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            return;
        }

        if (_checking || Time.realtimeSinceStartup < _nextCheckAt)
        {
            return;
        }

        if (!Plugin.Settings.ShowAssistantNoticesDuringRaid.Value && IsRaidActive())
        {
            _nextCheckAt = Time.realtimeSinceStartup + 15f;
            return;
        }

        _ = RefreshAsync(false);
    }

    public Task RefreshNowAsync()
    {
        return RefreshAsync(true);
    }

    public void Clear()
    {
        HermesNativeNotificationBridge.DismissAll();
        _notices.Clear();
        _activeFingerprints.Clear();
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

    private async Task RefreshAsync(bool manual)
    {
        if (_checking)
        {
            return;
        }

        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            _status = "Alerts are disabled in F12 configuration.";
            return;
        }

        if (!manual && !Plugin.Settings.ShowAssistantNoticesDuringRaid.Value && IsRaidActive())
        {
            _nextCheckAt = Time.realtimeSinceStartup + 15f;
            return;
        }

        var requestVersion = ++_requestVersion;
        var retrySoon = false;
        _checking = true;
        _status = manual ? "Checking current HERMES data..." : "Checking for meaningful changes...";

        try
        {
            var profile = await TryFetchAsync(HermesApiClient.GetProfileContextAsync, "profile context");
            if (requestVersion != _requestVersion)
            {
                return;
            }

            if (profile is null || !profile.Found || string.IsNullOrWhiteSpace(profile.ContextToken))
            {
                retrySoon = true;
                _status = profile?.Message ?? "No active PMC profile is available for alerts.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_profileToken)
                && !_profileToken.Equals(profile.ContextToken, StringComparison.Ordinal))
            {
                HermesNativeNotificationBridge.DismissAll();
                _notices.Clear();
                _activeFingerprints.Clear();
            }
            _profileToken = profile.ContextToken;

            var loadoutTask = NeedsLoadout()
                ? TryFetchAsync(
                    () => HermesApiClient.GetLoadoutSummaryAsync(Plugin.Settings.CreateLoadoutRequestSettings()),
                    "loadout")
                : Task.FromResult<HermesLoadoutSummaryResponse?>(null);
            var hideoutTask = NeedsHideout()
                ? TryFetchAsync(HermesApiClient.GetHideoutSummaryAsync, "hideout")
                : Task.FromResult<HermesHideoutSummaryResponse?>(null);
            var craftsTask = Plugin.Settings.NotifyReadyProfitableCrafts.Value
                ? TryFetchAsync(HermesApiClient.GetCraftsAsync, "crafts")
                : Task.FromResult<HermesCraftsResponse?>(null);
            var stashTask = Plugin.Settings.NotifyStashOpportunities.Value
                ? TryFetchAsync(
                    () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                    "stash")
                : Task.FromResult<HermesStashSummaryResponse?>(null);

            await Task.WhenAll(loadoutTask, hideoutTask, craftsTask, stashTask);
            if (requestVersion != _requestVersion)
            {
                return;
            }

            var candidates = new List<HermesAssistantNoticeCandidate>();
            AddLoadoutCandidates(loadoutTask.Result, candidates);
            AddHideoutCandidates(hideoutTask.Result, candidates);
            AddCraftCandidates(craftsTask.Result, candidates);
            AddStashCandidates(stashTask.Result, candidates);
            ApplyCandidates(candidates);

            _status = candidates.Count == 0
                ? "No configured actionable conditions were found."
                : $"Checked current profile data • {candidates.Count:N0} actionable condition(s) found.";
        }
        catch (Exception ex)
        {
            _status = HermesApiClient.DescribeFailure(ex, "Proactive notice check");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _checking = false;
            _nextCheckAt = Time.realtimeSinceStartup
                           + (retrySoon
                               ? 20f
                               : Plugin.Settings.GetAssistantNoticeCheckIntervalMinutes() * 60f);
        }
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
                            && craft.EstimatedEconomicProfit >= minimumProfit)
            .OrderByDescending(craft => craft.EstimatedEconomicProfitPerHour)
            .ThenByDescending(craft => craft.EstimatedEconomicProfit)
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
            $"{best.StationName} • estimated economic profit ₽{best.EstimatedEconomicProfit:N0} (₽{best.EstimatedEconomicProfitPerHour:N0}/h).",
            "Crafts"));
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
        var previousFingerprints = _activeFingerprints;
        var currentFingerprints = candidates
            .Select(candidate => candidate.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stale in _notices
                     .Where(notice => !currentFingerprints.Contains(notice.Fingerprint))
                     .ToList())
        {
            DismissNotice(stale);
        }
        _notices.RemoveAll(notice => notice.Dismissed);

        foreach (var candidate in candidates
                     .OrderByDescending(candidate => SeverityRank(candidate.Severity)))
        {
            if (previousFingerprints.Contains(candidate.Fingerprint)
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
            || _assistantVisible
            || (!Plugin.Settings.ShowAssistantNoticesDuringRaid.Value && IsRaidActive())
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
            notice.Dismissed = true;
            notice.NativePublished = false;
            notice.NativeDescription = null;
            _notices.Remove(notice);
        }

        Plugin.Instance?.OpenNoticeTarget(targetTab);
    }

    private void OpenNotice(HermesAssistantNotice notice, Action<string> navigate)
    {
        DismissNotice(notice);
        navigate(notice.TargetTab);
    }

    private void DismissNotice(HermesAssistantNotice notice)
    {
        notice.Dismissed = true;
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

    private static bool NeedsLoadout()
    {
        return Plugin.Settings.NotifyLoadoutReadiness.Value
               || Plugin.Settings.NotifyHighValueUninsuredItems.Value;
    }

    private static bool NeedsHideout()
    {
        return Plugin.Settings.NotifyCompletedHideoutProduction.Value
               || Plugin.Settings.NotifyReadyHideoutUpgrades.Value;
    }

    private static async Task<T?> TryFetchAsync<T>(Func<Task<T>> fetch, string source)
        where T : class
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES proactive {source} check failed: {ex.Message}");
            }
            return null;
        }
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

    private static bool IsRaidActive()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var gameWorldType = assemblies
                .Select(assembly => assembly.GetType("EFT.GameWorld", false))
                .FirstOrDefault(type => type is not null);
            if (gameWorldType is null)
            {
                return false;
            }

            var directInstance = gameWorldType
                .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null)
                ?? gameWorldType
                    .GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null);
            if (directInstance is not null)
            {
                return true;
            }

            var singletonType = assemblies
                .Select(assembly => assembly.GetType("Comfort.Common.Singleton`1", false))
                .FirstOrDefault(type => type is not null);
            if (singletonType is null)
            {
                return false;
            }

            var closedSingleton = singletonType.MakeGenericType(gameWorldType);
            var singletonInstance = closedSingleton
                .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            return singletonInstance is not null;
        }
        catch
        {
            return false;
        }
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
