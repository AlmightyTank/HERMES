using System.Reflection;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantNoticeService
{
    private const int MaximumRetainedNotices = 30;

    private readonly List<HermesAssistantNotice> _notices = [];
    private readonly Dictionary<string, float> _lastShownByFingerprint = new(StringComparer.Ordinal);
    private HashSet<string> _activeFingerprints = new(StringComparer.Ordinal);
    private bool _checking;
    private float _nextCheckAt = 8f;
    private string _status = "Proactive notices are waiting for the first profile check.";
    private string? _profileToken;
    private int _requestVersion;
    private bool _inboxExpanded = true;
    private GUIStyle? _overlayTitleStyle;
    private GUIStyle? _overlayBodyStyle;
    private GUIStyle? _overlayMetaStyle;
    private GUIStyle? _overlayHintStyle;
    private GUIStyle? _overlayCloseStyle;

    public int ActiveNoticeCount => _notices.Count(notice => !notice.Dismissed);

    public string GetDiagnosticsSummary()
    {
        var nextSeconds = Math.Max(0, Convert.ToInt32(Math.Ceiling(_nextCheckAt - Time.realtimeSinceStartup)));
        return $"enabled={Plugin.Settings.EnableProactiveAssistantNotices.Value}, active={ActiveNoticeCount}, "
               + $"retained={_notices.Count}, checking={_checking}, next-check-seconds={nextSeconds}, "
               + $"profile-context={(!string.IsNullOrWhiteSpace(_profileToken) ? "available" : "unavailable")}";
    }

    public void Tick()
    {
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
        _notices.Clear();
        _activeFingerprints.Clear();
        _lastShownByFingerprint.Clear();
        _status = "Notice history cleared.";
    }

    public void DrawOverlay(bool hermesVisible, bool assistantVisible, Action<string> navigate)
    {
        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value
            || assistantVisible
            || (!Plugin.Settings.ShowAssistantNoticesDuringRaid.Value && IsRaidActive())
            || (!hermesVisible && !Plugin.Settings.ShowAssistantNoticesWhenClosed.Value))
        {
            return;
        }

        var visible = _notices
            .Where(notice => !notice.Dismissed)
            .OrderByDescending(notice => notice.SeverityRank)
            .ThenByDescending(notice => notice.CreatedAt)
            .Take(Plugin.Settings.GetMaximumVisibleAssistantNotices())
            .ToList();
        if (visible.Count == 0)
        {
            return;
        }

        EnsureOverlayStyles();
        var width = Math.Min(430f, Math.Max(350f, Screen.width * 0.30f));
        var cardHeight = Plugin.Settings.CompactMode.Value ? 94f : 112f;
        const float spacing = 8f;
        const float rightMargin = 18f;
        const float bottomMargin = 24f;
        var totalHeight = visible.Count * cardHeight + Math.Max(0, visible.Count - 1) * spacing;
        var startY = Math.Max(12f, Screen.height - bottomMargin - totalHeight);
        var startX = Screen.width - rightMargin - width;

        for (var index = 0; index < visible.Count; index++)
        {
            var cardRect = new Rect(
                startX,
                startY + index * (cardHeight + spacing),
                width,
                cardHeight);
            DrawPersistentEftNoticeCard(cardRect, visible[index], navigate);
        }
    }

    private void DrawPersistentEftNoticeCard(
        Rect cardRect,
        HermesAssistantNotice notice,
        Action<string> navigate)
    {
        var originalColor = GUI.color;
        var originalContentColor = GUI.contentColor;
        var accent = GetSeverityColor(notice.Severity);

        try
        {
            GUI.color = new Color(0.035f, 0.045f, 0.05f, 0.96f);
            GUI.DrawTexture(cardRect, Texture2D.whiteTexture, ScaleMode.StretchToFill);

            GUI.color = new Color(0.14f, 0.16f, 0.17f, 0.98f);
            GUI.DrawTexture(
                new Rect(cardRect.x + 1f, cardRect.y + 1f, cardRect.width - 2f, 2f),
                Texture2D.whiteTexture,
                ScaleMode.StretchToFill);

            GUI.color = accent;
            GUI.DrawTexture(
                new Rect(cardRect.x, cardRect.y, 4f, cardRect.height),
                Texture2D.whiteTexture,
                ScaleMode.StretchToFill);

            var iconRect = new Rect(cardRect.x + 14f, cardRect.y + 17f, 42f, 42f);
            var sprite = HermesIconService.AskHermesIcon;
            if (sprite?.texture is not null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(iconRect, sprite.texture, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.contentColor = accent;
                GUI.Label(iconRect, "H", _overlayTitleStyle);
            }

            var textX = cardRect.x + 68f;
            var textWidth = cardRect.width - 104f;
            GUI.contentColor = new Color(0.63f, 0.76f, 0.82f, 1f);
            GUI.Label(
                new Rect(textX, cardRect.y + 8f, textWidth, 18f),
                $"HERMES  •  {notice.Category.ToUpperInvariant()}",
                _overlayMetaStyle);

            GUI.contentColor = Color.white;
            GUI.Label(
                new Rect(textX, cardRect.y + 25f, textWidth, 24f),
                notice.Title,
                _overlayTitleStyle);

            GUI.contentColor = new Color(0.83f, 0.85f, 0.85f, 1f);
            var bodyHeight = Plugin.Settings.CompactMode.Value ? 31f : 46f;
            GUI.Label(
                new Rect(textX, cardRect.y + 49f, textWidth, bodyHeight),
                notice.Message,
                _overlayBodyStyle);

            GUI.contentColor = new Color(0.56f, 0.70f, 0.76f, 1f);
            GUI.Label(
                new Rect(textX, cardRect.yMax - 19f, textWidth, 15f),
                $"CLICK TO OPEN HERMES  •  {FriendlyTarget(notice.TargetTab)}",
                _overlayHintStyle);

            var closeRect = new Rect(cardRect.xMax - 29f, cardRect.y + 7f, 21f, 21f);
            GUI.contentColor = new Color(0.75f, 0.78f, 0.78f, 1f);
            if (GUI.Button(closeRect, "×", _overlayCloseStyle))
            {
                notice.Dismissed = true;
                return;
            }

            var openLeftRect = new Rect(cardRect.x, cardRect.y, cardRect.width - 34f, cardRect.height);
            var openLowerRightRect = new Rect(cardRect.xMax - 34f, cardRect.y + 33f, 34f, cardRect.height - 33f);
            var tooltip = new GUIContent(string.Empty, $"Open HERMES: {FriendlyTarget(notice.TargetTab)}");
            var openClicked = GUI.Button(openLeftRect, tooltip, GUIStyle.none)
                              || GUI.Button(openLowerRightRect, tooltip, GUIStyle.none);
            if (openClicked)
            {
                notice.Dismissed = true;
                navigate(notice.TargetTab);
            }
        }
        finally
        {
            GUI.color = originalColor;
            GUI.contentColor = originalContentColor;
        }
    }

    private static Color GetSeverityColor(string severity)
    {
        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" or "error" => new Color(0.78f, 0.24f, 0.18f, 1f),
            "warning" => new Color(0.80f, 0.59f, 0.20f, 1f),
            _ => new Color(0.27f, 0.58f, 0.72f, 1f)
        };
    }

    private static string FriendlyTarget(string targetTab)
    {
        return targetTab.Replace('/', ' ').Trim().ToUpperInvariant();
    }

    public void DrawInbox(Action<string> navigate)
    {
        GUILayout.BeginHorizontal();
        if (HermesUi.DrawSectionButton(
                "PROACTIVE NOTICES",
                _inboxExpanded,
                ActiveNoticeCount == 0 ? "CLEAR" : $"{ActiveNoticeCount:N0} ACTIVE"))
        {
            _inboxExpanded = !_inboxExpanded;
        }
        GUILayout.EndHorizontal();

        if (!_inboxExpanded)
        {
            return;
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = !_checking && Plugin.Settings.EnableProactiveAssistantNotices.Value;
        if (GUILayout.Button(_checking ? "Checking..." : "Check now", GUILayout.Width(105f)))
        {
            _ = RefreshNowAsync();
        }
        GUI.enabled = true;
        if (GUILayout.Button("Dismiss all", GUILayout.Width(105f)))
        {
            foreach (var notice in _notices)
            {
                notice.Dismissed = true;
            }
        }
        if (GUILayout.Button("Clear history", GUILayout.Width(105f)))
        {
            Clear();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        HermesUi.DrawStatusLine(_status, _checking);

        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            HermesUi.DrawEmptyState(
                "Proactive notices are disabled.",
                "Enable Assistant Notices → Enable proactive notices in BepInEx/F12 configuration.");
            return;
        }

        var rows = _notices
            .OrderBy(notice => notice.Dismissed)
            .ThenByDescending(notice => notice.SeverityRank)
            .ThenByDescending(notice => notice.CreatedAt)
            .ToList();
        if (rows.Count == 0)
        {
            HermesUi.DrawEmptyState(
                "No notices have been generated.",
                "HERMES only surfaces configured conditions that are currently actionable.");
            return;
        }

        var limited = HermesUi.LimitRows(rows, out var hiddenRows);
        foreach (var notice in limited)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{notice.Severity.ToUpperInvariant()} • {notice.Category}", GUILayout.Width(200f));
            GUILayout.Label(notice.Title, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.Label(notice.Message);
            GUILayout.BeginHorizontal();
            GUI.enabled = !notice.Dismissed;
            if (GUILayout.Button("Open", GUILayout.Width(85f)))
            {
                notice.Dismissed = true;
                navigate(notice.TargetTab);
            }
            if (GUILayout.Button("Dismiss", GUILayout.Width(85f)))
            {
                notice.Dismissed = true;
            }
            GUI.enabled = true;
            if (notice.Dismissed)
            {
                GUILayout.Label("Dismissed");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
        HermesUi.DrawHiddenRowsNotice(hiddenRows);
    }

    private async Task RefreshAsync(bool manual)
    {
        if (_checking)
        {
            return;
        }

        if (!Plugin.Settings.EnableProactiveAssistantNotices.Value)
        {
            _status = "Proactive notices are disabled in F12 configuration.";
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
                _status = profile?.Message ?? "No active PMC profile is available for proactive notices.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_profileToken)
                && !_profileToken.Equals(profile.ContextToken, StringComparison.Ordinal))
            {
                _notices.Clear();
                _activeFingerprints.Clear();
                _lastShownByFingerprint.Clear();
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
                    "Loadout readiness needs attention",
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
                "High-value uninsured equipment detected",
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
                    "Hideout production is ready",
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
                "Hideout upgrades are ready",
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
            $"Profitable craft ready: {best.OutputName}",
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
                "Stash cleanup opportunity",
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
                "Safe-to-sell surplus available",
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
        var cooldownSeconds = Plugin.Settings.GetAssistantNoticeCooldownMinutes() * 60f;

        foreach (var candidate in candidates)
        {
            var isChange = !_activeFingerprints.Contains(candidate.Fingerprint);
            var cooldownElapsed = !_lastShownByFingerprint.TryGetValue(candidate.Fingerprint, out var lastShown)
                                  || now - lastShown >= cooldownSeconds;
            if (Plugin.Settings.OnlyNotifyAssistantNoticeChanges.Value ? !isChange : !cooldownElapsed)
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
            _lastShownByFingerprint[candidate.Fingerprint] = now;
        }

        _activeFingerprints = currentFingerprints;
        if (_notices.Count > MaximumRetainedNotices)
        {
            _notices.RemoveRange(0, _notices.Count - MaximumRetainedNotices);
        }
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

    private void EnsureOverlayStyles()
    {
        _overlayTitleStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontStyle = UnityEngine.FontStyle.Bold,
            fontSize = 13,
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 0, 0)
        };
        _overlayBodyStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            wordWrap = true,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(0, 0, 0, 0)
        };
        _overlayMetaStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontStyle = UnityEngine.FontStyle.Bold,
            fontSize = 10,
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 0, 0)
        };
        _overlayHintStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 9,
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 0, 0)
        };
        _overlayCloseStyle ??= new GUIStyle(GUI.skin.button)
        {
            fontStyle = UnityEngine.FontStyle.Bold,
            fontSize = 15,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(0, 0, 0, 1),
            margin = new RectOffset(0, 0, 0, 0)
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
