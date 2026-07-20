using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    private enum HermesTab
    {
        Assistant,
        ItemSearch,
        Actions,
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
    private string? _suppressedAssistantContextItemKey;
    private string? _suppressedAssistantContextInstanceKey;
    private int _searchRequestVersion;
    private int _openRequestVersion;
    private int _detailRequestVersion;
    private int _instanceRequestVersion;
    private bool _refreshingCurrent;
    private bool _cacheStatusRequested;
    private bool _cacheStatusLoading;
    private Task? _profileSaveTask;
    private float _nextCacheStatusRefresh;
    private float _nextProfileSaveAt;
    private HermesCacheStatusResponse? _cacheStatus;
    private string? _refreshStatus;
    private HermesActionProposal? _actionProposal;
    private HermesActionResultResponse? _actionResult;
    private HermesActionHistoryResponse? _actionHistory;
    private string _actionStatus = "Action confirmation pipeline ready. HERMES 1.2 supports harmless tests, confirmed inventory tag actions, and confirmed completed craft collection.";
    private bool _actionLoading;
    private string _tagActionMode = "apply";
    private string _tagDraftName = string.Empty;
    private string _tagDraftColor = "blue";
    private string _tagEditorInstanceKey = string.Empty;
    private string _tagEditorMode = "apply";
    private readonly HashSet<string> _selectedTagActionInstanceKeys = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingClientTagMutation(
        string Mode,
        string TagName,
        string TagColor,
        IReadOnlyList<HermesStashInstanceSummary> Instances);

    private static readonly (string Value, string Label)[] TagColorOptions =
    [
        ("red", "Red"),
        ("orange", "Orange"),
        ("yellow", "Yellow"),
        ("green", "Green"),
        ("blue", "Blue"),
        ("violet", "Violet"),
        ("grey", "Grey")
    ];

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
            var wasVisible = _visible;
            _nativeMode = true;
            _visible = true;
            if (!wasVisible)
            {
                _nextProfileSaveAt = Time.realtimeSinceStartup;
            }
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
        // Rebuild the visible native presentation immediately and refresh the active workspace
        // from current server sources without putting the navigation shell into button-refresh mode.
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        RefreshSelectedWorkspace();
    }

    internal void OpenForInventoryItem(string profileItemId)
    {
        if (string.IsNullOrWhiteSpace(profileItemId))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch, refreshOnSelect: false);
        _ = OpenForInventoryItemAsync(profileItemId, null);
    }

    internal void OpenForLoadoutItem(string profileItemId, string itemName)
    {
        if (string.IsNullOrWhiteSpace(profileItemId) && string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }

        EnsurePresentationVisible();
        SetActiveTab(HermesTab.ItemSearch, refreshOnSelect: false);
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
        SetActiveTab(HermesTab.ItemSearch, refreshOnSelect: false);

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
        SetActiveTab(HermesTab.ItemSearch, refreshOnSelect: false);
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
        SetActiveTab(HermesTab.ItemSearch, refreshOnSelect: false);
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
        TickProfileSave();
        _noticeService.Tick(_visible, _visible && _activeTab == HermesTab.Assistant);
    }

    private void TickProfileSave()
    {
        if (!_visible
            || !Plugin.Settings.SaveProfileWhileHermesOpen.Value
            || _profileSaveTask is { IsCompleted: false }
            || Time.realtimeSinceStartup < _nextProfileSaveAt)
        {
            return;
        }

        _nextProfileSaveAt = Time.realtimeSinceStartup + Plugin.Settings.GetProfileSaveWhileHermesOpenSeconds();
        _profileSaveTask = SaveActiveProfileAsync();
    }

    private async Task SaveActiveProfileAsync()
    {
        try
        {
            var response = await HermesApiClient.SaveProfileAsync();
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log?.LogDebug(response.Saved
                    ? $"HERMES active profile save completed in {response.DurationSeconds:N3}s."
                    : response.Message ?? "HERMES active profile save was not accepted.");
            }
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log?.LogWarning($"HERMES active profile save failed: {ex.Message}");
            }
        }
        finally
        {
            _profileSaveTask = null;
        }
    }

    private void EnsureEnabledTabSelected()
    {
        if (_activeTab == HermesTab.Assistant && !Plugin.Settings.EnableAssistantTab.Value)
        {
            SetActiveTab(HermesTab.ItemSearch);
        }
        else if (_activeTab == HermesTab.Actions)
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
                case HermesTab.Actions:
                    await RefreshItemSearchDataAsync();
                    _refreshStatus = "Items & Market refreshed.";
                    break;
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
            case HermesTab.Actions:
                Clear();
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

    internal HermesItemSummary? GetAssistantSelectedItem()
    {
        if (_selectedItem is null || !IsAssistantSelectedItemSuppressed())
        {
            return _selectedItem;
        }

        return null;
    }

    internal string? GetAssistantSelectedInstanceKey()
    {
        return GetAssistantSelectedItem() is null
            ? null
            : _selectedStashInstanceKey;
    }

    internal void ClearAssistantContext()
    {
        _assistantPanel.ClearContext();
        if (_selectedItem is not null)
        {
            _suppressedAssistantContextItemKey = _selectedItem.ItemKey;
            _suppressedAssistantContextInstanceKey = _selectedStashInstanceKey;
        }
        else
        {
            _suppressedAssistantContextItemKey = null;
            _suppressedAssistantContextInstanceKey = null;
        }
    }

    private bool IsAssistantSelectedItemSuppressed()
    {
        return _selectedItem is not null
               && string.Equals(
                   _suppressedAssistantContextItemKey,
                   _selectedItem.ItemKey,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   _suppressedAssistantContextInstanceKey ?? string.Empty,
                   _selectedStashInstanceKey ?? string.Empty,
                   StringComparison.OrdinalIgnoreCase);
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

    private void SetActiveTab(HermesTab tab, bool refreshOnSelect = true)
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
        if (refreshOnSelect)
        {
            RefreshSelectedWorkspace();
        }
        if (Plugin.Settings.DetailedLogging.Value)
        {
            var refreshStatus = refreshOnSelect ? "refresh started" : "refresh skipped for direct item navigation";
            Plugin.Log.LogDebug($"HERMES active tab changed to {tabName}; {refreshStatus}.");
        }
    }

    private void RefreshSelectedWorkspace()
    {
        if (_activeTab == HermesTab.ItemSearch)
        {
            _ = RefreshItemSearchDataAsync();
            return;
        }

        if (_activeTab == HermesTab.Actions)
        {
            _ = RefreshItemSearchDataAsync();
            return;
        }

        var tabName = GetTabName(_activeTab);
        var displayName = GetTabDisplayName(_activeTab);
        _ = RefreshSelectedProfileWorkspaceAsync(tabName, displayName);
    }

    private async Task RefreshSelectedProfileWorkspaceAsync(string tabName, string displayName)
    {
        var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
        if (coordinator is null)
        {
            _refreshStatus = "HERMES workspace coordinator is unavailable.";
            return;
        }

        try
        {
            _refreshStatus = $"Refreshing {displayName} from current PMC sources...";
            await coordinator.RefreshWorkspaceAsync(tabName, manual: true);
            _refreshStatus = $"{displayName} refreshed from current PMC sources.";
        }
        catch (Exception ex)
        {
            _refreshStatus = HermesApiClient.DescribeFailure(ex, $"{displayName} refresh");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    private static HermesTab ParseTabName(string value)
    {
        var tab = value.Trim().ToLowerInvariant() switch
        {
            "assistant" or "chat" => HermesTab.Assistant,
            "actions" or "action" or "confirmed actions" => HermesTab.ItemSearch,
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
            HermesTab.Actions => "ItemSearch",
            HermesTab.Hideout => "Hideout",
            HermesTab.Crafts => "Crafts",
            HermesTab.Stash => "Stash",
            HermesTab.Loadout => "Loadout",
            HermesTab.RaidPlanner => "Raid Planner",
            _ => "Item Search"
        };
    }
}
