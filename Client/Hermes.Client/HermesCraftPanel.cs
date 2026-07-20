using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesCraftPanel
{
    private enum CraftFilter
    {
        All,
        Available,
        Ready,
        Profitable,
        Overnight,
        Active
    }

    private enum CraftSort
    {
        Name,
        Station,
        Duration,
        Profit,
        ProfitPercent,
        ProfitPerHour,
        MissingIngredients
    }

    private Vector2 _listScroll;
    private Vector2 _detailScroll;
    private HermesCraftsResponse? _response;
    private HermesCraftSummary? _selectedCraft;
    private HermesCraftDetailResponse? _detail;
    private bool _loading;
    private bool _detailLoading;
    private bool _loadRequested;
    private bool _ingredientsExpanded;
    private bool _valueExpanded;
    private int _refreshVersion;
    private int _detailVersion;
    private string _search = string.Empty;
    private readonly HashSet<string> _focusedCraftKeys = new(StringComparer.OrdinalIgnoreCase);
    private int _focusVersion;
    private string _status = "Loading hideout recipes...";
    private CraftFilter _filter;
    private CraftSort _sort;

    public HermesCraftPanel()
    {
        _filter = ParseFilter(Plugin.Settings.DefaultCraftFilter.Value);
        _sort = ParseSort(Plugin.Settings.DefaultCraftSorting.Value);
        var expanded = !Plugin.Settings.CollapseSectionsByDefault.Value;
        _ingredientsExpanded = expanded;
        _valueExpanded = expanded;
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
            "CRAFTING INTELLIGENCE",
            "Filter and sort recipe availability, station locks, ingredient readiness, live acquisition plans, and profit estimates.",
            _status,
            _loading,
            () => _ = RefreshAsync(true));
        DrawFiltersToolbar();
        GUILayout.Space(HermesUi.StandardSpace);

        if (_response is null)
        {
            HermesUi.DrawEmptyState(
                _loading ? "Indexing current hideout recipes and profile readiness..." : "No crafting data loaded.",
                "Refresh after the hideout profile has loaded.");
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
        _focusedCraftKeys.Clear();
        _focusVersion++;
        _filter = ParseFilter(Plugin.Settings.DefaultCraftFilter.Value);
        _sort = ParseSort(Plugin.Settings.DefaultCraftSorting.Value);
        _loadRequested = false;
        _refreshVersion++;
        _detailVersion++;
        _loading = false;
        _detailLoading = false;
        _status = "Loading hideout recipes...";
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

    internal void OpenForTemplate(string templateId, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        _filter = CraftFilter.All;
        _listScroll = Vector2.zero;
        _focusedCraftKeys.Clear();
        _status = $"Finding recipes related to the selected {sourceLabel} item...";
        _loadRequested = true;
        _ = FocusTemplateAsync(templateId, ++_focusVersion);
    }

    private async Task FocusTemplateAsync(string templateId, int focusVersion)
    {
        try
        {
            var selection = await HermesApiClient.GetPreviewItemSelectionAsync(templateId);
            if (focusVersion != _focusVersion || !selection.Found || selection.Item is null)
            {
                return;
            }

            var usage = await HermesApiClient.GetItemHideoutUsageAsync(selection.Item.ItemKey);
            if (focusVersion != _focusVersion)
            {
                return;
            }

            _search = selection.Item.Name;
            _focusedCraftKeys.Clear();
            foreach (var key in usage.ProducedBy.Select(craft => craft.CraftKey)
                         .Concat(usage.UsedBy.Select(craft => craft.CraftKey)))
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _focusedCraftKeys.Add(key);
                }
            }

            HermesNativeCraftFocus.Set(selection.Item.Name, _focusedCraftKeys);

            if (_response is null)
            {
                await RefreshAsync(false);
            }

            if (focusVersion != _focusVersion || _response is null)
            {
                return;
            }

            var firstMatch = _response.Crafts.FirstOrDefault(craft =>
                _focusedCraftKeys.Contains(craft.CraftKey)
                || string.Equals(craft.OutputTemplateId, templateId, StringComparison.OrdinalIgnoreCase));
            if (firstMatch is not null)
            {
                _status = $"Showing recipes that produce or consume {selection.Item.Name}.";
                await SelectCraftAsync(firstMatch);
            }
            else
            {
                _status = $"No installed-station recipe currently produces or consumes {selection.Item.Name}.";
            }

            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
        catch (Exception ex)
        {
            if (focusVersion == _focusVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Hideout craft lookup");
                HermesNativeWorkspaceRuntime.RequestClientRefresh();
            }

            Plugin.Log.LogError(ex);
        }
    }

    private void DrawFiltersToolbar()
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
            _focusedCraftKeys.Clear();
            _focusVersion++;
            _listScroll = Vector2.zero;
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("SHOW", GUILayout.Width(48f));
        DrawFilterButton(CraftFilter.All, "All", 62f);
        DrawFilterButton(CraftFilter.Available, "Available Crafts", 132f);
        DrawFilterButton(CraftFilter.Ready, "Ready to Collect", 128f);
        DrawFilterButton(CraftFilter.Profitable, "Profitable", 92f);
        DrawFilterButton(CraftFilter.Overnight, "Overnight", 92f);
        DrawFilterButton(CraftFilter.Active, "Active", 76f);
        GUILayout.FlexibleSpace();
        var completedKeys = _response?.Crafts
            .Where(craft => craft.IsComplete && !string.IsNullOrWhiteSpace(craft.ProductionKey))
            .Select(craft => craft.ProductionKey)
            .ToArray() ?? [];
        GUI.enabled = Plugin.Settings.EnableConfirmedActions.Value
                      && Plugin.Settings.AllowCraftActions.Value
                      && completedKeys.Length > 0;
        if (GUILayout.Button("Collect All Complete", GUILayout.Width(158f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _ = Plugin.Instance?.Window.ProposeCraftCollectActionAsync(completedKeys, collectAllCompleted: true);
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("SORT", GUILayout.Width(48f));
        if (GUILayout.Button(GetSortLabel(_sort), GUILayout.Width(190f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _sort = NextSort(_sort);
            _listScroll = Vector2.zero;
        }
        GUILayout.Label("Click to cycle sorting. Profitable uses the configured minimum profit and return percentage.");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawFilterButton(CraftFilter filter, string label, float width)
    {
        if (GUILayout.Button(
                (_filter == filter ? "● " : string.Empty) + label,
                GUILayout.Width(width),
                GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _filter = filter;
            if (filter == CraftFilter.Profitable)
            {
                _sort = CraftSort.Profit;
            }
            _listScroll = Vector2.zero;
        }
    }

    private void DrawCraftList(HermesCraftsResponse response)
    {
        var visible = ApplyFiltersAndSort(response.Crafts).ToList();
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(430f), GUILayout.ExpandHeight(true));
        GUILayout.Label($"RECIPES — {visible.Count:N0} of {response.TotalCrafts:N0} • {GetSortLabel(_sort)}");
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));

        if (visible.Count == 0)
        {
            HermesUi.DrawEmptyState("No recipes match the current search, filters, and F12 settings.");
        }
        else
        {
            var visibleRows = HermesUi.LimitRows(visible, out var hiddenRows);
            foreach (var craft in visibleRows) DrawCraftButton(craft);
            HermesUi.DrawHiddenRowsNotice(hiddenRows);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawCraftButton(HermesCraftSummary craft)
    {
        var selected = _selectedCraft?.CraftKey == craft.CraftKey;
        var cashProfit = craft.EstimatedCashProfit >= 0 ? $"+₽{craft.EstimatedCashProfit:N0}" : $"-₽{Math.Abs(craft.EstimatedCashProfit):N0}";
        var economicProfit = craft.EstimatedEconomicProfit >= 0 ? $"+₽{craft.EstimatedEconomicProfit:N0}" : $"-₽{Math.Abs(craft.EstimatedEconomicProfit):N0}";
        var percent = GetProfitPercent(craft);
        var badge = craft.IsComplete ? "READY" : craft.IsActive ? "ACTIVE" : craft.CanStartNow ? "STARTABLE" : craft.IsAvailable ? "AVAILABLE" : craft.Status.ToUpperInvariant();
        var label = $"{(selected ? "▶ " : string.Empty)}{craft.OutputQuantity:N0} × {craft.OutputName}\n" +
                    $"{craft.StationName} • your L{craft.CurrentStationLevel} / required L{craft.RequiredStationLevel} • {FormatDuration(craft.DurationSeconds)} • [{badge}]\n" +
                    $"Cash {cashProfit} • Economic {economicProfit} ({percent:N1}%) • ₽{craft.EstimatedEconomicProfitPerHour:N0}/h";

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(label, GUILayout.MinHeight(76f), GUILayout.ExpandWidth(true)))
        {
            _ = SelectCraftAsync(craft);
        }
        if (craft.IsComplete && !string.IsNullOrWhiteSpace(craft.ProductionKey))
        {
            GUI.enabled = Plugin.Settings.EnableConfirmedActions.Value && Plugin.Settings.AllowCraftActions.Value;
            if (GUILayout.Button("Collect", GUILayout.Width(92f), GUILayout.MinHeight(76f)))
            {
                _ = Plugin.Instance?.Window.ProposeCraftCollectActionAsync([craft.ProductionKey]);
            }
            GUI.enabled = true;
        }
        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId)
            && GUILayout.Button("Ask HERMES", GUILayout.Width(104f), GUILayout.MinHeight(76f)))
        {
            Plugin.Instance?.OpenForPreviewItem(craft.OutputTemplateId, "craft output");
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(HermesUi.SmallSpace);
    }

    private void DrawCraftDetails()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("RECIPE DETAILS");
        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

        if (_selectedCraft is null)
        {
            HermesUi.DrawEmptyState("Select a recipe to inspect ingredient sources, readiness, cash profit, and economic profit.");
        }
        else if (_detailLoading)
        {
            HermesUi.DrawStatusLine($"Analyzing {_selectedCraft.OutputName}...", true);
        }
        else if (_detail is null || !_detail.Found || _detail.Craft is null)
        {
            HermesUi.DrawError(_detail?.Message ?? "Recipe details are unavailable.");
        }
        else
        {
            DrawDetail(_detail);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawDetail(HermesCraftDetailResponse detail)
    {
        var craft = detail.Craft!;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{craft.OutputQuantity:N0} × {craft.OutputName}");
        GUILayout.FlexibleSpace();
        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId)
            && GUILayout.Button("Ask HERMES", GUILayout.Width(104f)))
        {
            Plugin.Instance?.OpenForPreviewItem(craft.OutputTemplateId, "craft output");
        }
        GUILayout.Label(craft.IsComplete ? "READY TO COLLECT" : craft.CanStartNow ? "STARTABLE" : craft.IsAvailable ? "AVAILABLE" : craft.Status.ToUpperInvariant());
        GUILayout.EndHorizontal();
        GUILayout.Label($"Station: {craft.StationName} • your level {craft.CurrentStationLevel} / required {craft.RequiredStationLevel} • Base duration: {FormatDuration(craft.DurationSeconds)}");
        GUILayout.Label($"Status: {craft.Status} • Can start now: {(craft.CanStartNow ? "Yes" : "No")}");
        if (craft.IsActive || craft.IsComplete)
        {
            GUILayout.Label(craft.IsComplete ? "Production complete — ready to collect" : "Production currently active");
        }
        GUILayout.EndVertical();

        if (!string.IsNullOrWhiteSpace(detail.RequiredQuestName))
        {
            GUILayout.Space(HermesUi.StandardSpace);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("QUEST REQUIREMENT");
            GUILayout.Label($"{(detail.RequiredQuestComplete ? "✓" : "✗")} {detail.RequiredQuestName}");
            GUILayout.EndVertical();
        }

        GUILayout.Space(HermesUi.StandardSpace);
        var missingCount = detail.Ingredients.Count(ingredient => !ingredient.IsMet);
        if (HermesUi.DrawSectionButton("INGREDIENTS", _ingredientsExpanded, $"{missingCount:N0} missing"))
        {
            _ingredientsExpanded = !_ingredientsExpanded;
        }
        if (_ingredientsExpanded)
        {
            DrawIngredients(detail.Ingredients);
        }

        GUILayout.Space(HermesUi.StandardSpace);
        if (HermesUi.DrawSectionButton("VALUE ANALYSIS", _valueExpanded, craft.EstimatedEconomicProfit >= 0 ? $"+₽{craft.EstimatedEconomicProfit:N0}" : $"-₽{Math.Abs(craft.EstimatedEconomicProfit):N0}"))
        {
            _valueExpanded = !_valueExpanded;
        }
        if (_valueExpanded)
        {
            DrawValue(detail);
        }
    }

    private static void DrawIngredients(IReadOnlyList<HermesCraftIngredient> ingredients)
    {
        if (ingredients.Count == 0)
        {
            HermesUi.DrawEmptyState("No player-facing item ingredients were returned.");
            return;
        }

        var ordered = ingredients
            .OrderBy(ingredient => ingredient.IsMet)
            .ThenByDescending(ingredient => ingredient.Missing)
            .ThenBy(ingredient => ingredient.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visible = HermesUi.LimitRows(ordered, out var hidden);
        foreach (var ingredient in visible)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{(ingredient.IsMet ? "✓" : "✗")} {ingredient.Name}");
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrWhiteSpace(ingredient.TemplateId)
                && GUILayout.Button("Ask HERMES", GUILayout.Width(104f)))
            {
                Plugin.Instance?.OpenForPreviewItem(ingredient.TemplateId, "craft ingredient");
            }
            GUILayout.Label(ingredient.IsReusableTool ? "REUSABLE TOOL" : ingredient.RequirementType.ToUpperInvariant());
            GUILayout.EndHorizontal();
            GUILayout.Label($"Owned: {FormatCount(ingredient.Owned)} • Used: {FormatCount(ingredient.OwnedUsed)} / {FormatCount(ingredient.Required)}" +
                            (ingredient.Missing > 0d ? $" • Missing: {FormatCount(ingredient.Missing)}" : string.Empty));
            if (ingredient.FoundInRaidRequired) GUILayout.Label("FIR REQUIRED");
            if (ingredient.EstimatedOwnedEconomicValue > 0) GUILayout.Label($"Owned opportunity value: ₽{ingredient.EstimatedOwnedEconomicValue:N0}");

            if (!ingredient.IsMet)
            {
                GUILayout.Label($"Additional cash estimate: ₽{ingredient.EstimatedPurchaseCost:N0}");
                if (ingredient.UnavailableQuantity > 0d) GUILayout.Label($"No current purchase source: {FormatCount(ingredient.UnavailableQuantity)}");
                if (Plugin.Settings.ShowDetailedCraftAcquisitionPlan.Value)
                {
                    if (ingredient.AcquisitionPlan.Count == 0)
                    {
                        GUILayout.Label("Acquisition plan: unavailable");
                    }
                    else
                    {
                        GUILayout.Label("ACQUISITION PLAN");
                        foreach (var line in ingredient.AcquisitionPlan)
                        {
                            GUILayout.Label($"• {FormatCount(line.Quantity)} × {line.Source}{(line.IsFallback ? " (estimate only)" : string.Empty)} — ₽{line.UnitPrice:N0} each — ₽{line.TotalCost:N0}");
                        }
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(ingredient.CostNote)) GUILayout.Label(ingredient.CostNote);
            GUILayout.EndVertical();
            GUILayout.Space(HermesUi.SmallSpace);
        }
        HermesUi.DrawHiddenRowsNotice(hidden);
    }

    private static void DrawValue(HermesCraftDetailResponse detail)
    {
        var craft = detail.Craft!;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"Additional cash required: ₽{craft.EstimatedAdditionalCashCost:N0}");
        GUILayout.Label($"Owned ingredient opportunity value: ₽{craft.EstimatedOwnedIngredientValue:N0}");
        GUILayout.Label($"Total economic input value: ₽{craft.EstimatedEconomicInputValue:N0}");
        GUILayout.Label($"Estimated output value: ₽{craft.EstimatedOutputValue:N0}");
        GUILayout.Space(HermesUi.SmallSpace);
        GUILayout.Label(craft.EstimatedCashProfit >= 0 ? $"Cash profit: ₽{craft.EstimatedCashProfit:N0}" : $"Cash loss: ₽{Math.Abs(craft.EstimatedCashProfit):N0}");
        GUILayout.Label(craft.EstimatedEconomicProfit >= 0 ? $"Economic profit: ₽{craft.EstimatedEconomicProfit:N0}" : $"Economic loss: ₽{Math.Abs(craft.EstimatedEconomicProfit):N0}");
        GUILayout.Label($"Economic return: {GetProfitPercent(craft):N1}% • {craft.EstimatedEconomicProfitPerHour:N0} roubles/hour");
        if (!craft.AcquisitionPlanComplete) GUILayout.Label("WARNING: the acquisition plan is incomplete because one or more missing ingredients have no current purchase source.");
        GUILayout.Label(detail.ValuationBasis);
        GUILayout.EndVertical();
    }

    private IEnumerable<HermesCraftSummary> ApplyFiltersAndSort(IEnumerable<HermesCraftSummary> source)
    {
        var query = _search.Trim();
        var minProfit = Plugin.Settings.GetMinimumCraftProfit();
        var minPercent = Plugin.Settings.GetMinimumCraftProfitPercent();
        var overnightMin = Plugin.Settings.GetOvernightMinimumHours() * 3600L;
        var overnightMax = Plugin.Settings.GetOvernightMaximumHours() * 3600L;

        var filtered = source.Where(craft =>
        {
            if (_focusedCraftKeys.Count > 0 && !_focusedCraftKeys.Contains(craft.CraftKey))
            {
                return false;
            }

            if (_focusedCraftKeys.Count == 0
                && query.Length > 0
                && !craft.OutputName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !craft.StationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !craft.Status.Contains(query, StringComparison.OrdinalIgnoreCase)) return false;

            if (Plugin.Settings.HideCraftsWithMissingIngredients.Value && HasMissingIngredients(craft)) return false;
            if (Plugin.Settings.HideUnavailableCrafts.Value && IsProgressionUnavailable(craft)) return false;

            return _filter switch
            {
                CraftFilter.Available => craft.StationLevelMet,
                CraftFilter.Ready => craft.IsComplete,
                CraftFilter.Profitable => craft.EstimatedBestSaleProfit >= minProfit && GetProfitPercent(craft) >= minPercent,
                CraftFilter.Overnight => craft.DurationSeconds >= overnightMin && craft.DurationSeconds <= overnightMax,
                CraftFilter.Active => craft.IsActive || craft.IsComplete,
                _ => true
            };
        });

        // PROFITABLE always ranks the best actual sale path from most to least profit.
        // This prevents a saved Name/Station sort from making the profitability view look random.
        if (_filter == CraftFilter.Profitable)
        {
            return filtered
                .OrderByDescending(craft => craft.EstimatedBestSaleProfit)
                .ThenByDescending(craft => craft.EstimatedBestSaleProfitPerHour)
                .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
        }

        return _sort switch
        {
            CraftSort.Station => filtered.OrderBy(craft => craft.StationName, StringComparer.OrdinalIgnoreCase).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            CraftSort.Duration => filtered.OrderBy(craft => craft.DurationSeconds).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            CraftSort.Profit => filtered.OrderByDescending(craft => craft.EstimatedBestSaleProfit).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            CraftSort.ProfitPercent => filtered.OrderByDescending(GetProfitPercent).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            CraftSort.ProfitPerHour => filtered.OrderByDescending(craft => craft.EstimatedBestSaleProfitPerHour).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            CraftSort.MissingIngredients => filtered.OrderBy(craft => HasMissingIngredients(craft)).ThenBy(craft => craft.Status, StringComparer.OrdinalIgnoreCase).ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool HasMissingIngredients(HermesCraftSummary craft) =>
        !craft.IsAvailable && craft.Status.Contains("missing", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgressionUnavailable(HermesCraftSummary craft) =>
        !craft.IsAvailable && !HasMissingIngredients(craft) && !craft.IsActive && !craft.IsComplete;

    private static double GetProfitPercent(HermesCraftSummary craft)
    {
        if (craft.EstimatedEconomicInputValue <= 0L)
        {
            return craft.EstimatedBestSaleProfit > 0L ? 100d : 0d;
        }
        return craft.EstimatedBestSaleProfit * 100d / craft.EstimatedEconomicInputValue;
    }

    private async Task RefreshAsync(bool invalidateMarketCache)
    {
        if (_loading) return;
        var requestVersion = ++_refreshVersion;
        _detailVersion++;
        _loading = true;
        _detailLoading = false;
        _status = invalidateMarketCache ? "Refreshing recipes and current market data..." : "Loading recipe summaries and checking the active PMC inventory...";
        HermesCraftSummary? nextCraft = null;

        try
        {
            if (invalidateMarketCache)
            {
                await HermesApiClient.ClearCachesAsync();
                if (requestVersion != _refreshVersion) return;
            }
            var response = await HermesApiClient.GetCraftsAsync();
            if (requestVersion != _refreshVersion) return;
            _response = response;
            _status = response.Found
                ? $"Loaded {response.TotalCrafts:N0} recipe(s). List values are quick estimates; select a recipe for live sourcing."
                : response.Message ?? "Crafting information is unavailable.";

            if (response.Found && response.Crafts.Count > 0)
            {
                var oldKey = _selectedCraft?.CraftKey;
                nextCraft = string.IsNullOrWhiteSpace(oldKey) ? null : response.Crafts.FirstOrDefault(craft => craft.CraftKey == oldKey);
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
            if (requestVersion != _refreshVersion) return;
            _response = null;
            _selectedCraft = null;
            _detail = null;
            _status = HermesApiClient.DescribeFailure(ex, "Craft list refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _refreshVersion) _loading = false;
        }

        if (nextCraft is not null && requestVersion == _refreshVersion) _ = SelectCraftAsync(nextCraft);
    }

    private async Task SelectCraftAsync(HermesCraftSummary craft)
    {
        if (_detailLoading && _selectedCraft?.CraftKey == craft.CraftKey) return;
        var requestVersion = ++_detailVersion;
        _selectedCraft = craft;
        _detail = null;
        _detailLoading = true;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetCraftDetailAsync(craft.CraftKey);
            if (requestVersion == _detailVersion && _selectedCraft?.CraftKey == craft.CraftKey) _detail = response;
        }
        catch (Exception ex)
        {
            if (requestVersion == _detailVersion && _selectedCraft?.CraftKey == craft.CraftKey)
            {
                _detail = new HermesCraftDetailResponse { Found = false, Message = HermesApiClient.DescribeFailure(ex, "Craft analysis") };
            }
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _detailVersion && _selectedCraft?.CraftKey == craft.CraftKey) _detailLoading = false;
        }
    }

    private static CraftFilter ParseFilter(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "available" or "available crafts" => CraftFilter.Available,
        "ready" or "ready now" => CraftFilter.Ready,
        "profitable" or "profit" => CraftFilter.Profitable,
        "overnight" => CraftFilter.Overnight,
        "active" => CraftFilter.Active,
        _ => CraftFilter.All
    };

    private static CraftSort ParseSort(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "station" => CraftSort.Station,
        "duration" => CraftSort.Duration,
        "profit" => CraftSort.Profit,
        "profit percentage" or "profit percent" => CraftSort.ProfitPercent,
        "profit per hour" => CraftSort.ProfitPerHour,
        "missing ingredients" => CraftSort.MissingIngredients,
        _ => CraftSort.Name
    };

    private static CraftSort NextSort(CraftSort sort) => sort switch
    {
        CraftSort.Name => CraftSort.Station,
        CraftSort.Station => CraftSort.Duration,
        CraftSort.Duration => CraftSort.Profit,
        CraftSort.Profit => CraftSort.ProfitPercent,
        CraftSort.ProfitPercent => CraftSort.ProfitPerHour,
        CraftSort.ProfitPerHour => CraftSort.MissingIngredients,
        _ => CraftSort.Name
    };

    private static string GetSortLabel(CraftSort sort) => sort switch
    {
        CraftSort.Name => "Name",
        CraftSort.Station => "Station",
        CraftSort.Duration => "Duration",
        CraftSort.Profit => "Best sale profit",
        CraftSort.ProfitPercent => "Profit percentage",
        CraftSort.ProfitPerHour => "Profit per hour",
        CraftSort.MissingIngredients => "Missing ingredients",
        _ => "Name"
    };

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
