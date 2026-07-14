using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesCraftPanel
{
    private enum CraftFilter
    {
        All,
        Ready,
        Profitable,
        Overnight
    }

    private Vector2 _listScroll;
    private Vector2 _detailScroll;
    private HermesCraftsResponse? _response;
    private HermesCraftSummary? _selectedCraft;
    private HermesCraftDetailResponse? _detail;
    private bool _loading;
    private bool _detailLoading;
    private bool _loadRequested;
    private int _refreshVersion;
    private int _detailVersion;
    private string _search = string.Empty;
    private string _status = "Loading hideout recipes...";
    private CraftFilter _filter;

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

        if (_response is null)
        {
            GUILayout.Label(_loading ? "Indexing current hideout recipes and profile readiness..." : "No crafting data loaded.");
            GUILayout.EndVertical();
            return;
        }

        GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
        DrawCraftList(_response);
        GUILayout.Space(8f);
        DrawCraftDetails();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    public void Clear()
    {
        _response = null;
        _selectedCraft = null;
        _detail = null;
        _listScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _search = string.Empty;
        _filter = CraftFilter.All;
        _loadRequested = false;
        _refreshVersion++;
        _detailVersion++;
        _loading = false;
        _detailLoading = false;
        _status = "Loading hideout recipes...";
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
        GUILayout.Label("CRAFTING INTELLIGENCE", GUILayout.Width(190f));
        _search = GUILayout.TextField(_search, GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true), GUILayout.Height(28f));

        DrawFilterButton(CraftFilter.All, "All", 60f);
        DrawFilterButton(CraftFilter.Ready, "Ready", 65f);
        DrawFilterButton(CraftFilter.Profitable, "Profit", 65f);
        DrawFilterButton(CraftFilter.Overnight, "Overnight", 85f);

        GUI.enabled = !_loading;
        if (GUILayout.Button(_loading ? "Refreshing..." : "Refresh", GUILayout.Width(105f), GUILayout.Height(28f)))
        {
            _ = RefreshAsync(true);
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }

    private void DrawFilterButton(CraftFilter filter, string label, float width)
    {
        var selected = _filter == filter;
        if (GUILayout.Button((selected ? "● " : string.Empty) + label, GUILayout.Width(width), GUILayout.Height(28f)))
        {
            _filter = filter;
            _listScroll = Vector2.zero;
        }
    }

    private void DrawCraftList(HermesCraftsResponse response)
    {
        var visible = ApplyFilters(response.Crafts).ToList();

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(410f), GUILayout.ExpandHeight(true));
        GUILayout.Label($"RECIPES — {visible.Count:N0} of {response.TotalCrafts:N0}");
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));

        if (visible.Count == 0)
        {
            GUILayout.Label("No recipes match the current search and filter.");
        }
        else
        {
            foreach (var craft in visible)
            {
                DrawCraftButton(craft);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawCraftButton(HermesCraftSummary craft)
    {
        var selected = _selectedCraft?.CraftKey == craft.CraftKey;
        var prefix = selected ? "▶ " : string.Empty;
        var profit = craft.EstimatedEconomicProfit >= 0
            ? $"+₽{craft.EstimatedEconomicProfit:N0}"
            : $"-₽{Math.Abs(craft.EstimatedEconomicProfit):N0}";
        var label = $"{prefix}{craft.OutputQuantity:N0} × {craft.OutputName}\n{craft.StationName} L{craft.RequiredStationLevel} • {FormatDuration(craft.DurationSeconds)} • Handbook econ {profit}\n{craft.Status}";

        if (GUILayout.Button(label, GUILayout.MinHeight(68f), GUILayout.ExpandWidth(true)))
        {
            _ = SelectCraftAsync(craft);
        }

        GUILayout.Space(3f);
    }

    private void DrawCraftDetails()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("RECIPE DETAILS");
        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

        if (_selectedCraft is null)
        {
            GUILayout.Label("Select a recipe to inspect ingredient sources, readiness, cash profit, and economic profit.");
        }
        else if (_detailLoading)
        {
            GUILayout.Label($"Analyzing {_selectedCraft.OutputName}...");
        }
        else if (_detail is null || !_detail.Found || _detail.Craft is null)
        {
            GUILayout.Label(_detail?.Message ?? "Recipe details are unavailable.");
        }
        else
        {
            DrawDetail(_detail);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private static void DrawDetail(HermesCraftDetailResponse detail)
    {
        var craft = detail.Craft!;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"{craft.OutputQuantity:N0} × {craft.OutputName}");
        GUILayout.Label($"Station: {craft.StationName} Level {craft.RequiredStationLevel}");
        GUILayout.Label($"Base duration: {FormatDuration(craft.DurationSeconds)}");
        GUILayout.Label($"Status: {craft.Status}");
        GUILayout.Label($"Can start now: {(craft.CanStartNow ? "Yes" : "No")}");
        GUILayout.EndVertical();

        if (!string.IsNullOrWhiteSpace(detail.RequiredQuestName))
        {
            GUILayout.Space(6f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("QUEST REQUIREMENT");
            GUILayout.Label($"{(detail.RequiredQuestComplete ? "✓" : "✗")} {detail.RequiredQuestName}");
            GUILayout.EndVertical();
        }

        GUILayout.Space(6f);
        GUILayout.Label("INGREDIENTS");
        if (detail.Ingredients.Count == 0)
        {
            GUILayout.Label("No player-facing item ingredients were returned.");
        }
        else
        {
            foreach (var ingredient in detail.Ingredients)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{(ingredient.IsMet ? "✓" : "✗")} {ingredient.Name}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(ingredient.RequirementType);
                GUILayout.EndHorizontal();
                GUILayout.Label($"Owned: {FormatCount(ingredient.Owned)} • Used by craft: {FormatCount(ingredient.OwnedUsed)} / {FormatCount(ingredient.Required)}");
                GUILayout.Label($"Owned cash cost: ₽0");
                if (ingredient.EstimatedOwnedEconomicValue > 0)
                {
                    GUILayout.Label($"Owned opportunity value: ₽{ingredient.EstimatedOwnedEconomicValue:N0}");
                }

                if (ingredient.IsReusableTool)
                {
                    GUILayout.Label("Reusable tool: not charged as a consumed economic input.");
                }

                if (!ingredient.IsMet)
                {
                    GUILayout.Label($"Missing: {FormatCount(ingredient.Missing)}");
                    if (ingredient.AcquisitionPlan.Count == 0)
                    {
                        GUILayout.Label("Purchase source: unavailable");
                    }
                    else
                    {
                        GUILayout.Label("ACQUISITION PLAN");
                        foreach (var line in ingredient.AcquisitionPlan)
                        {
                            var fallback = line.IsFallback ? " (fallback only)" : string.Empty;
                            GUILayout.Label(
                                $"{FormatCount(line.Quantity)} × {line.Source}{fallback} — ₽{line.UnitPrice:N0} each — ₽{line.TotalCost:N0}");
                        }
                    }

                    if (ingredient.UnavailableQuantity > 0d)
                    {
                        GUILayout.Label($"No current trader/flea source: {FormatCount(ingredient.UnavailableQuantity)}");
                    }

                    GUILayout.Label($"Additional cash estimate: ₽{ingredient.EstimatedPurchaseCost:N0}");
                }

                if (ingredient.FoundInRaidRequired)
                {
                    GUILayout.Label("Found in raid required");
                }

                if (!string.IsNullOrWhiteSpace(ingredient.CostNote))
                {
                    GUILayout.Label(ingredient.CostNote);
                }

                GUILayout.EndVertical();
                GUILayout.Space(3f);
            }
        }

        GUILayout.Space(6f);
        GUILayout.Label("VALUE ANALYSIS");
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"Additional cash required: ₽{craft.EstimatedAdditionalCashCost:N0}");
        GUILayout.Label($"Owned ingredient opportunity value: ₽{craft.EstimatedOwnedIngredientValue:N0}");
        GUILayout.Label($"Total economic input value: ₽{craft.EstimatedEconomicInputValue:N0}");
        GUILayout.Label($"Estimated output value: ₽{craft.EstimatedOutputValue:N0}");
        GUILayout.Space(4f);
        var cashLabel = craft.AcquisitionPlanComplete ? "Cash" : "Known cash";
        GUILayout.Label(craft.EstimatedCashProfit >= 0
            ? $"{cashLabel} profit: ₽{craft.EstimatedCashProfit:N0}"
            : $"{cashLabel} loss: ₽{Math.Abs(craft.EstimatedCashProfit):N0}");
        GUILayout.Label(craft.EstimatedEconomicProfit >= 0
            ? $"Economic profit: ₽{craft.EstimatedEconomicProfit:N0}"
            : $"Economic loss: ₽{Math.Abs(craft.EstimatedEconomicProfit):N0}");
        GUILayout.Label(craft.EstimatedEconomicProfitPerHour >= 0
            ? $"Economic profit per hour: ₽{craft.EstimatedEconomicProfitPerHour:N0}"
            : $"Economic loss per hour: ₽{Math.Abs(craft.EstimatedEconomicProfitPerHour):N0}");
        if (!craft.AcquisitionPlanComplete)
        {
            GUILayout.Label("Warning: one or more missing ingredients cannot currently be purchased; the acquisition plan is incomplete.");
        }

        GUILayout.Label(detail.ValuationBasis);
        GUILayout.EndVertical();
    }

    private IEnumerable<HermesCraftSummary> ApplyFilters(IEnumerable<HermesCraftSummary> source)
    {
        var query = _search.Trim();
        foreach (var craft in source)
        {
            if (query.Length > 0
                && !craft.OutputName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !craft.StationName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var visible = _filter switch
            {
                CraftFilter.Ready => craft.CanStartNow,
                CraftFilter.Profitable => craft.EstimatedEconomicProfit > 0,
                CraftFilter.Overnight => craft.DurationSeconds >= 4 * 60 * 60
                                         && craft.DurationSeconds <= 12 * 60 * 60,
                _ => true
            };

            if (visible)
            {
                yield return craft;
            }
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
            ? "Refreshing recipes and current market data..."
            : "Loading recipe summaries and checking the active PMC inventory...";
        HermesCraftSummary? nextCraft = null;

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

            var response = await HermesApiClient.GetCraftsAsync();
            if (requestVersion != _refreshVersion)
            {
                return;
            }

            _response = response;
            _status = response.Found
                ? $"Loaded {response.TotalCrafts:N0} recipe(s). Summary values use fast handbook estimates; select a recipe for live trader and flea sourcing."
                : response.Message ?? "Crafting information is unavailable.";

            if (response.Found && response.Crafts.Count > 0)
            {
                var oldKey = _selectedCraft?.CraftKey;
                nextCraft = string.IsNullOrWhiteSpace(oldKey)
                    ? null
                    : response.Crafts.FirstOrDefault(craft => craft.CraftKey == oldKey);

                if (nextCraft is null)
                {
                    _selectedCraft = null;
                    _detail = null;
                    _detailLoading = false;
                }
            }
            else
            {
                _selectedCraft = null;
                _detail = null;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion != _refreshVersion)
            {
                return;
            }

            _response = null;
            _selectedCraft = null;
            _detail = null;
            _status = HermesApiClient.DescribeFailure(ex, "Craft list refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _refreshVersion)
            {
                _loading = false;
            }
        }

        if (nextCraft is not null && requestVersion == _refreshVersion)
        {
            _ = SelectCraftAsync(nextCraft);
        }
    }

    private async Task SelectCraftAsync(HermesCraftSummary craft)
    {
        if (_detailLoading && _selectedCraft?.CraftKey == craft.CraftKey)
        {
            return;
        }

        var requestVersion = ++_detailVersion;
        _selectedCraft = craft;
        _detail = null;
        _detailLoading = true;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetCraftDetailAsync(craft.CraftKey);
            if (requestVersion == _detailVersion
                && _selectedCraft?.CraftKey == craft.CraftKey)
            {
                _detail = response;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == _detailVersion
                && _selectedCraft?.CraftKey == craft.CraftKey)
            {
                _detail = new HermesCraftDetailResponse
                {
                    Found = false,
                    Message = HermesApiClient.DescribeFailure(ex, "Craft analysis")
                };
            }

            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _detailVersion
                && _selectedCraft?.CraftKey == craft.CraftKey)
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
