using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesHideoutPanel
{
    private Vector2 _areaScroll;
    private Vector2 _detailScroll;
    private HermesHideoutSummaryResponse? _summary;
    private HermesHideoutAreaSummary? _selectedArea;
    private HermesHideoutAreaDetailResponse? _detail;
    private bool _loading;
    private bool _detailLoading;
    private bool _loadRequested;
    private int _refreshVersion;
    private int _detailVersion;
    private string _status = "Loading current hideout status...";

    public void Draw()
    {
        if (!_loadRequested)
        {
            _loadRequested = true;
            _ = RefreshAsync(false);
        }

        GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
        DrawToolbar();
        GUILayout.Space(4f);
        GUILayout.Label(_status);
        GUILayout.Space(6f);

        if (_summary is null)
        {
            GUILayout.Label(_loading ? "Reading the active PMC hideout profile..." : "No hideout data loaded.");
            GUILayout.EndVertical();
            return;
        }

        DrawTopSummary(_summary);
        GUILayout.Space(6f);
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
        _loadRequested = false;
        _refreshVersion++;
        _detailVersion++;
        _loading = false;
        _detailLoading = false;
        _status = "Loading current hideout status...";
    }

    public Task RefreshFromServerAsync(
        bool invalidateMarketCache = true,
        bool force = false)
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
        GUILayout.Label("HIDEOUT OPERATIONS", GUILayout.ExpandWidth(true));
        GUI.enabled = !_loading;
        if (GUILayout.Button(_loading ? "Refreshing..." : "Refresh", GUILayout.Width(110f), GUILayout.Height(28f)))
        {
            _ = RefreshAsync(true);
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }

    private static void DrawTopSummary(HermesHideoutSummaryResponse summary)
    {
        GUILayout.BeginHorizontal();
        DrawMetric("READY TO UPGRADE", summary.ReadyAreaCount.ToString("N0"));
        DrawMetric("MISSING MATERIALS", summary.MaterialBlockedAreaCount.ToString("N0"));
        DrawMetric("PROGRESSION BLOCKED", summary.ProgressionBlockedAreaCount.ToString("N0"));
        DrawMetric("ACTIVE PRODUCTIONS", summary.Resources.ActiveProductionCount.ToString("N0"));
        GUILayout.EndHorizontal();
    }

    private static void DrawMetric(string label, string value)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(150f), GUILayout.ExpandWidth(true));
        GUILayout.Label(label);
        GUILayout.Label(value);
        GUILayout.EndVertical();
    }

    private void DrawAreaList(HermesHideoutSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(350f), GUILayout.ExpandHeight(true));
        GUILayout.Label("AREAS");
        _areaScroll = GUILayout.BeginScrollView(_areaScroll, GUILayout.ExpandHeight(true));

        if (summary.Areas.Count == 0)
        {
            GUILayout.Label("No hideout areas were returned.");
        }
        else
        {
            foreach (var area in summary.Areas)
            {
                var selected = _selectedArea?.AreaKey == area.AreaKey;
                var target = area.TargetLevel.HasValue
                    ? $" → L{area.TargetLevel.Value}"
                    : string.Empty;
                var line = $"{(selected ? "▶ " : string.Empty)}{area.Name}  L{area.CurrentLevel}{target}\n{area.Status}";
                if (GUILayout.Button(line, GUILayout.MinHeight(50f), GUILayout.ExpandWidth(true)))
                {
                    _ = SelectAreaAsync(area);
                }

                GUILayout.Space(3f);
            }
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
            GUILayout.Label("Select a hideout area to inspect its next upgrade requirements.");
        }
        else if (_detailLoading)
        {
            GUILayout.Label($"Analyzing {_selectedArea.Name} requirements and current acquisition sources...");
        }
        else if (_detail is null || !_detail.Found || _detail.Area is null)
        {
            GUILayout.Label(_detail?.Message ?? "Area details are unavailable.");
        }
        else
        {
            DrawSelectedArea(_detail);
        }

        GUILayout.Space(10f);
        DrawResources(summary.Resources);
        GUILayout.Space(8f);
        DrawProductions(summary.ActiveProductions);

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private static void DrawSelectedArea(HermesHideoutAreaDetailResponse detail)
    {
        var area = detail.Area!;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(area.Name);
        GUILayout.Label($"Current level: {area.CurrentLevel} / {area.MaximumLevel}");
        GUILayout.Label($"Status: {area.Status}");

        if (area.TargetLevel.HasValue)
        {
            GUILayout.Label($"Next upgrade: Level {area.TargetLevel.Value}");
            GUILayout.Label($"Construction time: {FormatDuration(detail.ConstructionSeconds)}");
        }

        if (area.IsConstructing && area.SecondsUntilComplete.HasValue)
        {
            GUILayout.Label($"Upgrade completes in: {FormatDuration(area.SecondsUntilComplete.Value)}");
        }

        if (detail.EstimatedMissingAcquisitionCost > 0)
        {
            GUILayout.Label($"Estimated missing-material cost: ₽{detail.EstimatedMissingAcquisitionCost:N0}");
        }

        GUILayout.EndVertical();

        GUILayout.Space(6f);
        GUILayout.Label("REQUIREMENTS");
        if (detail.Requirements.Count == 0)
        {
            GUILayout.Label(area.TargetLevel.HasValue
                ? "No player-facing requirements were returned for this stage."
                : "This area has reached its maximum level.");
            return;
        }

        foreach (var requirement in detail.Requirements)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{(requirement.IsMet ? "✓" : "✗")} {requirement.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label(requirement.Type);
            GUILayout.EndHorizontal();

            if (requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label($"Owned: {FormatCount(requirement.Owned)} / {FormatCount(requirement.Required)}");
                if (!requirement.IsMet)
                {
                    GUILayout.Label($"Missing: {FormatCount(requirement.Missing)}");
                }

                if (requirement.FoundInRaidRequired)
                {
                    GUILayout.Label("Found in raid required");
                }

                if (!string.IsNullOrWhiteSpace(requirement.AcquisitionSource) && requirement.UnitPrice.HasValue)
                {
                    GUILayout.Label($"Cheapest current source: {requirement.AcquisitionSource} — ₽{requirement.UnitPrice.Value:N0} each");
                }

                if (requirement.EstimatedMissingCost is > 0)
                {
                    GUILayout.Label($"Estimated missing cost: ₽{requirement.EstimatedMissingCost.Value:N0}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(requirement.Details))
            {
                GUILayout.Label(requirement.Details);
            }

            GUILayout.EndVertical();
            GUILayout.Space(3f);
        }
    }

    private static void DrawResources(HermesHideoutResourceSummary resources)
    {
        GUILayout.Label("RESOURCES");
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"Generator: {(resources.GeneratorActive ? "Running" : "Inactive")}");
        GUILayout.Label($"Fuel containers installed: {resources.FuelContainerCount:N0}");
        GUILayout.Label($"Fuel resource remaining: {resources.FuelResourceRemaining:N2}");
        GUILayout.Label(resources.EstimatedGeneratorRuntimeSeconds.HasValue
            ? $"Estimated generator runtime: {FormatDuration(resources.EstimatedGeneratorRuntimeSeconds.Value)}"
            : "Estimated generator runtime: unavailable");

        if (resources.AirFilterCounter.HasValue)
        {
            GUILayout.Label($"Air filter counter: {resources.AirFilterCounter.Value:N0}");
        }

        if (resources.WaterFilterCounter.HasValue)
        {
            GUILayout.Label($"Water filter counter: {resources.WaterFilterCounter.Value:N0}");
        }

        GUILayout.Label($"Completed productions waiting: {resources.CompletedProductionCount:N0}");
        GUILayout.EndVertical();
    }

    private static void DrawProductions(IReadOnlyList<HermesActiveProductionSummary> productions)
    {
        GUILayout.Label("ACTIVE PRODUCTIONS");
        if (productions.Count == 0)
        {
            GUILayout.Label("No active or completed hideout productions were found.");
            return;
        }

        foreach (var production in productions)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(production.StationName);
            GUILayout.FlexibleSpace();
            GUILayout.Label(production.Status);
            GUILayout.EndHorizontal();
            GUILayout.Label($"{production.OutputQuantity:N0} × {production.OutputName}");
            GUILayout.Label(production.IsComplete
                ? "Ready to collect"
                : $"Time remaining: {FormatDuration(production.SecondsRemaining)}");
            GUILayout.EndVertical();
            GUILayout.Space(3f);
        }
    }

    private async Task RefreshAsync(bool invalidateMarketCache)
    {
        if (_loading)
        {
            return;
        }

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
                if (requestVersion != _refreshVersion)
                {
                    return;
                }
            }

            var response = await HermesApiClient.GetHideoutSummaryAsync();
            if (requestVersion != _refreshVersion)
            {
                return;
            }

            _summary = response;
            _status = response.Found
                ? $"Loaded {response.Areas.Count:N0} hideout area(s) and {response.ActiveProductions.Count:N0} production(s)."
                : response.Message ?? "Hideout information is unavailable.";

            if (response.Found && response.Areas.Count > 0)
            {
                var previouslySelected = _selectedArea?.AreaKey;
                var next = response.Areas.FirstOrDefault(area => area.AreaKey == previouslySelected)
                           ?? response.Areas.FirstOrDefault(area => area.Status == "Ready to upgrade")
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
            if (requestVersion != _refreshVersion)
            {
                return;
            }

            _summary = null;
            _selectedArea = null;
            _detail = null;
            _status = HermesApiClient.DescribeFailure(ex, "Hideout refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _refreshVersion)
            {
                _loading = false;
            }
        }
    }

    private async Task SelectAreaAsync(HermesHideoutAreaSummary area)
    {
        if (_detailLoading && _selectedArea?.AreaKey == area.AreaKey)
        {
            return;
        }

        var requestVersion = ++_detailVersion;
        _selectedArea = area;
        _detail = null;
        _detailLoading = true;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetHideoutAreaAsync(area.AreaKey);
            if (requestVersion == _detailVersion
                && _selectedArea?.AreaKey == area.AreaKey)
            {
                _detail = response;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == _detailVersion
                && _selectedArea?.AreaKey == area.AreaKey)
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
            if (requestVersion == _detailVersion
                && _selectedArea?.AreaKey == area.AreaKey)
            {
                _detailLoading = false;
            }
        }
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0L, seconds);
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1d)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }

        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1d)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{duration.Seconds}s";
    }

    private static string FormatCount(double count)
    {
        return Math.Abs(count - Math.Round(count)) < 0.001d
            ? Math.Round(count).ToString("N0")
            : count.ToString("N2");
    }
}
