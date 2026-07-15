using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesHideoutPanel
{
    private enum HideoutFilter
    {
        All,
        Actionable,
        Ready,
        MissingMaterials,
        ProgressionBlocked,
        InProgress,
        Completed
    }

    private Vector2 _areaScroll;
    private Vector2 _detailScroll;
    private HermesHideoutSummaryResponse? _summary;
    private HermesHideoutAreaSummary? _selectedArea;
    private HermesHideoutAreaDetailResponse? _detail;
    private bool _loading;
    private bool _detailLoading;
    private bool _loadRequested;
    private bool _requirementsExpanded;
    private bool _resourcesExpanded;
    private bool _productionsExpanded;
    private int _refreshVersion;
    private int _detailVersion;
    private string _search = string.Empty;
    private string _status = "Loading current hideout status...";
    private HideoutFilter _filter;

    public HermesHideoutPanel()
    {
        _filter = ParseFilter(Plugin.Settings.DefaultHideoutFilter.Value);
        var expanded = !Plugin.Settings.CollapseSectionsByDefault.Value;
        _requirementsExpanded = expanded;
        _resourcesExpanded = expanded;
        _productionsExpanded = expanded;
    }

    public void Draw()
    {
        if (!_loadRequested)
        {
            _loadRequested = true;
            _ = RefreshAsync(false);
        }

        GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
        HermesUi.DrawPanelHeader(
            "HIDEOUT OPERATIONS",
            "Filter current, actionable, blocked, constructing, and completed areas; inspect missing requirements and acquisition estimates.",
            _status,
            _loading,
            () => _ = RefreshAsync(true));
        DrawToolbar();
        GUILayout.Space(HermesUi.StandardSpace);

        if (_summary is null)
        {
            HermesUi.DrawEmptyState(
                _loading ? "Reading the active PMC hideout profile..." : "No hideout data loaded.",
                "Refresh the tab after the SPT server and PMC profile are fully loaded.");
            GUILayout.EndVertical();
            return;
        }

        DrawTopSummary(_summary);
        GUILayout.Space(HermesUi.StandardSpace);
        GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
        DrawAreaList(_summary);
        GUILayout.Space(8f);
        DrawAreaDetails(_summary);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    public void Clear()
    {
        _summary = null;
        _selectedArea = null;
        _detail = null;
        _areaScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _search = string.Empty;
        _filter = ParseFilter(Plugin.Settings.DefaultHideoutFilter.Value);
        _loadRequested = false;
        _refreshVersion++;
        _detailVersion++;
        _loading = false;
        _detailLoading = false;
        _status = "Loading current hideout status...";
    }

    public Task RefreshFromServerAsync(bool invalidateMarketCache = true, bool force = false)
    {
        if (force)
        {
            _refreshVersion++;
            _detailVersion++;
            _loading = false;
            _detailLoading = false;
        }

        _loadRequested = true;
        return RefreshAsync(invalidateMarketCache);
    }

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("SEARCH", GUILayout.Width(58f));
        _search = GUILayout.TextField(
            _search,
            GUILayout.MinWidth(180f),
            GUILayout.ExpandWidth(true),
            GUILayout.Height(HermesUi.ToolbarHeight));
        if (GUILayout.Button("Clear", GUILayout.Width(70f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _search = string.Empty;
            _areaScroll = Vector2.zero;
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("SHOW", GUILayout.Width(48f));
        DrawFilterButton(HideoutFilter.All, "All", 62f);
        DrawFilterButton(HideoutFilter.Actionable, "Actionable", 94f);
        DrawFilterButton(HideoutFilter.Ready, "Ready", 70f);
        DrawFilterButton(HideoutFilter.MissingMaterials, "Missing", 78f);
        DrawFilterButton(HideoutFilter.ProgressionBlocked, "Progression", 100f);
        DrawFilterButton(HideoutFilter.InProgress, "In Progress", 98f);
        DrawFilterButton(HideoutFilter.Completed, "Completed", 92f);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawFilterButton(HideoutFilter filter, string label, float width)
    {
        if (GUILayout.Button(
                (_filter == filter ? "● " : string.Empty) + label,
                GUILayout.Width(width),
                GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _filter = filter;
            _areaScroll = Vector2.zero;
        }
    }

    private static void DrawTopSummary(HermesHideoutSummaryResponse summary)
    {
        var quickMissingValue = summary.Areas.Sum(area => Math.Max(0L, area.EstimatedMissingHandbookCost));
        GUILayout.BeginHorizontal();
        HermesUi.DrawMetric("READY TO UPGRADE", summary.ReadyAreaCount.ToString("N0"), null, 145f);
        HermesUi.DrawMetric("MISSING MATERIALS", summary.MaterialBlockedAreaCount.ToString("N0"), null, 145f);
        HermesUi.DrawMetric("PROGRESSION BLOCKED", summary.ProgressionBlockedAreaCount.ToString("N0"), null, 155f);
        HermesUi.DrawMetric("ACTIVE PRODUCTIONS", summary.Resources.ActiveProductionCount.ToString("N0"), null, 145f);
        HermesUi.DrawMetric("QUICK MISSING VALUE", $"₽{quickMissingValue:N0}", "Handbook summary; select an area for live sourcing.", 170f);
        GUILayout.EndHorizontal();
    }

    private void DrawAreaList(HermesHideoutSummaryResponse summary)
    {
        var areas = ApplyFilters(summary.Areas).ToList();
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(365f), GUILayout.ExpandHeight(true));
        GUILayout.Label($"AREAS — {areas.Count:N0} of {summary.Areas.Count:N0}");
        _areaScroll = GUILayout.BeginScrollView(_areaScroll, GUILayout.ExpandHeight(true));

        if (areas.Count == 0)
        {
            HermesUi.DrawEmptyState("No hideout areas match the current search and filter.");
        }
        else
        {
            var visibleAreas = HermesUi.LimitRows(areas, out var hiddenAreas);
            foreach (var area in visibleAreas)
            {
                var selected = _selectedArea?.AreaKey == area.AreaKey;
                var target = area.TargetLevel.HasValue ? $" → L{area.TargetLevel.Value}" : string.Empty;
                var badge = GetStatusBadge(area);
                var line = $"{(selected ? "▶ " : string.Empty)}{area.Name}  L{area.CurrentLevel}{target}\n[{badge}] {area.Status}";
                if (area.MissingItemTypes > 0)
                {
                    line += $" • {area.MissingItemTypes:N0} missing type(s)";
                }

                if (GUILayout.Button(line, GUILayout.MinHeight(58f), GUILayout.ExpandWidth(true)))
                {
                    _ = SelectAreaAsync(area);
                }
                GUILayout.Space(HermesUi.SmallSpace);
            }
            HermesUi.DrawHiddenRowsNotice(hiddenAreas);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawAreaDetails(HermesHideoutSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("AREA DETAILS");
        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

        if (_selectedArea is null)
        {
            HermesUi.DrawEmptyState("Select a hideout area to inspect its next upgrade requirements.");
        }
        else if (_detailLoading)
        {
            HermesUi.DrawStatusLine($"Analyzing {_selectedArea.Name} requirements and current acquisition sources...", true);
        }
        else if (_detail is null || !_detail.Found || _detail.Area is null)
        {
            HermesUi.DrawError(_detail?.Message ?? "Area details are unavailable.");
        }
        else
        {
            DrawSelectedArea(_detail);
        }

        GUILayout.Space(HermesUi.StandardSpace);
        if (HermesUi.DrawSectionButton("RESOURCES", _resourcesExpanded, summary.Resources.GeneratorActive ? "GENERATOR ON" : "GENERATOR OFF"))
        {
            _resourcesExpanded = !_resourcesExpanded;
        }
        if (_resourcesExpanded)
        {
            DrawResources(summary.Resources);
        }

        GUILayout.Space(HermesUi.StandardSpace);
        if (HermesUi.DrawSectionButton("ACTIVE PRODUCTIONS", _productionsExpanded, summary.ActiveProductions.Count.ToString("N0")))
        {
            _productionsExpanded = !_productionsExpanded;
        }
        if (_productionsExpanded)
        {
            DrawProductions(summary.ActiveProductions);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawSelectedArea(HermesHideoutAreaDetailResponse detail)
    {
        var area = detail.Area!;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(area.Name);
        GUILayout.FlexibleSpace();
        GUILayout.Label(GetStatusBadge(area));
        GUILayout.EndHorizontal();
        GUILayout.Label($"Current level: {area.CurrentLevel} / {area.MaximumLevel}");
        GUILayout.Label($"Status: {area.Status}");
        if (area.TargetLevel.HasValue)
        {
            GUILayout.Label($"Next upgrade: Level {area.TargetLevel.Value} • Construction: {FormatDuration(detail.ConstructionSeconds)}");
        }
        if (area.IsConstructing && area.SecondsUntilComplete.HasValue)
        {
            GUILayout.Label($"Upgrade completes in: {FormatDuration(area.SecondsUntilComplete.Value)}");
        }
        if (Plugin.Settings.ShowHideoutAcquisitionPlans.Value && detail.EstimatedMissingAcquisitionCost > 0)
        {
            GUILayout.Label($"Estimated current missing-material cost: ₽{detail.EstimatedMissingAcquisitionCost:N0}");
        }
        GUILayout.EndVertical();

        var requirements = detail.Requirements
            .Where(requirement => !Plugin.Settings.ShowOnlyMissingHideoutRequirements.Value || !requirement.IsMet)
            .OrderBy(requirement => requirement.IsMet)
            .ThenByDescending(requirement => requirement.Missing)
            .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        GUILayout.Space(HermesUi.StandardSpace);
        if (HermesUi.DrawSectionButton("REQUIREMENTS", _requirementsExpanded, $"{requirements.Count:N0} shown"))
        {
            _requirementsExpanded = !_requirementsExpanded;
        }
        if (!_requirementsExpanded)
        {
            return;
        }

        if (requirements.Count == 0)
        {
            HermesUi.DrawEmptyState(
                area.TargetLevel.HasValue ? "No requirements match the current display settings." : "This area has reached its maximum level.");
            return;
        }

        var visibleRequirements = HermesUi.LimitRows(requirements, out var hiddenRequirements);
        foreach (var requirement in visibleRequirements)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{(requirement.IsMet ? "✓" : "✗")} {requirement.Name}");
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrWhiteSpace(requirement.ItemTemplateId)
                && GUILayout.Button("Ask HERMES", GUILayout.Width(104f)))
            {
                Plugin.Instance?.OpenForPreviewItem(requirement.ItemTemplateId, "hideout requirement");
            }
            GUILayout.Label(requirement.Type);
            GUILayout.EndHorizontal();

            if (requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label($"Owned: {FormatCount(requirement.Owned)} / {FormatCount(requirement.Required)}" +
                                (requirement.Missing > 0d ? $" • Missing: {FormatCount(requirement.Missing)}" : string.Empty));
                if (requirement.FoundInRaidRequired)
                {
                    GUILayout.Label("FIR REQUIRED");
                }
                if (Plugin.Settings.ShowHideoutAcquisitionPlans.Value && requirement.EstimatedMissingCost is > 0)
                {
                    GUILayout.Label($"Estimated missing cost: ₽{requirement.EstimatedMissingCost.Value:N0}");
                }
                if (Plugin.Settings.ShowHideoutDetailedPriceSources.Value
                    && !string.IsNullOrWhiteSpace(requirement.AcquisitionSource)
                    && requirement.UnitPrice.HasValue)
                {
                    GUILayout.Label($"Price source: {requirement.AcquisitionSource} • ₽{requirement.UnitPrice.Value:N0} each");
                }
            }
            else if (!string.IsNullOrWhiteSpace(requirement.Details))
            {
                GUILayout.Label(requirement.Details);
            }
            GUILayout.EndVertical();
            GUILayout.Space(HermesUi.SmallSpace);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenRequirements);
    }

    private IEnumerable<HermesHideoutAreaSummary> ApplyFilters(IEnumerable<HermesHideoutAreaSummary> source)
    {
        var query = _search.Trim();
        return source
            .Where(area => query.Length == 0
                           || area.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || area.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(area => Plugin.Settings.ShowCompletedHideoutAreas.Value
                           || _filter == HideoutFilter.Completed
                           || !IsCompleted(area))
            .Where(area => _filter switch
            {
                HideoutFilter.Actionable => IsReady(area) || IsMissing(area) || area.IsConstructing,
                HideoutFilter.Ready => IsReady(area),
                HideoutFilter.MissingMaterials => IsMissing(area),
                HideoutFilter.ProgressionBlocked => IsProgressionBlocked(area),
                HideoutFilter.InProgress => area.IsConstructing,
                HideoutFilter.Completed => IsCompleted(area),
                _ => true
            })
            .OrderBy(AreaRank)
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static int AreaRank(HermesHideoutAreaSummary area)
    {
        if (IsReady(area)) return 0;
        if (area.IsConstructing) return 1;
        if (IsMissing(area)) return 2;
        if (IsProgressionBlocked(area)) return 3;
        if (IsCompleted(area)) return 5;
        return 4;
    }

    private static bool IsReady(HermesHideoutAreaSummary area) =>
        area.Status.Contains("ready", StringComparison.OrdinalIgnoreCase) && !area.IsConstructing;

    private static bool IsMissing(HermesHideoutAreaSummary area) =>
        area.MissingItemTypes > 0 || area.Status.Contains("missing", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleted(HermesHideoutAreaSummary area) =>
        area.CurrentLevel >= area.MaximumLevel
        || area.Status.Contains("complete", StringComparison.OrdinalIgnoreCase)
        || area.Status.Contains("maximum", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgressionBlocked(HermesHideoutAreaSummary area) =>
        !IsCompleted(area)
        && !area.IsConstructing
        && !IsReady(area)
        && !IsMissing(area)
        && (area.Status.Contains("progress", StringComparison.OrdinalIgnoreCase)
            || area.Status.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || area.TargetLevel.HasValue);

    private static string GetStatusBadge(HermesHideoutAreaSummary area)
    {
        if (IsCompleted(area)) return "COMPLETED";
        if (area.IsConstructing) return "IN PROGRESS";
        if (IsReady(area)) return "READY";
        if (IsMissing(area)) return "MISSING ITEMS";
        if (IsProgressionBlocked(area)) return "PROGRESSION LOCKED";
        return "AVAILABLE";
    }

    private static HideoutFilter ParseFilter(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "actionable" => HideoutFilter.Actionable,
            "ready" => HideoutFilter.Ready,
            "missing" or "missing materials" => HideoutFilter.MissingMaterials,
            "progression" or "progression blocked" => HideoutFilter.ProgressionBlocked,
            "in progress" or "constructing" => HideoutFilter.InProgress,
            "completed" => HideoutFilter.Completed,
            _ => HideoutFilter.All
        };
    }

    private static void DrawResources(HermesHideoutResourceSummary resources)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"Generator: {(resources.GeneratorActive ? "Running" : "Inactive")}");
        GUILayout.Label($"Fuel containers: {resources.FuelContainerCount:N0} • Remaining resource: {resources.FuelResourceRemaining:N2}");
        GUILayout.Label(resources.EstimatedGeneratorRuntimeSeconds.HasValue
            ? $"Estimated generator runtime: {FormatDuration(resources.EstimatedGeneratorRuntimeSeconds.Value)}"
            : "Estimated generator runtime: unavailable");
        if (resources.AirFilterCounter.HasValue) GUILayout.Label($"Air filter counter: {resources.AirFilterCounter.Value:N0}");
        if (resources.WaterFilterCounter.HasValue) GUILayout.Label($"Water filter counter: {resources.WaterFilterCounter.Value:N0}");
        GUILayout.Label($"Completed productions waiting: {resources.CompletedProductionCount:N0}");
        GUILayout.EndVertical();
    }

    private static void DrawProductions(IReadOnlyList<HermesActiveProductionSummary> productions)
    {
        if (productions.Count == 0)
        {
            HermesUi.DrawEmptyState("No active or completed hideout productions were found.");
            return;
        }

        var ordered = productions
            .OrderByDescending(production => production.IsComplete)
            .ThenBy(production => production.SecondsRemaining)
            .ThenBy(production => production.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visible = HermesUi.LimitRows(ordered, out var hidden);
        foreach (var production in visible)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(production.StationName);
            GUILayout.FlexibleSpace();
            GUILayout.Label(production.IsComplete ? "READY TO COLLECT" : production.Status);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{production.OutputQuantity:N0} × {production.OutputName}");
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrWhiteSpace(production.OutputTemplateId)
                && GUILayout.Button("Ask HERMES", GUILayout.Width(104f)))
            {
                Plugin.Instance?.OpenForPreviewItem(production.OutputTemplateId, "hideout production output");
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(production.IsComplete ? "Ready to collect" : $"Time remaining: {FormatDuration(production.SecondsRemaining)}");
            GUILayout.EndVertical();
        }
        HermesUi.DrawHiddenRowsNotice(hidden);
    }

    private async Task RefreshAsync(bool invalidateMarketCache)
    {
        if (_loading) return;
        var requestVersion = ++_refreshVersion;
        _detailVersion++;
        _loading = true;
        _detailLoading = false;
        _status = invalidateMarketCache
            ? "Refreshing current hideout and market data..."
            : "Reading current hideout areas, productions, and resources...";

        try
        {
            if (invalidateMarketCache)
            {
                await HermesApiClient.ClearCachesAsync();
                if (requestVersion != _refreshVersion) return;
            }

            var response = await HermesApiClient.GetHideoutSummaryAsync();
            if (requestVersion != _refreshVersion) return;
            _summary = response;
            _status = response.Found
                ? $"Loaded {response.Areas.Count:N0} hideout area(s) and {response.ActiveProductions.Count:N0} production(s)."
                : response.Message ?? "Hideout information is unavailable.";

            if (response.Found && response.Areas.Count > 0)
            {
                var previous = _selectedArea?.AreaKey;
                var candidates = ApplyFilters(response.Areas).ToList();
                var next = response.Areas.FirstOrDefault(area => area.AreaKey == previous)
                           ?? candidates.FirstOrDefault()
                           ?? response.Areas[0];
                await SelectAreaAsync(next);
            }
            else
            {
                _selectedArea = null;
                _detail = null;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion != _refreshVersion) return;
            _summary = null;
            _selectedArea = null;
            _detail = null;
            _status = HermesApiClient.DescribeFailure(ex, "Hideout refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _refreshVersion) _loading = false;
        }
    }

    private async Task SelectAreaAsync(HermesHideoutAreaSummary area)
    {
        if (_detailLoading && _selectedArea?.AreaKey == area.AreaKey) return;
        var requestVersion = ++_detailVersion;
        _selectedArea = area;
        _detail = null;
        _detailLoading = true;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetHideoutAreaAsync(area.AreaKey);
            if (requestVersion == _detailVersion && _selectedArea?.AreaKey == area.AreaKey) _detail = response;
        }
        catch (Exception ex)
        {
            if (requestVersion == _detailVersion && _selectedArea?.AreaKey == area.AreaKey)
            {
                _detail = new HermesHideoutAreaDetailResponse
                {
                    Found = false,
                    Message = HermesApiClient.DescribeFailure(ex, "Hideout area analysis")
                };
            }
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _detailVersion && _selectedArea?.AreaKey == area.AreaKey) _detailLoading = false;
        }
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0L, seconds);
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1d) return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1d) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1d) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    private static string FormatCount(double count) =>
        Math.Abs(count - Math.Round(count)) < 0.001d ? Math.Round(count).ToString("N0") : count.ToString("N2");
}
