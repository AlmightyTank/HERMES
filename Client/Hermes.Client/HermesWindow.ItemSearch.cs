using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Item Search

    private void DrawItemSearchTab()
    {
        CheckDeferredSearch();
        HermesUi.DrawPanelTitle(
            "ITEM SEARCH & MARKET INTELLIGENCE",
            "Search player-facing item names or inspect an exact PMC item through Ask HERMES.",
            _status,
            _searching);
        DrawSearchBar();
        GUILayout.Space(HermesUi.StandardSpace);

        GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
        DrawResultPanel();
        GUILayout.Space(8f);
        DrawDetailPanel();
        GUILayout.EndHorizontal();
    }

    private void DrawSearchBar()
    {
        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("HermesSearchField");
        var previousQuery = _query;
        _query = GUILayout.TextField(_query, GUILayout.ExpandWidth(true), GUILayout.Height(HermesUi.ToolbarHeight));
        if (!string.Equals(previousQuery, _query, StringComparison.Ordinal))
        {
            _deferredSearchAt = Plugin.Settings.SearchWhileTyping.Value
                                && _query.Trim().Length >= Plugin.Settings.GetMinimumSearchCharacters()
                ? Time.realtimeSinceStartup + 0.35f
                : -1f;
        }

        var trimmedLength = _query.Trim().Length;
        var canSearch = !_searching && trimmedLength >= Plugin.Settings.GetMinimumSearchCharacters();
        GUI.enabled = canSearch;
        if (GUILayout.Button(_searching ? "Searching..." : "Search", GUILayout.Width(110f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            _deferredSearchAt = -1f;
            _ = RunSearchAsync();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Clear", GUILayout.Width(70f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            Clear();
            _query = string.Empty;
            _lastSubmittedQuery = string.Empty;
            _deferredSearchAt = -1f;
        }
        GUILayout.EndHorizontal();

        if (Plugin.Settings.ShowSectionDescriptions.Value)
        {
            var mode = Plugin.Settings.SearchWhileTyping.Value ? "Automatic search enabled" : "Press Search or Enter";
            GUILayout.Label($"{mode} • minimum {Plugin.Settings.GetMinimumSearchCharacters()} character(s) • up to {Plugin.Settings.GetMaximumSearchResults()} result(s).");
        }

        if (Event.current.type == EventType.KeyDown
            && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
            && canSearch)
        {
            Event.current.Use();
            _deferredSearchAt = -1f;
            _ = RunSearchAsync();
        }
    }

    private void CheckDeferredSearch()
    {
        if (_deferredSearchAt < 0f
            || _searching
            || Time.realtimeSinceStartup < _deferredSearchAt)
        {
            return;
        }

        _deferredSearchAt = -1f;
        var query = _query.Trim();
        if (query.Length >= Plugin.Settings.GetMinimumSearchCharacters()
            && !query.Equals(_lastSubmittedQuery, StringComparison.OrdinalIgnoreCase))
        {
            _ = RunSearchAsync();
        }
    }

    private void DrawResultPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(330f), GUILayout.ExpandHeight(true));
        GUILayout.Label($"SEARCH RESULTS — {_results.Count:N0}");
        GUILayout.Space(4f);

        _resultScroll = GUILayout.BeginScrollView(_resultScroll, GUILayout.ExpandHeight(true));

        if (_results.Count == 0)
        {
            HermesUi.DrawEmptyState(
                string.IsNullOrWhiteSpace(_lastSubmittedQuery) ? "No results loaded." : $"No items matched \"{_lastSubmittedQuery}\".",
                "Quest-only items and items without a positive handbook value are excluded.");
        }
        else
        {
            var visibleResults = HermesUi.LimitRows(_results, out var hiddenResults);
            foreach (var item in visibleResults)
            {
                DrawResultButton(item);
            }
            HermesUi.DrawHiddenRowsNotice(hiddenResults);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawResultButton(HermesItemSummary item)
    {
        var selected = _selectedItem?.ItemKey == item.ItemKey;
        var selectedInstance = selected ? GetSelectedStashInstance() : null;
        var displayedValue = selectedInstance is { ConditionAdjustedReferenceValue: > 0 }
            ? selectedInstance.ConditionAdjustedReferenceValue
            : item.ReferencePrice;
        var displayedLabel = selectedInstance switch
        {
            { ChildItemCount: > 0 } => "Assembled",
            not null => "Instance",
            _ => "Handbook"
        };
        var lines = new List<string> { (selected ? "▶ " : string.Empty) + item.Name };
        if (Plugin.Settings.ShowItemShortNames.Value
            && !string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(item.ShortName);
        }
        if (Plugin.Settings.ShowItemReferencePrices.Value && displayedValue.HasValue)
        {
            lines.Add($"{displayedLabel} ₽{displayedValue.Value:N0}");
        }

        if (GUILayout.Button(string.Join("\n", lines), GUILayout.MinHeight(lines.Count >= 3 ? 62f : 48f), GUILayout.ExpandWidth(true)))
        {
            _ = SelectItemAsync(item);
        }
        GUILayout.Space(HermesUi.SmallSpace);
    }

    private void DrawDetailPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("MARKET INTELLIGENCE");
        GUILayout.Space(4f);
        GUILayout.Label(_detailStatus);
        GUILayout.Space(4f);

        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

        if (_selectedItem is null)
        {
            HermesUi.DrawEmptyState("Select a search result to compare vanilla traders and the local SPT flea market.", "Search by item name or use Ask HERMES from an inventory, trader, or flea context menu.");
        }
        else
        {
            DrawSelectedItemOverview(_selectedItem);
            DrawStashInstanceSection();

            if (_loadingDetails)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Loading current trader assortments, player access, and local flea offers...");
            }
            else
            {
                if (_traderSummary is not null)
                {
                    DrawTraderSaleSection(_traderSummary);
                }

                if (_marketSummary is not null)
                {
                    DrawMarketSection(_marketSummary);
                }


                if (_hideoutUsage is not null)
                {
                    DrawHideoutUsageSection(_hideoutUsage);
                }
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawSelectedItemOverview(HermesItemSummary item)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(item.Name);

        if (Plugin.Settings.ShowItemShortNames.Value
            && !string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.Label($"Short name: {item.ShortName}");
        }

        if (Plugin.Settings.ShowItemReferencePrices.Value)
        {
            var selectedInstance = GetSelectedStashInstance();
            if (selectedInstance is { ChildItemCount: > 0 })
            {
                GUILayout.Label($"Assembled reference value: ₽{selectedInstance.ConditionAdjustedReferenceValue:N0}");
                GUILayout.Label($"Root ₽{selectedInstance.RootConditionAdjustedReferenceValue:N0} + child items ₽{selectedInstance.InstalledComponentReferenceValue:N0} ({selectedInstance.ChildItemCount:N0} child item(s)).");
            }
            else if (selectedInstance is not null)
            {
                GUILayout.Label($"Selected-instance reference value: ₽{selectedInstance.ConditionAdjustedReferenceValue:N0}");
            }
            else
            {
                GUILayout.Label(item.ReferencePrice.HasValue
                    ? $"Handbook reference: ₽{item.ReferencePrice.Value:N0}"
                    : "Handbook reference: unavailable");
            }
        }
        GUILayout.Label("Current quest, hideout, and crafting progress is shown below from the active PMC profile.");
        GUILayout.EndVertical();
    }

    private HermesStashInstanceSummary? GetSelectedStashInstance()
    {
        if (string.IsNullOrWhiteSpace(_selectedStashInstanceKey))
        {
            return null;
        }

        return _stashInstances.FirstOrDefault(instance =>
            string.Equals(instance.InstanceKey, _selectedStashInstanceKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshItemSearchDataAsync()
    {
        if (_activeTab != HermesTab.ItemSearch
            || _searching
            || _loadingDetails
            || _loadingInstancePrice)
        {
            return;
        }

        try
        {
            _refreshStatus = "Reloading Items & Market...";
            if (_selectedItem is not null)
            {
                _detailRequestVersion++;
                _instanceRequestVersion++;
                _loadingDetails = false;
                _loadingInstancePrice = false;

                var selectedInstance = _selectedStashInstanceKey is null
                    ? null
                    : _stashInstances.FirstOrDefault(instance =>
                        instance.InstanceKey == _selectedStashInstanceKey);
                await SelectItemAsync(
                    _selectedItem,
                    selectedInstance,
                    _selectedStashInstanceKey is not null);
                _refreshStatus = "Items & Market refreshed.";
            }
            else if (_query.Trim().Length >= Plugin.Settings.GetMinimumSearchCharacters())
            {
                await RunSearchAsync();
                _refreshStatus = "Items & Market search refreshed.";
            }
            else
            {
                _refreshStatus = "Items & Market ready.";
            }
        }
        catch (Exception ex)
        {
            _refreshStatus = HermesApiClient.DescribeFailure(ex, "Items & Market refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    private async Task RunSearchAsync()
    {
        var query = _query.Trim();
        if (query.Length < Plugin.Settings.GetMinimumSearchCharacters() || _searching)
        {
            _status = $"Enter at least {Plugin.Settings.GetMinimumSearchCharacters()} character(s) to search.";
            return;
        }

        var requestVersion = ++_searchRequestVersion;
        _openRequestVersion++;
        _detailRequestVersion++;
        _instanceRequestVersion++;
        _searching = true;
        _status = $"Searching for \"{query}\"...";
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _hideoutUsage = null;
        _stashInstances = [];
        _selectedStashInstanceKey = null;
        _selectedTagActionInstanceKeys.Clear();
        _loadingInstancePrice = false;
        _saleComparisonExpanded = Plugin.Settings.ExpandTraderComparisonByDefault.Value;
        _marketExpanded = Plugin.Settings.ExpandMarketByDefault.Value;
        _questRequirementsExpanded = false;
        _questKeysExpanded = false;
        _hideoutCraftUsesExpanded = false;
        _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";

        try
        {
            var response = await HermesApiClient.SearchAsync(query, Plugin.Settings.GetMaximumSearchResults());
            if (requestVersion != _searchRequestVersion)
            {
                return;
            }

            _results = response.Results ?? [];
            _resultScroll = Vector2.zero;
            _detailScroll = Vector2.zero;
            _status = response.TotalMatches == 0
                ? $"No items matched \"{query}\"."
                : $"Showing {_results.Count} of {response.TotalMatches} match(es).";

            if (_results.Count > 0)
            {
                await SelectItemAsync(_results[0]);
            }
        }
        catch (Exception ex)
        {
            if (requestVersion != _searchRequestVersion)
            {
                return;
            }

            _results = [];
            _selectedItem = null;
            _traderSummary = null;
            _marketSummary = null;
            _hideoutUsage = null;
            _stashInstances = [];
            _selectedStashInstanceKey = null;
            _status = HermesApiClient.DescribeFailure(ex, "Item search");
            _detailStatus = "Market information unavailable. Retry or use Refresh current data.";
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _searchRequestVersion)
            {
                _searching = false;
            }
        }
    }

    private async Task SelectItemAsync(
        HermesItemSummary item,
        HermesStashInstanceSummary? preferredInstance = null,
        bool selectFirstMatchingStashInstance = true)
    {
        if (_loadingDetails && _selectedItem?.ItemKey == item.ItemKey)
        {
            return;
        }

        var requestVersion = ++_detailRequestVersion;
        _instanceRequestVersion++;
        _selectedItem = item;
        _traderSummary = null;
        _marketSummary = null;
        _hideoutUsage = null;
        _stashInstances = [];
        _selectedStashInstanceKey = null;
        _selectedTagActionInstanceKeys.Clear();
        _loadingInstancePrice = false;
        ResetSectionExpansionDefaults();
        _loadingDetails = true;
        _detailScroll = Vector2.zero;
        _detailStatus = $"Analyzing traders, the local SPT flea market, hideout upgrades, and recipes for {item.Name}...";

        bool IsCurrent() => requestVersion == _detailRequestVersion
                            && _selectedItem?.ItemKey == item.ItemKey;

        try
        {
            if (preferredInstance is not null)
            {
                _stashInstances = [preferredInstance];
                _selectedStashInstanceKey = preferredInstance.InstanceKey;
            }
            else
            {
                try
                {
                    var stashResponse = await HermesApiClient.GetStashInstancesAsync(item.ItemKey);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    _stashInstances = stashResponse.Instances ?? [];
                    _selectedStashInstanceKey = selectFirstMatchingStashInstance
                        ? _stashInstances.FirstOrDefault()?.InstanceKey
                        : null;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(ex);
                    if (IsCurrent())
                    {
                        _stashInstances = [];
                        _selectedStashInstanceKey = null;
                    }
                }
            }

            if (!IsCurrent())
            {
                return;
            }

            // Start independent detail requests together. They still have their own
            // 12-second timeout and are applied only if this selection remains current.
            var traderTask = HermesApiClient.GetTraderSummaryAsync(
                item.ItemKey,
                _selectedStashInstanceKey);
            var marketTask = HermesApiClient.GetMarketSummaryAsync(
                item.ItemKey,
                _selectedStashInstanceKey);
            var usageTask = HermesApiClient.GetItemHideoutUsageAsync(item.ItemKey);

            try
            {
                var traderResponse = await traderTask;
                if (IsCurrent())
                {
                    _traderSummary = traderResponse;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (IsCurrent())
                {
                    _traderSummary = new HermesTraderSummaryResponse
                    {
                        Found = false,
                        Message = HermesApiClient.DescribeFailure(ex, "Trader analysis")
                    };
                }
            }

            try
            {
                var marketResponse = await marketTask;
                if (IsCurrent())
                {
                    _marketSummary = marketResponse;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (IsCurrent())
                {
                    _marketSummary = new HermesMarketSummaryResponse
                    {
                        Found = false,
                        Message = HermesApiClient.DescribeFailure(ex, "Local flea analysis")
                    };
                }
            }

            try
            {
                var usageResponse = await usageTask;
                if (IsCurrent())
                {
                    _hideoutUsage = usageResponse;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (IsCurrent())
                {
                    _hideoutUsage = new HermesItemHideoutUsageResponse
                    {
                        Found = false,
                        Message = HermesApiClient.DescribeFailure(ex, "Quest, hideout, and crafting usage analysis")
                    };
                }
            }

            if (IsCurrent())
            {
                ApplySmartItemSectionCollapse();
                _detailStatus = !selectFirstMatchingStashInstance
                    ? _stashInstances.Count > 0
                        ? "Preview analysis uses the full-condition base item. Matching owned copies are available in the selector below."
                        : "Preview analysis uses the full-condition base item. No matching owned copy is currently in the active PMC inventory."
                    : _stashInstances.Count > 0
                        ? "Current profile loaded. Trader sale prices use the selected owned copy; flea data remains a local market comparison."
                        : "Current profile loaded. No matching owned copy was found, so trader sale prices use the base-item estimate.";
            }
        }
        finally
        {
            if (IsCurrent())
            {
                _loadingDetails = false;
            }
        }
    }

    private async Task SelectStashInstanceAsync(string? instanceKey)
    {
        if (_selectedItem is null
            || _loadingInstancePrice
            || string.Equals(_selectedStashInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requestVersion = ++_instanceRequestVersion;
        var item = _selectedItem;
        _selectedStashInstanceKey = instanceKey;
        _loadingInstancePrice = true;
        _detailStatus = instanceKey is null
            ? "Restoring the full-condition base-item trader estimate..."
            : "Calculating trader sale prices for the selected owned copy...";

        try
        {
            var traderTask = HermesApiClient.GetTraderSummaryAsync(item.ItemKey, instanceKey);
            var marketTask = HermesApiClient.GetMarketSummaryAsync(item.ItemKey, instanceKey);

            var traderResponse = await traderTask;
            if (requestVersion != _instanceRequestVersion
                || _selectedItem?.ItemKey != item.ItemKey
                || !string.Equals(_selectedStashInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _traderSummary = traderResponse;

            try
            {
                var marketResponse = await marketTask;
                if (requestVersion == _instanceRequestVersion
                    && _selectedItem?.ItemKey == item.ItemKey
                    && string.Equals(_selectedStashInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase))
                {
                    _marketSummary = marketResponse;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (requestVersion == _instanceRequestVersion
                    && _selectedItem?.ItemKey == item.ItemKey)
                {
                    _marketSummary = new HermesMarketSummaryResponse
                    {
                        Found = false,
                        Message = HermesApiClient.DescribeFailure(ex, "Selected owned-copy flea pricing")
                    };
                }
            }

            _detailStatus = traderResponse.UsesSelectedStashInstance
                ? "Trader sale prices now use the selected owned copy."
                : "Trader sale prices now use the full-condition base-item estimate.";
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
            if (requestVersion == _instanceRequestVersion
                && _selectedItem?.ItemKey == item.ItemKey)
            {
                _detailStatus = HermesApiClient.DescribeFailure(ex, "Selected owned-copy pricing");
            }
        }
        finally
        {
            if (requestVersion == _instanceRequestVersion)
            {
                _loadingInstancePrice = false;
            }
        }
    }

    #endregion
}
