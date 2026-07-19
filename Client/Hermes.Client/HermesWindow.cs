using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesWindow
{
    private enum HermesTab
    {
        Assistant,
        ItemSearch,
        Hideout,
        Crafts,
        Stash,
        Loadout,
        RaidPlanner
    }

    private const float EmbeddedOuterPadding = 6f;
    private const float EmbeddedHeaderHeight = 48f;
    private const float EmbeddedNavigationWidth = 188f;
    private const float EmbeddedCompactNavigationHeight = 70f;
    private const float EmbeddedGap = 6f;
    private const float EmbeddedRailBreakpoint = 1080f;

    private Vector2 _resultScroll;
    private Vector2 _detailScroll;
    private string _query = string.Empty;
    private string _lastSubmittedQuery = string.Empty;
    private float _deferredSearchAt = -1f;
    private string _status = "Search for an item or ask where it can be bought or sold.";
    private string _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";
    private bool _visible;
    private bool _nativeMode;
    private bool _searching;
    private bool _loadingDetails;
    private bool _saleComparisonExpanded;
    private bool _marketExpanded;
    private bool _questRequirementsExpanded;
    private bool _questKeysExpanded;
    private bool _hideoutCraftUsesExpanded;
    private bool _stashInstancesExpanded = true;
    private bool _loadingInstancePrice;
    private HermesTab _activeTab;
    private readonly HermesAssistantPanel _assistantPanel = new();
    private readonly HermesAssistantNoticeService _noticeService = new();
    private readonly HermesHideoutPanel _hideoutPanel = new();
    private readonly HermesCraftPanel _craftPanel = new();
    private readonly HermesStashPanel _stashPanel = new();
    private readonly HermesLoadoutPanel _loadoutPanel = new();
    private IReadOnlyList<HermesItemSummary> _results = [];
    private HermesItemSummary? _selectedItem;
    private HermesTraderSummaryResponse? _traderSummary;
    private HermesMarketSummaryResponse? _marketSummary;
    private HermesItemHideoutUsageResponse? _hideoutUsage;
    private IReadOnlyList<HermesStashInstanceSummary> _stashInstances = [];
    private string? _selectedStashInstanceKey;
    private int _searchRequestVersion;
    private int _openRequestVersion;
    private int _detailRequestVersion;
    private int _instanceRequestVersion;
    private bool _refreshingCurrent;
    private bool _cacheStatusRequested;
    private bool _cacheStatusLoading;
    private float _nextCacheStatusRefresh;
    private HermesCacheStatusResponse? _cacheStatus;
    private string? _refreshStatus;

    public HermesWindow()
    {
        _activeTab = ParseTabName(Plugin.Settings.GetOpeningTabName());
        ResetSectionExpansionDefaults();
    }

    public void Toggle()
    {
        if (HermesNativeScreenRegistry.TryToggle())
        {
            return;
        }

        // HERMES is inventory-only. When F8 is pressed outside Character/inventory,
        // use the same native taskbar navigation path as Ask HERMES and notices.
        HermesGlobalNavigation.RequestOpen();
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log?.LogDebug("HERMES F8 queued native Character-screen navigation.");
        }
    }

    internal void SetNativeVisibility(bool visible)
    {
        if (visible)
        {
            _nativeMode = true;
            _visible = true;
            OnPresentationOpened();
            return;
        }

        if (_nativeMode)
        {
            _visible = false;
            _nativeMode = false;
        }
    }

    private void EnsurePresentationVisible()
    {
        if (!HermesNativeScreenRegistry.TryShow() && Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log?.LogDebug("HERMES could not open because no Character or in-raid InventoryScreen is active.");
        }
    }

    private void OnPresentationOpened()
    {
        // Rebuild the visible native presentation immediately. Server data is refreshed only
        // through the prepared-workspace coordinator; opening HERMES must never behave like the
        // top Refresh button or clear/invalidate server caches.
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        if (Plugin.Settings.AutomaticallyRefreshWhenOpened.Value)
        {
            HermesWorkspaceSnapshotCoordinator.Current?.OnPresentationOpened();
        }
    }

    internal void OpenForInventoryItem(string profileItemId)
    {
        if (string.IsNullOrWhiteSpace(profileItemId))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch);
        _ = OpenForInventoryItemAsync(profileItemId, null);
    }

    internal void OpenForLoadoutItem(string profileItemId, string itemName)
    {
        if (string.IsNullOrWhiteSpace(profileItemId) && string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch);
        if (string.IsNullOrWhiteSpace(profileItemId))
        {
            OpenForNamedItem(itemName, "loadout");
            return;
        }

        _ = OpenForInventoryItemAsync(profileItemId, itemName);
    }

    internal void OpenForNamedItem(string itemName, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch);

        // Cancel any stale exact-instance/detail operation before starting the visible search.
        Clear();
        _query = itemName.Trim();
        _status = $"Looking up the selected {sourceLabel} item: {_query}...";
        _ = RunSearchAsync();
    }

    internal void OpenForStashItem(string selectionKey, string itemName)
    {
        if (string.IsNullOrWhiteSpace(selectionKey) && string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch);
        HermesNativeWorkspaceRuntime.RequestClientRefresh();

        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            OpenForNamedItem(itemName, "stash");
            return;
        }

        _ = OpenForInventoryItemAsync(selectionKey, itemName);
    }

    internal void OpenForStashItem(string selectionKey)
    {
        OpenForStashItem(selectionKey, string.Empty);
    }

    internal void OpenForPreviewItem(string templateId, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch);
        _ = OpenForPreviewItemAsync(templateId, sourceLabel);
    }

    private async Task OpenForPreviewItemAsync(string templateId, string sourceLabel)
    {
        var requestVersion = ++_openRequestVersion;
        _detailRequestVersion++;
        _instanceRequestVersion++;
        _searching = true;
        _loadingDetails = true;
        _status = $"Resolving the selected {sourceLabel} preview...";
        _detailStatus = "HERMES is loading a base-item analysis for the previewed offer.";
        _results = [];
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _hideoutUsage = null;
        _stashInstances = [];
        _selectedStashInstanceKey = null;
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetPreviewItemSelectionAsync(templateId);
            if (requestVersion != _openRequestVersion)
            {
                return;
            }

            if (!response.Found || response.Item is null)
            {
                _status = response.Message ?? "HERMES could not analyze the selected preview item.";
                _detailStatus = _status;
                return;
            }

            _query = response.Item.Name;
            _results = [response.Item];
            _status = $"Selected {sourceLabel} preview: {response.Item.Name}.";
            await SelectItemAsync(response.Item);
        }
        catch (Exception ex)
        {
            if (requestVersion == _openRequestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Preview item analysis");
                _detailStatus = _status;
            }

            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _openRequestVersion)
            {
                _searching = false;
                if (_selectedItem is null)
                {
                    _loadingDetails = false;
                }
            }
        }
    }

    private async Task OpenForInventoryItemAsync(string profileItemId, string? fallbackItemName)
    {
        var requestVersion = ++_openRequestVersion;
        _detailRequestVersion++;
        _instanceRequestVersion++;
        _searching = true;
        _loadingDetails = true;
        _status = "Resolving the selected PMC inventory item...";
        _detailStatus = "HERMES is locating the exact PMC inventory instance.";
        _results = [];
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _hideoutUsage = null;
        _stashInstances = [];
        _selectedStashInstanceKey = null;
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;

        try
        {
            var response = await HermesApiClient.GetInventoryInstanceSelectionAsync(profileItemId);
            if (requestVersion != _openRequestVersion)
            {
                return;
            }

            if (!response.Found || response.Item is null || response.Instance is null)
            {
                if (await TryFallbackNamedSearchAsync(
                        fallbackItemName,
                        response.Message ?? "The exact loadout instance could not be resolved."))
                {
                    return;
                }

                _status = response.Message ?? "HERMES could not analyze the selected inventory item.";
                _detailStatus = _status;
                return;
            }

            _query = response.Item.Name;
            _results = [response.Item];
            _status = $"Selected exact PMC copy: {response.Item.Name} • {response.InventoryLocation ?? "PMC inventory"}.";
            await SelectItemAsync(response.Item, response.Instance);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
            if (requestVersion == _openRequestVersion
                && await TryFallbackNamedSearchAsync(
                    fallbackItemName,
                    HermesApiClient.DescribeFailure(ex, "Exact PMC inventory-item analysis")))
            {
                return;
            }

            if (requestVersion == _openRequestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Exact PMC inventory-item analysis");
                _detailStatus = _status;
            }
        }
        finally
        {
            if (requestVersion == _openRequestVersion)
            {
                _searching = false;
                if (_selectedItem is null)
                {
                    _loadingDetails = false;
                }
            }
        }
    }

    private async Task<bool> TryFallbackNamedSearchAsync(string? fallbackItemName, string failureReason)
    {
        if (string.IsNullOrWhiteSpace(fallbackItemName))
        {
            return false;
        }

        // Exact equipment-instance resolution can fail after a profile refresh or while the
        // loadout snapshot is older than the live inventory. A name lookup still resolves the
        // correct catalog item and loads trader/flea/hideout intelligence for the clicked row.
        _searching = false;
        _loadingDetails = false;
        _query = fallbackItemName.Trim();
        _status = $"{failureReason} Looking up {_query} by item name...";
        _detailStatus = "Resolving the selected loadout item through the HERMES catalog.";
        await RunSearchAsync();
        return true;
    }

    public void Tick()
    {
        _noticeService.Tick(_visible, _activeTab == HermesTab.Assistant);
    }

    public void Draw()
    {
        // The standalone floating window is removed. HERMES renders only inside
        // the main Character screen or the in-raid InventoryScreen tab.
    }

    internal void DrawEmbedded(Rect rect)
    {
        if (!_visible || !_nativeMode || rect.width < 480f || rect.height < 320f)
        {
            return;
        }

        if (Plugin.Settings.ShowDiagnosticsFooter.Value
            && !_cacheStatusLoading
            && (!_cacheStatusRequested || Time.realtimeSinceStartup >= _nextCacheStatusRefresh))
        {
            _cacheStatusRequested = true;
            _cacheStatusLoading = true;
            _ = LoadCacheStatusAsync();
        }

        var originalColor = GUI.color;
        var originalEnabled = GUI.enabled;
        try
        {
            GUI.color = Color.white;
            GUI.enabled = true;

            var workspace = new Rect(
                EmbeddedOuterPadding,
                EmbeddedOuterPadding,
                Math.Max(0f, rect.width - EmbeddedOuterPadding * 2f),
                Math.Max(0f, rect.height - EmbeddedOuterPadding * 2f));

            GUI.Box(workspace, GUIContent.none);

            var headerRect = new Rect(
                workspace.x + EmbeddedGap,
                workspace.y + EmbeddedGap,
                workspace.width - EmbeddedGap * 2f,
                EmbeddedHeaderHeight);

            DrawInventoryHeader(headerRect);

            var contentTop = headerRect.yMax + EmbeddedGap;
            var availableHeight = Math.Max(120f, workspace.yMax - EmbeddedGap - contentTop);
            if (workspace.width >= EmbeddedRailBreakpoint)
            {
                var navigationWidth = Mathf.Clamp(
                    workspace.width * 0.115f,
                    172f,
                    EmbeddedNavigationWidth);
                var navigationRect = new Rect(
                    headerRect.x,
                    contentTop,
                    navigationWidth,
                    availableHeight);
                var bodyRect = new Rect(
                    navigationRect.xMax + EmbeddedGap,
                    contentTop,
                    Math.Max(260f, headerRect.xMax - (navigationRect.xMax + EmbeddedGap)),
                    availableHeight);

                DrawInventoryNavigationRail(navigationRect);
                DrawInventoryBody(bodyRect);
            }
            else
            {
                var navigationRect = new Rect(
                    headerRect.x,
                    contentTop,
                    headerRect.width,
                    EmbeddedCompactNavigationHeight);
                var bodyRect = new Rect(
                    headerRect.x,
                    navigationRect.yMax + EmbeddedGap,
                    headerRect.width,
                    Math.Max(100f, workspace.yMax - EmbeddedGap - (navigationRect.yMax + EmbeddedGap)));

                DrawCompactInventoryNavigation(navigationRect);
                DrawInventoryBody(bodyRect);
            }
        }
        finally
        {
            GUI.enabled = originalEnabled;
            GUI.color = originalColor;
        }
    }

    private void DrawInventoryHeader(Rect rect)
    {
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.BeginHorizontal();

        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label($"HERMES  /  {GetTabDisplayName(_activeTab).ToUpperInvariant()}");
        if (Plugin.Settings.ShowHelpText.Value && rect.width >= 900f)
        {
            GUILayout.Label("Read-only inventory intelligence and raid planning.");
        }
        else if (!string.IsNullOrWhiteSpace(_refreshStatus))
        {
            GUILayout.Label(_refreshStatus);
        }
        GUILayout.EndVertical();

        GUILayout.Space(8f);
        if (rect.width >= 760f)
        {
            if (GUILayout.Button("Reset", GUILayout.Width(72f), GUILayout.Height(28f)))
            {
                ClearCurrentTab();
            }
        }

        GUI.enabled = !_refreshingCurrent;
        if (GUILayout.Button(
                _refreshingCurrent ? "Working..." : "Refresh",
                GUILayout.Width(84f),
                GUILayout.Height(28f)))
        {
            _ = RefreshCurrentDataAsync();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Back", GUILayout.Width(72f), GUILayout.Height(28f)))
        {
            HermesNativeScreenRegistry.TryReturnToInventory();
        }

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawInventoryNavigationRail(Rect rect)
    {
        EnsureEnabledTabSelected();

        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label("WORKSPACES");
        GUILayout.Space(2f);

        if (Plugin.Settings.EnableAssistantTab.Value)
        {
            DrawNavigationButton(HermesTab.Assistant, "Assistant");
        }

        DrawNavigationButton(HermesTab.ItemSearch, "Items & Market");
        DrawNavigationButton(HermesTab.Hideout, "Hideout");
        DrawNavigationButton(HermesTab.Crafts, "Crafts");
        DrawNavigationButton(HermesTab.Stash, "Stash");
        DrawNavigationButton(HermesTab.Loadout, "Loadout");
        DrawNavigationButton(HermesTab.RaidPlanner, "Raid Planner");

        GUILayout.FlexibleSpace();
        GUILayout.Label("READ ONLY");
        if (Plugin.Settings.ShowDiagnosticsFooter.Value)
        {
            GUILayout.Label(FormatCompactDiagnosticsStatus());
            if (GUILayout.Button("Copy diagnostics", GUILayout.Height(26f)))
            {
                GUIUtility.systemCopyBuffer = BuildDiagnosticsReport();
                _refreshStatus = "Diagnostics copied.";
            }
        }
        GUILayout.EndArea();
    }

    private void DrawCompactInventoryNavigation(Rect rect)
    {
        EnsureEnabledTabSelected();

        GUILayout.BeginArea(rect, GUI.skin.box);
        var firstRow = new List<(HermesTab Tab, string Label)>();
        if (Plugin.Settings.EnableAssistantTab.Value)
        {
            firstRow.Add((HermesTab.Assistant, "Assistant"));
        }
        firstRow.Add((HermesTab.ItemSearch, "Items"));
        firstRow.Add((HermesTab.Hideout, "Hideout"));
        firstRow.Add((HermesTab.Crafts, "Crafts"));

        var secondRow = new List<(HermesTab Tab, string Label)>
        {
            (HermesTab.Stash, "Stash"),
            (HermesTab.Loadout, "Loadout"),
            (HermesTab.RaidPlanner, "Raid Planner")
        };

        DrawCompactNavigationRow(firstRow, rect.width);
        GUILayout.Space(3f);
        DrawCompactNavigationRow(secondRow, rect.width);
        GUILayout.EndArea();
    }

    private void DrawCompactNavigationRow(
        IReadOnlyList<(HermesTab Tab, string Label)> entries,
        float availableWidth)
    {
        GUILayout.BeginHorizontal();
        var width = Mathf.Clamp(
            (availableWidth - 18f - Math.Max(0, entries.Count - 1) * 4f) / Math.Max(1, entries.Count),
            72f,
            180f);
        foreach (var entry in entries)
        {
            DrawTabButton(entry.Tab, entry.Label, width);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawNavigationButton(HermesTab tab, string label)
    {
        var selected = _activeTab == tab;
        if (GUILayout.Button(
                (selected ? "▶  " : "    ") + label,
                GUILayout.Height(32f),
                GUILayout.ExpandWidth(true)))
        {
            SetActiveTab(tab);
        }
        GUILayout.Space(2f);
    }

    private void EnsureEnabledTabSelected()
    {
        if (_activeTab == HermesTab.Assistant && !Plugin.Settings.EnableAssistantTab.Value)
        {
            SetActiveTab(HermesTab.ItemSearch);
        }
    }

    private static string GetTabDisplayName(HermesTab tab)
    {
        return tab switch
        {
            HermesTab.Assistant => "Assistant",
            HermesTab.ItemSearch => "Items & Market",
            HermesTab.Hideout => "Hideout",
            HermesTab.Crafts => "Crafts",
            HermesTab.Stash => "Stash",
            HermesTab.Loadout => "Loadout",
            HermesTab.RaidPlanner => "Raid Planner",
            _ => "Workspace"
        };
    }

    private void DrawInventoryBody(Rect rect)
    {
        GUI.BeginGroup(rect);
        try
        {
            GUILayout.BeginArea(new Rect(0f, 0f, rect.width, rect.height));
            DrawActiveTabContent();
        }
        finally
        {
            GUILayout.EndArea();
            GUI.EndGroup();
        }
    }

    private void DrawActiveTabContent()
    {
        switch (_activeTab)
        {
            case HermesTab.Assistant:
                _noticeService.DrawInbox(OpenNoticeTarget);
                GUILayout.Space(HermesUi.StandardSpace);
                _assistantPanel.Draw(_selectedItem, _selectedStashInstanceKey, NavigateToTab);
                break;
            case HermesTab.Hideout:
                _hideoutPanel.Draw();
                break;
            case HermesTab.Crafts:
                _craftPanel.Draw();
                break;
            case HermesTab.Stash:
                _stashPanel.Draw();
                break;
            case HermesTab.Loadout:
                _loadoutPanel.Draw();
                break;
            case HermesTab.RaidPlanner:
                _loadoutPanel.OpenView("Raid Planner");
                _loadoutPanel.Draw();
                break;
            default:
                DrawItemSearchTab();
                break;
        }
    }

    private string FormatCompactDiagnosticsStatus()
    {
        var requests = HermesApiClient.GetDiagnosticsSnapshot();
        var cacheLabel = _cacheStatus is { Found: true }
            ? $"Cache {_cacheStatus.MarketSummaryEntryCount + _cacheStatus.MarketUnitValueEntryCount:N0}"
            : "Cache --";
        return $"{cacheLabel}  •  Req {requests.Active:N0}/{requests.Failed:N0}  •  Notices {_noticeService.ActiveNoticeCount:N0}";
    }

    private void DrawTabButton(HermesTab tab, string label, float width)
    {
        if (HermesUi.DrawTabButton(label, _activeTab == tab, width))
        {
            SetActiveTab(tab);
        }
    }

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

    private void DrawStashInstanceSection()
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _stashInstancesExpanded ? "▼" : "▶";
        var selectedLabel = _selectedStashInstanceKey is null
            ? "Base item estimate"
            : _stashInstances.FirstOrDefault(instance => instance.InstanceKey == _selectedStashInstanceKey)?.Label
              ?? "Selected owned copy";

        if (GUILayout.Button(
                $"{arrow}  OWNED COPY FOR TRADER SALE — {selectedLabel}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _stashInstancesExpanded = !_stashInstancesExpanded;
        }

        if (_loadingInstancePrice)
        {
            GUILayout.Label("Recalculating trader prices for the selected owned copy...");
        }

        if (_stashInstancesExpanded)
        {
            GUILayout.Space(4f);
            GUILayout.Label("Select the exact owned copy HERMES should value. Root and child-item value are included in trader sale estimates.");

            GUI.enabled = !_loadingInstancePrice && !_loadingDetails;
            var baseSelected = _selectedStashInstanceKey is null;
            if (GUILayout.Button(
                    (baseSelected ? "● " : string.Empty) + "Base item estimate — full condition, quantity 1, no installed items",
                    GUILayout.MinHeight(36f),
                    GUILayout.ExpandWidth(true)))
            {
                _ = SelectStashInstanceAsync(null);
            }

            foreach (var instance in _stashInstances)
            {
                var selected = string.Equals(
                    instance.InstanceKey,
                    _selectedStashInstanceKey,
                    StringComparison.OrdinalIgnoreCase);
                var valueText = instance.ConditionAdjustedReferenceValue > 0
                    ? $" - {instance.Location} - root RUB {instance.RootConditionAdjustedReferenceValue:N0} + child items RUB {instance.InstalledComponentReferenceValue:N0}"
                    : string.Empty;

                if (GUILayout.Button(
                        (selected ? "● " : string.Empty) + instance.Label + valueText,
                        GUILayout.MinHeight(42f),
                        GUILayout.ExpandWidth(true)))
                {
                    _ = SelectStashInstanceAsync(instance.InstanceKey);
                }
            }

            GUI.enabled = true;

            if (_stashInstances.Count == 0)
            {
                GUILayout.Label(_loadingDetails
                    ? "Loading matching owned copies..."
                    : "No matching owned copy is currently in the active PMC inventory. The base-item estimate is being used.");
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawTraderSaleSection(HermesTraderSummaryResponse summary)
    {
        GUILayout.Space(8f);

        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Trader information is unavailable.");
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _saleComparisonExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  TRADERS — {summary.SellOffers.Count:N0} SELL • {summary.PurchaseOffers.Count:N0} BUY",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _saleComparisonExpanded = !_saleComparisonExpanded;
        }

        DrawBestSale(summary);
        DrawBestTraderPurchase(summary);

        if (_saleComparisonExpanded)
        {
            if (!string.IsNullOrWhiteSpace(summary.Message))
            {
                GUILayout.Label(summary.Message);
            }

            if (!string.IsNullOrWhiteSpace(summary.SalePriceBasis))
            {
                GUILayout.Label(summary.SalePriceBasis);
                GUILayout.Space(4f);
            }

            GUILayout.Space(6f);
            GUILayout.Label("SALE COMPARISON ACROSS VANILLA TRADERS");

            if (summary.SellOffers.Count == 0)
            {
                if (summary.HasSupportedTraderBuyer && !summary.ReferencePrice.HasValue)
                {
                    GUILayout.Label("A supported vanilla trader accepts this item, but a sell-price estimate is unavailable because it has no handbook value.");
                }
                else
                {
                    GUILayout.Label("No supported vanilla trader buys this item.");
                }
            }
            else
            {
                foreach (var offer in summary.SellOffers)
                {
                    DrawSellOffer(offer);
                }
            }

            DrawTraderPurchaseSection(summary);
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestSale(HermesTraderSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(summary.UsesSelectedStashInstance
            ? "BEST SALE — SELECTED OWNED COPY"
            : "BEST SALE — BASE ITEM");

        var best = summary.BestSellOffer;
        if (best is null)
        {
            if (summary.HasSupportedTraderBuyer && !summary.ReferencePrice.HasValue)
            {
                GUILayout.Label("Trader acceptance confirmed; price estimate unavailable because no handbook value exists.");
            }
            else
            {
                GUILayout.Label("No supported vanilla trader buys this item.");
            }
        }
        else
        {
            GUILayout.Label($"{best.TraderName} — {FormatCurrency(best.Amount, best.Currency)}");
            if (!string.Equals(best.Currency, "RUB", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label($"Rouble equivalent: ₽{best.RoubleEquivalent:N0}");
            }

            if (summary.UsesSelectedStashInstance && Plugin.Settings.ShowFullAssemblyValuation.Value)
            {
                GUILayout.Label($"Root item value: ₽{best.RootRoubleEquivalent:N0}");
                GUILayout.Label($"Accepted installed value: ₽{best.InstalledComponentRoubleEquivalent:N0} ({best.IncludedWeaponAttachmentCount} attachment(s), {best.IncludedArmorInsertCount} armor insert(s))");
                if (best.IgnoredInstalledItemCount > 0)
                {
                    GUILayout.Label($"Ignored by {best.TraderName}: {best.IgnoredInstalledItemCount} installed item(s), reference basis ₽{best.IgnoredInstalledReferenceValue:N0}");
                }
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestTraderPurchase(HermesTraderSummaryResponse summary)
    {
        var best = summary.PurchaseOffers
            .Where(offer => offer.IsAvailable)
            .SelectMany(offer => offer.PaymentOptions
                .Where(payment => payment.EstimateAvailable && payment.EstimatedRoubleValue > 0)
                .Select(payment => new
                {
                    offer.TraderName,
                    payment.DisplayPrice,
                    payment.EstimatedRoubleValue
                }))
            .OrderBy(option => option.EstimatedRoubleValue)
            .FirstOrDefault();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("CHEAPEST AVAILABLE TRADER PURCHASE");
        if (best is null)
        {
            GUILayout.Label("No currently available vanilla-trader offer was found.");
        }
        else
        {
            GUILayout.Label($"{best.TraderName} — {best.DisplayPrice}");
            GUILayout.Label($"Estimated value: ₽{best.EstimatedRoubleValue:N0}");
        }
        GUILayout.EndVertical();
    }

    private static void DrawSellOffer(HermesSellOffer offer)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(offer.IsBest ? $"★ {offer.TraderName}" : offer.TraderName);
        GUILayout.FlexibleSpace();
        GUILayout.Label(FormatCurrency(offer.Amount, offer.Currency));
        GUILayout.EndHorizontal();
        GUILayout.Label($"Your loyalty level: {offer.PlayerLoyaltyLevel}");

        if (!string.Equals(offer.Currency, "RUB", StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.Label($"Rouble equivalent: ₽{offer.RoubleEquivalent:N0}");
        }

        if (Plugin.Settings.ShowFullAssemblyValuation.Value)
        {
            GUILayout.Label($"Root item: ₽{offer.RootRoubleEquivalent:N0}");
            if (offer.IncludedInstalledItemCount > 0)
            {
                GUILayout.Label($"Accepted installed items: ₽{offer.InstalledComponentRoubleEquivalent:N0} ({offer.IncludedWeaponAttachmentCount} attachment(s), {offer.IncludedArmorInsertCount} armor insert(s))");
            }
            else
            {
                GUILayout.Label("Accepted installed items: none");
            }

            if (offer.IgnoredInstalledItemCount > 0)
            {
                GUILayout.Label($"Ignored installed items: {offer.IgnoredInstalledItemCount} • reference basis ₽{offer.IgnoredInstalledReferenceValue:N0}");
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawMarketSection(HermesMarketSummaryResponse summary)
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _marketExpanded ? "▼" : "▶";
        var configuredMinimum = Plugin.Settings.GetMinimumComparableFleaOffers();
        var reliable = summary.MarketPriceFromActiveOffers && summary.ComparableOfferCount >= configuredMinimum;
        var reliabilityBadge = reliable ? "RELIABLE" : summary.MarketPriceFromActiveOffers ? "LOW SAMPLE" : "REFERENCE";
        var headline = summary.MedianPrice.HasValue
            ? summary.MarketPriceFromActiveOffers
                ? $"[{reliabilityBadge}] adjusted median ₽{summary.MedianPrice.Value:N0} • {summary.ComparableOfferCount:N0} comparable"
                : $"[{reliabilityBadge}] market reference ₽{summary.MedianPrice.Value:N0} • {summary.MarketPriceSource}"
            : "No flea or fallback market value";

        if (GUILayout.Button(
                $"{arrow}  LOCAL FLEA MARKET — {headline}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _marketExpanded = !_marketExpanded;
        }

        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Local flea information is unavailable.");
            GUILayout.EndVertical();
            return;
        }

        DrawMarketAtAGlance(summary);
        if (summary.MarketPriceFromActiveOffers
            && summary.ComparableOfferCount < Plugin.Settings.GetMinimumComparableFleaOffers())
        {
            GUILayout.Label($"Reliability warning: {summary.ComparableOfferCount:N0} comparable offer(s); F12 minimum is {Plugin.Settings.GetMinimumComparableFleaOffers():N0}.");
        }

        if (_marketExpanded)
        {
            GUILayout.Space(6f);
            DrawFleaAccess(summary);
            GUILayout.Space(6f);
            DrawFleaStatistics(summary);
            GUILayout.Space(6f);
            DrawFleaSaleAnalysis(summary);
            GUILayout.Space(6f);
            DrawFleaBuyAnalysis(summary);
            GUILayout.Space(6f);
            DrawLowestFleaOffers(summary);
        }

        GUILayout.EndVertical();
    }

    private static void DrawMarketAtAGlance(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("FLEA AT A GLANCE");
        if (!summary.FleaUnlocked)
        {
            GUILayout.Label($"Locked until level {summary.RequiredPlayerLevel}; current level {summary.PlayerLevel}.");
        }
        else if (!summary.CanSellOnFlea)
        {
            GUILayout.Label($"Cannot list: {summary.SellUnavailableReason ?? "Unavailable"}");
        }
        else
        {
            var suggested = summary.SuggestedListPrice.HasValue
                ? $"₽{summary.SuggestedListPrice.Value:N0}"
                : "unavailable";
            var fee = summary.EstimatedListingFee.HasValue
                ? $"₽{summary.EstimatedListingFee.Value:N0}"
                : "unavailable";
            var net = summary.EstimatedNetSale.HasValue
                ? $"₽{summary.EstimatedNetSale.Value:N0}"
                : "unavailable";
            var basis = summary.UsesSelectedOwnedCopy ? "selected copy" : "base item";
            GUILayout.Label($"Suggested {basis} list: {suggested} - Fee: {fee} - Net: {net}");
        }

        GUILayout.Label(summary.LowestPrice.HasValue
            ? $"Buy reference: lowest ₽{summary.LowestPrice.Value:N0} • median ₽{summary.MedianPrice.GetValueOrDefault():N0}"
            : "Buy reference: no comparable offer available.");
        GUILayout.EndVertical();
    }

    private static void DrawFleaAccess(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("FLEA ACCESS");
        GUILayout.Label(summary.FleaUnlocked
            ? $"Unlocked — player level {summary.PlayerLevel}"
            : $"Locked — level {summary.RequiredPlayerLevel} required; current level {summary.PlayerLevel}");
        GUILayout.Label(summary.CanSellOnFlea
            ? "Listing eligibility: This item can be listed."
            : $"Listing eligibility: {summary.SellUnavailableReason ?? "Unavailable"}");
        GUILayout.EndVertical();
    }

    private static void DrawFleaStatistics(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("CURRENT MARKET VALUE");

        if (!summary.LowestPrice.HasValue)
        {
            GUILayout.Label("No active flea offer, converted barter, SPT dynamic price, or handbook fallback could be valued.");
        }
        else if (summary.MarketPriceFromActiveOffers)
        {
            if (summary.LowestListedPrice.HasValue)
            {
                GUILayout.Label($"Best comparable listing total: ₽{summary.LowestListedPrice.Value:N0}");
            }

            GUILayout.Label($"Component-adjusted lowest: ₽{summary.LowestPrice.Value:N0}");
            GUILayout.Label($"Component-adjusted median: ₽{summary.MedianPrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Component-adjusted average: ₽{summary.AveragePrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Component-adjusted highest reasonable: ₽{summary.HighestReasonablePrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Source: {summary.MarketPriceSource}");
        }
        else
        {
            GUILayout.Label($"Market reference: ₽{summary.LowestPrice.Value:N0}");
            GUILayout.Label($"Source: {summary.MarketPriceSource}");
            if (summary.MarketPriceUsedHandbookFallback)
            {
                GUILayout.Label("Fallback note: No active cash offer, convertible barter offer, or SPT dynamic flea-market price was available.");
            }
            GUILayout.Label("No active comparable offer was available. This reference is not treated as a reliable listing recommendation.");
        }

        GUILayout.Label($"Valid cash offers found: {summary.ValidCashOfferCount:N0}");
        if (Plugin.Settings.ShowConvertedBarterOffers.Value)
        {
            GUILayout.Label($"Converted barter offers found: {summary.ConvertedBarterOfferCount:N0}");
        }
        if (Plugin.Settings.ShowConvertedBarterOffers.Value && summary.BarterOffersUsingHandbookFallback > 0)
        {
            GUILayout.Label($"Converted barters using handbook fallback: {summary.BarterOffersUsingHandbookFallback:N0}");
        }
        GUILayout.Label($"Offers used for comparison: {summary.ComparableOfferCount:N0}");
        GUILayout.Label($"Offers with installed attachments or armor inserts: {summary.OffersWithInstalledComponents:N0}");

        GUILayout.Label(
            "Valuation order: active local cash flea offer → converted flea barter offer → SPT dynamic flea-market price → handbook fallback. The same chain is used for barter requirements, installed weapon attachments, armor inserts, stash values, crafts, and hideout estimates. Stored container contents and loaded ammunition are ignored when decomposing an assembly.");

        if (summary.UsedLowConditionFallback)
        {
            GUILayout.Label("Condition note: No 80%+ root-condition offers were found, so used-condition offers were analyzed.");
        }

        var ignoredParts = new List<string>();
        if (summary.IgnoredBarterOfferCount > 0)
        {
            ignoredParts.Add($"unpriced barter {summary.IgnoredBarterOfferCount}");
        }

        if (summary.IgnoredTraderOfferCount > 0)
        {
            ignoredParts.Add($"trader duplicates {summary.IgnoredTraderOfferCount}");
        }

        if (summary.IgnoredExpiredOrInvalidOfferCount > 0)
        {
            ignoredParts.Add($"expired/invalid {summary.IgnoredExpiredOrInvalidOfferCount}");
        }

        if (summary.IgnoredLowConditionOfferCount > 0)
        {
            ignoredParts.Add($"below 80% root condition {summary.IgnoredLowConditionOfferCount}");
        }

        if (summary.IgnoredOutlierCount > 0)
        {
            ignoredParts.Add($"high outliers {summary.IgnoredOutlierCount}");
        }

        if (ignoredParts.Count > 0)
        {
            GUILayout.Label("Ignored: " + string.Join(" • ", ignoredParts));
        }

        GUILayout.EndVertical();
    }

    private static void DrawFleaSaleAnalysis(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("SELL ANALYSIS");

        if (!summary.FleaUnlocked || !summary.CanSellOnFlea)
        {
            GUILayout.Label(summary.SellUnavailableReason ?? "This item cannot currently be listed.");
        }
        else if (!summary.SuggestedListPrice.HasValue)
        {
            GUILayout.Label(summary.LowestPrice.HasValue
                ? $"No active comparable offer was found. Reference only: ₽{summary.LowestPrice.Value:N0} ({summary.MarketPriceSource})."
                : "A suggested listing price is unavailable because no market value could be resolved.");
        }
        else
        {
            var saleBasis = summary.UsesSelectedOwnedCopy ? "selected-copy" : "base-item";
            GUILayout.Label($"Suggested {saleBasis} listing price: RUB {summary.SuggestedListPrice.Value:N0}");
            if (summary.UsesSelectedOwnedCopy)
            {
                GUILayout.Label($"Selected copy: {summary.SelectedOwnedCopyLabel} - {summary.SelectedOwnedCopyLocation} - root RUB {summary.SelectedOwnedCopyRootValue.GetValueOrDefault():N0} + child items RUB {summary.SelectedOwnedCopyChildValue.GetValueOrDefault():N0}");
            }
            if (Plugin.Settings.ShowListingFeeEstimates.Value)
            {
                GUILayout.Label(summary.EstimatedListingFee.HasValue
                    ? $"Estimated listing fee: ₽{summary.EstimatedListingFee.Value:N0}"
                    : "Estimated listing fee: unavailable");
                GUILayout.Label(summary.EstimatedNetSale.HasValue
                    ? $"Estimated {saleBasis} net sale: ₽{summary.EstimatedNetSale.Value:N0}"
                    : $"Estimated {saleBasis} net sale: unavailable");
            }
        }

        if (summary.BestTraderSellPrice.HasValue)
        {
            GUILayout.Label($"Best trader estimate: {summary.BestTraderSellName} — ₽{summary.BestTraderSellPrice.Value:N0}");
        }

        GUILayout.Label("Recommendation: " + summary.SellRecommendation);
        GUILayout.EndVertical();
    }

    private static void DrawFleaBuyAnalysis(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("BUY ANALYSIS");

        GUILayout.Label(summary.LowestListedPrice.HasValue
            ? summary.LowestOfferIsBarter
                ? $"Converted requirement value for best barter assembly: ₽{summary.LowestListedPrice.Value:N0}"
                : $"Cash required for best comparable flea assembly: ₽{summary.LowestListedPrice.Value:N0}"
            : "Best comparable flea assembly value: unavailable");

        GUILayout.Label(summary.LowestPrice.HasValue
            ? summary.MarketPriceFromActiveOffers
                ? $"Component-adjusted active-market value: ₽{summary.LowestPrice.Value:N0}"
                : $"Fallback market reference: ₽{summary.LowestPrice.Value:N0} ({summary.MarketPriceSource})"
            : "Component-adjusted market value: unavailable");

        GUILayout.Label(summary.CheapestAvailableTraderBuyPrice.HasValue
            ? $"Cheapest available cash trader: {summary.CheapestAvailableTraderName} — ₽{summary.CheapestAvailableTraderBuyPrice.Value:N0}"
            : "Cheapest available cash trader: none found");

        GUILayout.Label("Recommendation: " + summary.BuyRecommendation);
        GUILayout.EndVertical();
    }

    private static void DrawLowestFleaOffers(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("LOWEST COMPONENT-ADJUSTED OFFERS");

        if (summary.LowestOffers.Count == 0)
        {
            GUILayout.Label("No comparable offers to display.");
        }
        else
        {
            var offers = summary.LowestOffers
                .Where(offer => Plugin.Settings.ShowConvertedBarterOffers.Value || !offer.IsBarter)
                .Take(Plugin.Settings.GetMaximumFleaOffersDisplayed())
                .ToList();
            if (offers.Count == 0)
            {
                GUILayout.Label("No offers match the current Market Intelligence display settings.");
            }
            foreach (var offer in offers)
            {
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Base-item equivalent: ₽{offer.UnitPrice:N0}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(offer.IsBarter
                    ? $"Converted requirements: ₽{offer.ListedUnitPrice:N0}"
                    : $"Listed total: ₽{offer.ListedUnitPrice:N0}");
                GUILayout.EndHorizontal();

                if (offer.IsBarter)
                {
                    GUILayout.Label($"Barter offer • {offer.BarterRequirementCount:N0} requirement type(s)");
                    GUILayout.Label("Conversion source: " + offer.PriceSource);
                    if (offer.UsedHandbookFallback)
                    {
                        GUILayout.Label("Fallback note: At least one requirement had no current cash flea offer, so its handbook value was used.");
                    }
                }

                if (Plugin.Settings.ShowFullAssemblyValuation.Value
                    && (offer.InstalledComponentValue > 0
                        || offer.WeaponAttachmentCount > 0
                        || offer.ArmorInsertCount > 0))
                {
                    GUILayout.Label(
                        $"Installed value: ₽{offer.InstalledComponentValue:N0} • "
                        + $"Weapon attachments: {offer.WeaponAttachmentCount:N0} • "
                        + $"Armor inserts: {offer.ArmorInsertCount:N0}");
                }
                else if (Plugin.Settings.ShowFullAssemblyValuation.Value)
                {
                    GUILayout.Label("Installed value: none");
                }

                GUILayout.Label(
                    $"Qty {offer.Quantity:N0} • Root condition {offer.ConditionLabel} {offer.ConditionPercent}% • "
                    + $"{FormatDuration(offer.SecondsRemaining)} left");

                GUILayout.EndVertical();
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawTraderPurchaseSection(HermesTraderSummaryResponse summary)
    {
        if (!summary.Found)
        {
            return;
        }

        GUILayout.Space(8f);
        GUILayout.Label("BUY FROM TRADERS");
        if (summary.PurchaseOffers.Count == 0)
        {
            GUILayout.Label("No current vanilla-trader offer was found for this item.");
        }
        else
        {
            foreach (var offer in summary.PurchaseOffers)
            {
                DrawPurchaseOffer(offer);
            }
        }
    }

    private static void DrawPurchaseOffer(HermesPurchaseOffer offer)
    {
        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{offer.TraderName} LL{offer.RequiredLoyaltyLevel}");
        GUILayout.FlexibleSpace();
        GUILayout.Label(offer.IsAvailable ? "AVAILABLE" : "LOCKED");
        GUILayout.EndHorizontal();

        GUILayout.Label($"Your loyalty level: {offer.PlayerLoyaltyLevel}");
        GUILayout.Label($"Status: {offer.AvailabilityReason}");

        if (!string.IsNullOrWhiteSpace(offer.RequiredQuestName))
        {
            if (offer.IsAvailable)
            {
                GUILayout.Label($"Quest unlock: {offer.RequiredQuestName} ({offer.RequiredQuestState ?? "requirement met"})");
            }
            else
            {
                GUILayout.Label($"Quest requirement: {offer.QuestRequirementText ?? offer.RequiredQuestName}");
            }
        }

        GUILayout.Label(offer.UnlimitedStock
            ? "Stock: Unlimited"
            : offer.StockRemaining.HasValue
                ? $"Stock remaining: {offer.StockRemaining.Value:N0}"
                : "Stock: Unknown");

        if (offer.PurchaseLimit.HasValue)
        {
            var remaining = offer.PurchaseLimitRemaining.HasValue
                ? offer.PurchaseLimitRemaining.Value.ToString("N0")
                : "unknown";
            GUILayout.Label($"Personal limit: {remaining} of {offer.PurchaseLimit.Value:N0} remaining");
        }

        if (offer.SecondsUntilRestock.HasValue)
        {
            GUILayout.Label($"Restock: {FormatDuration(offer.SecondsUntilRestock.Value)}");
        }

        if (offer.PaymentOptions.Count == 0)
        {
            GUILayout.Label("Payment information unavailable.");
        }
        else
        {
            GUILayout.Label("Payment options:");
            foreach (var payment in offer.PaymentOptions)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(payment.IsCash ? $"Cash — {payment.DisplayPrice}" : $"Barter — {payment.DisplayPrice}");

                if (!payment.IsCash)
                {
                    if (payment.EstimateAvailable && payment.EstimatedRoubleValue > 0)
                    {
                        GUILayout.Label($"Current market estimate: ₽{payment.EstimatedRoubleValue:N0}");
                        if (!string.IsNullOrWhiteSpace(payment.EstimateSource))
                        {
                            GUILayout.Label($"Source: {payment.EstimateSource}");
                        }

                        if (payment.UsedHandbookFallback)
                        {
                            GUILayout.Label("Fallback note: One or more required items had no active cash offer, convertible barter offer, or SPT dynamic flea-market price, so HERMES used handbook value.");
                        }

                        if (Plugin.Settings.ShowDetailedBarterCalculations.Value && payment.Requirements.Count > 0)
                        {
                            GUILayout.Space(3f);
                            GUILayout.Label("Market calculation:");
                            foreach (var requirement in payment.Requirements)
                            {
                                if (!requirement.EstimateAvailable
                                    || !requirement.EstimatedUnitRoubleValue.HasValue
                                    || !requirement.EstimatedSubtotalRoubleValue.HasValue)
                                {
                                    GUILayout.Label($"• {FormatCount(requirement.Count)} × {requirement.Name} — value unavailable");
                                    continue;
                                }

                                var sourceLabel = requirement.Currency is not null
                                    ? "Trader currency conversion"
                                    : requirement.EstimateSource;

                                GUILayout.Label(
                                    $"• {FormatCount(requirement.Count)} × {requirement.Name} — " +
                                    $"₽{requirement.EstimatedUnitRoubleValue.Value:N0} each • " +
                                    $"subtotal ₽{requirement.EstimatedSubtotalRoubleValue.Value:N0} ({sourceLabel})");
                            }
                        }
                    }
                    else
                    {
                        GUILayout.Label("Current market estimate unavailable.");
                        if (!string.IsNullOrWhiteSpace(payment.EstimateSource))
                        {
                            GUILayout.Label($"Reason: {payment.EstimateSource}");
                        }

                        foreach (var requirement in payment.Requirements.Where(requirement => !requirement.EstimateAvailable))
                        {
                            GUILayout.Label($"• {FormatCount(requirement.Count)} × {requirement.Name} — {requirement.EstimateSource}");
                        }
                    }
                }

                GUILayout.EndVertical();
            }
        }

        GUILayout.EndVertical();
        GUILayout.Space(4f);
    }

    private void DrawHideoutUsageSection(HermesItemHideoutUsageResponse usage)
    {
        GUILayout.Space(8f);
        if (!usage.Found)
        {
            GUILayout.Label(usage.Message ?? "Quest, key, Hideout, and crafting usage is unavailable.");
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"PROFILE OWNERSHIP — {usage.OwnedQuantity:N0} total • {usage.OwnedFoundInRaidQuantity:N0} FIR");
        GUILayout.EndVertical();

        DrawQuestRequirementsSection(usage);
        DrawQuestKeysSection(usage);
        DrawHideoutCraftUsesSection(usage);
    }

    private void DrawQuestRequirementsSection(HermesItemHideoutUsageResponse usage)
    {
        var active = usage.QuestUses
            .Where(quest => quest.IsActive && !quest.ConditionCompleted && !quest.QuestCompleted)
            .OrderByDescending(quest => quest.Missing > 0d)
            .ThenBy(quest => quest.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remaining = usage.QuestUses.Count(quest => !quest.ConditionCompleted && !quest.QuestCompleted);
        var completed = usage.QuestUses.Count - remaining;
        var first = active.FirstOrDefault()
                    ?? usage.QuestUses.FirstOrDefault(quest => !quest.ConditionCompleted && !quest.QuestCompleted)
                    ?? usage.QuestUses.FirstOrDefault();

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _questRequirementsExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  QUEST REQUIREMENTS — {active.Count:N0} ACTIVE • {remaining:N0} REMAINING",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _questRequirementsExpanded = !_questRequirementsExpanded;
        }

        GUILayout.Label(first is null
            ? "No standard quest item requirement uses this item."
            : first.ConditionCompleted || first.QuestCompleted
                ? $"Completed use: {first.QuestName}."
                : $"{(first.IsActive ? "ACTIVE" : "FUTURE")}: {first.QuestName} — {first.ProgressText}");
        GUILayout.Label($"Owned: {usage.OwnedQuantity:N0} total • {usage.OwnedFoundInRaidQuantity:N0} FIR • Completed uses: {completed:N0}");

        if (_questRequirementsExpanded)
        {
            GUILayout.Space(6f);
            if (usage.QuestUses.Count == 0)
            {
                GUILayout.Label("No player-facing item requirement was found in standard quest completion conditions.");
            }
            else
            {
                foreach (var quest in usage.QuestUses)
                {
                    var marker = quest.ConditionCompleted ? "✓" : quest.IsActive ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {quest.QuestName} — {quest.TraderName}");
                    GUILayout.Label($"Status: {quest.QuestStatus} • Action: {quest.ConditionType}");
                    GUILayout.Label($"Required: {quest.Required:N0}{(quest.FoundInRaidRequired ? " FIR" : string.Empty)} • Owned matching targets: {quest.OwnedMatchingTargets:N0} • This item: {quest.OwnedSelectedItem:N0}");
                    GUILayout.Label(quest.ProgressText);
                    if (!quest.ConditionCompleted && quest.Missing > 0d)
                    {
                        GUILayout.Label($"Missing: {quest.Missing:N0}");
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawQuestKeysSection(HermesItemHideoutUsageResponse usage)
    {
        var active = usage.QuestKeyUses
            .Where(key => key.IsActive && !key.QuestCompleted)
            .OrderBy(key => key.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remaining = usage.QuestKeyUses.Count(key => !key.QuestCompleted);
        var completed = usage.QuestKeyUses.Count - remaining;
        var first = active.FirstOrDefault()
                    ?? usage.QuestKeyUses.FirstOrDefault(key => !key.QuestCompleted)
                    ?? usage.QuestKeyUses.FirstOrDefault();

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _questKeysExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  QUEST KEY KNOWLEDGE — {active.Count:N0} ACTIVE • {remaining:N0} REMAINING",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _questKeysExpanded = !_questKeysExpanded;
        }

        GUILayout.Label(first is null
            ? "This item is not linked to a known quest-key requirement."
            : $"{(first.IsActive && !first.QuestCompleted ? "ACTIVE" : first.QuestCompleted ? "COMPLETED" : "KNOWN")}: {first.QuestName} — {first.MapName} — opens {first.Opens}");
        GUILayout.Label($"Completed key uses: {completed:N0}");

        if (_questKeysExpanded)
        {
            GUILayout.Space(6f);
            if (usage.QuestKeyUses.Count == 0)
            {
                GUILayout.Label("The installed key and quest databases do not associate this item with a known quest lock.");
            }
            else
            {
                foreach (var keyUse in usage.QuestKeyUses)
                {
                    var marker = keyUse.QuestCompleted ? "✓" : keyUse.IsActive ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {keyUse.QuestName} — {keyUse.MapName}");
                    GUILayout.Label($"Status: {keyUse.QuestStatus}{(keyUse.AcquireInRaid ? " • Acquire during raid" : string.Empty)}");
                    if (!string.IsNullOrWhiteSpace(keyUse.Opens))
                    {
                        GUILayout.Label($"Opens: {keyUse.Opens}");
                    }
                    if (!string.IsNullOrWhiteSpace(keyUse.Purpose))
                    {
                        GUILayout.Label(keyUse.Purpose);
                    }
                    if (!string.IsNullOrWhiteSpace(keyUse.Acquisition))
                    {
                        GUILayout.Label($"Acquisition: {keyUse.Acquisition}");
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawHideoutCraftUsesSection(HermesItemHideoutUsageResponse usage)
    {
        var nextUpgrade = usage.UpgradeUses
            .Where(upgrade => !upgrade.IsMet && upgrade.TargetLevel > upgrade.CurrentLevel)
            .OrderByDescending(upgrade => upgrade.IsNextUpgrade)
            .ThenBy(upgrade => upgrade.TargetLevel)
            .ThenBy(upgrade => upgrade.AreaName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var readyCraft = usage.UsedBy
            .Concat(usage.ProducedBy)
            .OrderByDescending(craft => craft.CanStartNow || craft.IsComplete)
            .ThenBy(craft => craft.StationName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var totalUses = usage.UpgradeUses.Count + usage.ProducedBy.Count + usage.UsedBy.Count;

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _hideoutCraftUsesExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  HIDEOUT & CRAFT USES — {totalUses:N0}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _hideoutCraftUsesExpanded = !_hideoutCraftUsesExpanded;
        }

        GUILayout.Label(nextUpgrade is not null
            ? $"Next upgrade: {nextUpgrade.AreaName} L{nextUpgrade.TargetLevel} • Owned {nextUpgrade.Owned:N0}/{nextUpgrade.Required:N0} • Missing {nextUpgrade.Missing:N0}"
            : readyCraft is not null
                ? $"Craft use: {readyCraft.StationName} L{readyCraft.RequiredStationLevel} • {readyCraft.Status}"
                : "No Hideout upgrade or player-facing recipe currently uses this item.");
        GUILayout.Label($"Upgrades: {usage.UpgradeUses.Count:N0} • Produced by: {usage.ProducedBy.Count:N0} • Ingredient for: {usage.UsedBy.Count:N0}");

        if (_hideoutCraftUsesExpanded)
        {
            GUILayout.Space(8f);
            GUILayout.Label("HIDEOUT UPGRADES");
            if (usage.UpgradeUses.Count == 0)
            {
                GUILayout.Label("Not required by a player-facing Hideout upgrade.");
            }
            else
            {
                foreach (var upgrade in usage.UpgradeUses)
                {
                    var marker = upgrade.TargetLevel <= upgrade.CurrentLevel || upgrade.IsMet ? "✓" : upgrade.IsNextUpgrade ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {upgrade.AreaName} Level {upgrade.TargetLevel} — {upgrade.Status}");
                    GUILayout.Label($"Current area level: {upgrade.CurrentLevel} • Required: {upgrade.Required:N0}{(upgrade.FoundInRaidRequired ? " FIR" : string.Empty)} • Owned: {upgrade.Owned:N0} • Missing: {upgrade.Missing:N0}");
                    if (upgrade.EstimatedMissingCost.HasValue)
                    {
                        GUILayout.Label($"Estimated missing cost: ₽{upgrade.EstimatedMissingCost.Value:N0}{(string.IsNullOrWhiteSpace(upgrade.AcquisitionSource) ? string.Empty : $" via {upgrade.AcquisitionSource}")}");
                    }
                    GUILayout.EndVertical();
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("PRODUCED BY");
            if (usage.ProducedBy.Count == 0)
            {
                GUILayout.Label("No player-facing Hideout recipe produces this item.");
            }
            else
            {
                foreach (var craft in usage.ProducedBy)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"• {craft.StationName} L{craft.RequiredStationLevel} — produces {craft.OutputQuantity:N0} × {craft.OutputName}");
                    GUILayout.Label($"Current station: L{craft.CurrentStationLevel} • {craft.Status} • {FormatDuration(craft.DurationSeconds)}");
                    if (craft.IsActive || craft.IsComplete)
                    {
                        GUILayout.Label(craft.IsComplete ? "Production complete — ready to collect" : "Production currently active");
                    }
                    GUILayout.EndVertical();
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("USED AS AN INGREDIENT");
            if (usage.UsedBy.Count == 0)
            {
                GUILayout.Label("Not used by a player-facing Hideout recipe.");
            }
            else
            {
                foreach (var craft in usage.UsedBy)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"• {craft.ItemCount:N0} × for {craft.OutputName} at {craft.StationName} L{craft.RequiredStationLevel}");
                    GUILayout.Label($"Current station: L{craft.CurrentStationLevel} • Owned: {craft.Owned:N0} • Missing: {craft.Missing:N0} • {craft.Status}");
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    private string FormatDiagnosticsStatus()
    {
        var requests = HermesApiClient.GetDiagnosticsSnapshot();
        if (_cacheStatus is null || !_cacheStatus.Found)
        {
            return $"Caches unavailable • Requests: {requests.Active:N0} active, {requests.Completed:N0} completed, {requests.Failed:N0} failed";
        }

        var marketEntries = _cacheStatus.MarketUnitValueEntryCount + _cacheStatus.MarketSummaryEntryCount;
        return $"Cache M/S/L: {marketEntries:N0}/{_cacheStatus.StashAnalysisEntryCount:N0}/{_cacheStatus.LoadoutAnalysisEntryCount:N0}"
               + $" • Requests: {requests.Active:N0} active, {requests.Completed:N0} ok, {requests.Failed:N0} failed, {requests.DeduplicatedRequests:N0} shared"
               + $" • Alerts: {_noticeService.ActiveNoticeCount:N0}";
    }

    private string BuildDiagnosticsReport()
    {
        var requests = HermesApiClient.GetDiagnosticsSnapshot();
        var lines = new List<string>
        {
            $"HERMES {HermesVersionInfo.DisplayVersion} diagnostics",
            $"Active tab: {_activeTab}",
            $"Client requests: started={requests.Started}, completed={requests.Completed}, failed={requests.Failed}, active={requests.Active}",
            $"Failures: timeout={requests.TimedOut}, transport={requests.TransportFailures}, invalid-response={requests.InvalidResponses}",
            $"Performance: slow={requests.SlowRequests}, shared-duplicates={requests.DeduplicatedRequests}, last-duration-ms={requests.LastDurationMilliseconds}",
            $"Last route: {requests.LastRoute}",
            $"Last failure: {(string.IsNullOrWhiteSpace(requests.LastFailure) ? "none" : requests.LastFailure)}",
            $"Assistant alerts: {_noticeService.GetDiagnosticsSummary()}"
        };

        if (_cacheStatus is { Found: true })
        {
            var marketEntries = _cacheStatus.MarketUnitValueEntryCount + _cacheStatus.MarketSummaryEntryCount;
            lines.Add($"Market cache: entries={marketEntries}, hits={_cacheStatus.CacheHits}, misses={_cacheStatus.CacheMisses}, writes={_cacheStatus.CacheWrites}, ttl={_cacheStatus.TtlSeconds}s");
            lines.Add($"Stash cache: entries={_cacheStatus.StashAnalysisEntryCount}, hits={_cacheStatus.StashCacheHits}, misses={_cacheStatus.StashCacheMisses}, writes={_cacheStatus.StashCacheWrites}, ttl={_cacheStatus.StashTtlSeconds}s");
            lines.Add($"Loadout cache: entries={_cacheStatus.LoadoutAnalysisEntryCount}, hits={_cacheStatus.LoadoutCacheHits}, misses={_cacheStatus.LoadoutCacheMisses}, writes={_cacheStatus.LoadoutCacheWrites}, ttl={_cacheStatus.LoadoutTtlSeconds}s");
            lines.Add($"Last cache invalidation: {_cacheStatus.LastInvalidationReason}");
        }
        else
        {
            lines.Add($"Server cache status: {_cacheStatus?.Message ?? "unavailable"}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task LoadCacheStatusAsync()
    {
        try
        {
            _cacheStatus = await HermesApiClient.GetCacheStatusAsync();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
            _cacheStatus = new HermesCacheStatusResponse
            {
                Found = false,
                Message = HermesApiClient.DescribeFailure(ex, "Cache status")
            };
        }
        finally
        {
            _nextCacheStatusRefresh = Time.realtimeSinceStartup + Plugin.Settings.GetCacheStatusRefreshSeconds();
            _cacheStatusLoading = false;
        }
    }

    private async Task RefreshCurrentDataAsync(bool clearCaches = true)
    {
        if (_refreshingCurrent)
        {
            return;
        }

        _refreshingCurrent = true;
        var isProfileWorkspace = _activeTab is HermesTab.Assistant
            or HermesTab.Hideout
            or HermesTab.Crafts
            or HermesTab.Stash
            or HermesTab.Loadout
            or HermesTab.RaidPlanner;
        var clearedCaches = false;
        _refreshStatus = isProfileWorkspace
            ? "Asking the server to check the active PMC sources..."
            : clearCaches
                ? "Clearing short-lived item caches and reloading the current view..."
                : "Reloading the current view...";

        try
        {
            if (isProfileWorkspace)
            {
                var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
                if (coordinator is null)
                {
                    throw new InvalidOperationException("The HERMES workspace coordinator is unavailable.");
                }

                var tabName = GetTabName(_activeTab);
                _refreshStatus = $"Asking the server to verify and refresh {GetTabDisplayName(_activeTab)}...";
                await coordinator.RefreshWorkspaceAsync(tabName, manual: true);
                _refreshStatus = $"{GetTabDisplayName(_activeTab)} refreshed from the latest server sources.";
                return;
            }
            else if (clearCaches)
            {
                var cleared = await HermesApiClient.ClearCachesAsync();
                _cacheStatus = cleared.Status;
                _refreshStatus = cleared.Message;
                clearedCaches = true;
            }

            switch (_activeTab)
            {
                case HermesTab.Assistant:
                    HermesWorkspaceSnapshotCoordinator.Current?.RefreshNoticesFromLoadedData(manual: true);
                    _refreshStatus = "Checking current profile sources. Alerts and pages change only when new semantic data is found.";
                    break;
                case HermesTab.Hideout:
                case HermesTab.Crafts:
                case HermesTab.Stash:
                case HermesTab.Loadout:
                case HermesTab.RaidPlanner:
                    _refreshStatus = "Checking current profile sources. This workspace changes only when new semantic data is found.";
                    break;
                default:
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
                    }
                    else if (!string.IsNullOrWhiteSpace(_query))
                    {
                        await RunSearchAsync();
                    }
                    break;
            }

            if (clearedCaches)
            {
                await LoadCacheStatusAsync();
            }
        }
        catch (Exception ex)
        {
            _refreshStatus = HermesApiClient.DescribeFailure(ex, "Current-data refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _refreshingCurrent = false;
        }
    }

    private void ClearCurrentTab()
    {
        switch (_activeTab)
        {
            case HermesTab.Assistant:
                _assistantPanel.Clear();
                break;
            case HermesTab.Hideout:
                _hideoutPanel.Clear();
                break;
            case HermesTab.Crafts:
                _craftPanel.Clear();
                break;
            case HermesTab.Stash:
                _stashPanel.Clear();
                break;
            case HermesTab.Loadout:
            case HermesTab.RaidPlanner:
                _loadoutPanel.Clear();
                break;
            default:
                Clear();
                break;
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

    private void Clear()
    {
        _searchRequestVersion++;
        _openRequestVersion++;
        _detailRequestVersion++;
        _instanceRequestVersion++;
        _searching = false;
        _loadingDetails = false;
        _query = string.Empty;
        _results = [];
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _hideoutUsage = null;
        _stashInstances = [];
        _selectedStashInstanceKey = null;
        _loadingInstancePrice = false;
        ResetSectionExpansionDefaults();
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _status = "Search for an item or ask where it can be bought or sold.";
        _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";
    }

    internal void OpenNativeNoticeTarget(string tabName)
    {
        OpenNoticeTarget(tabName);
    }

    private void OpenNoticeTarget(string tabName)
    {
        EnsurePresentationVisible();
        NavigateToTab(tabName);
    }

    private void NavigateToTab(string tabName)
    {
        var parts = tabName.Split(new[] { '/' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .ToArray();
        var tab = ParseTabName(parts.ElementAtOrDefault(0) ?? tabName);
        if (tab == HermesTab.Loadout && parts.Length > 1)
        {
            _loadoutPanel.OpenView(parts[1]);
            if (string.Equals(parts[1], "Raid Planner", StringComparison.OrdinalIgnoreCase))
            {
                tab = HermesTab.RaidPlanner;
            }
        }
        else if (tab == HermesTab.RaidPlanner)
        {
            _loadoutPanel.OpenView("Raid Planner");
        }

        SetActiveTab(tab);
    }

    private void SetActiveTab(HermesTab tab)
    {
        if (tab == HermesTab.Assistant && !Plugin.Settings.EnableAssistantTab.Value)
        {
            tab = HermesTab.ItemSearch;
        }

        if (_activeTab == tab)
        {
            return;
        }

        var previousTab = _activeTab;
        _activeTab = tab;
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        if (tab == HermesTab.RaidPlanner)
        {
            _loadoutPanel.OpenView("Raid Planner");
        }
        else if (tab == HermesTab.Loadout && previousTab == HermesTab.RaidPlanner)
        {
            _loadoutPanel.OpenView("Overview");
        }
        var tabName = GetTabName(tab);
        Plugin.Settings.RememberTab(tabName);
        HermesWorkspaceSnapshotCoordinator.Current?.OnWorkspaceSelected(tabName);
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogDebug($"HERMES active tab changed to {tabName}; prepared server summary refresh started.");
        }
    }

    private static HermesTab ParseTabName(string value)
    {
        var tab = value.Trim().ToLowerInvariant() switch
        {
            "assistant" or "chat" => HermesTab.Assistant,
            "hideout" => HermesTab.Hideout,
            "crafts" or "craft" => HermesTab.Crafts,
            "stash" => HermesTab.Stash,
            "loadout" => HermesTab.Loadout,
            "raid" or "raid planner" => HermesTab.RaidPlanner,
            _ => HermesTab.ItemSearch
        };

        return tab == HermesTab.Assistant && !Plugin.Settings.EnableAssistantTab.Value
            ? HermesTab.ItemSearch
            : tab;
    }

    private static string GetTabName(HermesTab tab)
    {
        return tab switch
        {
            HermesTab.Assistant => "Assistant",
            HermesTab.Hideout => "Hideout",
            HermesTab.Crafts => "Crafts",
            HermesTab.Stash => "Stash",
            HermesTab.Loadout => "Loadout",
            HermesTab.RaidPlanner => "Raid Planner",
            _ => "Item Search"
        };
    }

    private void ApplySmartItemSectionCollapse()
    {
        // Preserve the player's configured defaults for useful sections, but never leave
        // a detail section expanded when it only contains empty, completed, or unavailable data.
        _stashInstancesExpanded &= _stashInstances.Count > 0;
        _saleComparisonExpanded &= HasUsefulTraderInfo(_traderSummary);
        _marketExpanded &= HasUsefulMarketInfo(_marketSummary);
        _questRequirementsExpanded &= HasUsefulQuestRequirements(_hideoutUsage);
        _questKeysExpanded &= HasUsefulQuestKeyKnowledge(_hideoutUsage);
        _hideoutCraftUsesExpanded &= HasUsefulHideoutOrCraftInfo(_hideoutUsage);
    }

    private static bool HasUsefulTraderInfo(HermesTraderSummaryResponse? summary)
    {
        return summary is { Found: true }
               && (summary.BestSellOffer is not null
                   || summary.SellOffers.Count > 0
                   || summary.PurchaseOffers.Any(offer => offer.IsAvailable));
    }

    private static bool HasUsefulMarketInfo(HermesMarketSummaryResponse? market)
    {
        return market is { Found: true }
               && (market.LowestPrice.HasValue
                   || market.MedianPrice.HasValue
                   || market.SuggestedListPrice.HasValue
                   || market.EstimatedNetSale.HasValue
                   || market.ComparableOfferCount > 0);
    }

    private static bool HasUsefulQuestRequirements(HermesItemHideoutUsageResponse? usage)
    {
        return usage is { Found: true }
               && usage.QuestUses.Any(quest => !quest.ConditionCompleted && !quest.QuestCompleted);
    }

    private static bool HasUsefulQuestKeyKnowledge(HermesItemHideoutUsageResponse? usage)
    {
        return usage is { Found: true }
               && usage.QuestKeyUses.Any(key => !key.QuestCompleted);
    }

    private static bool HasUsefulHideoutOrCraftInfo(HermesItemHideoutUsageResponse? usage)
    {
        return usage is { Found: true }
               && (usage.UpgradeUses.Any(upgrade => !upgrade.IsMet && upgrade.TargetLevel > upgrade.CurrentLevel)
                   || usage.ProducedBy.Count > 0
                   || usage.UsedBy.Count > 0);
    }

    private void ResetSectionExpansionDefaults()
    {
        var expanded = !Plugin.Settings.CollapseSectionsByDefault.Value;
        _stashInstancesExpanded = expanded;
        _saleComparisonExpanded = Plugin.Settings.ExpandTraderComparisonByDefault.Value;
        _marketExpanded = Plugin.Settings.ExpandMarketByDefault.Value;
        _questRequirementsExpanded = expanded;
        _questKeysExpanded = expanded;
        _hideoutCraftUsesExpanded = expanded;
    }

    private static string FormatCount(double count)
    {
        return Math.Abs(count - Math.Round(count)) < 0.0001d
            ? Math.Round(count).ToString("N0")
            : count.ToString("0.##");
    }

    private static string FormatCurrency(long amount, string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }
}
