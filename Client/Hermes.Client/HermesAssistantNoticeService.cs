using System.Reflection;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantNoticeService
{
    private const int MaximumRetainedNotices = 8;

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

    public int ActiveNoticeCount => _notices.Count(notice => !notice.Dismissed);

    public HermesAssistantNoticeService()
    {
        HermesNativeNotificationBridge.Configure(HandleNativeNotificationClick);
    }

    public string GetDiagnosticsSummary()
    {
        return $"enabled={Plugin.Settings.EnableProactiveAssistantNotices.Value}, active={ActiveNoticeCount}, "
               + $"native-active={HermesNativeNotificationBridge.ActiveCount}, retained={_notices.Count}, "
               + $"checking={_checking}, source=server-revision-snapshot, "
               + $"profile-context={(!string.IsNullOrWhiteSpace(_profileToken) ? "available" : "unavailable")}";
    }

    public void Tick(bool hermesVisible, bool assistantVisible)
    {
        _hermesVisible = hermesVisible;
        _assistantVisible = assistantVisible;
        PublishPendingNativeNotices();
    }

    public Task RefreshNowAsync()
    {
        RefreshFromCachedWorkspaceData(manual: true);
        return Task.CompletedTask;
    }

    public void UpdateFromWorkspaceData(
        string? profileToken,
        HermesLoadoutSummaryResponse? loadout,
        HermesHideoutSummaryResponse? hideout,
        HermesCraftsResponse? crafts,
        HermesStashSummaryResponse? stash,
        bool manual = false)
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

        RefreshFromCachedWorkspaceData(manual);
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
            RemoveNotice(notice, rememberDismissal: true);
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
