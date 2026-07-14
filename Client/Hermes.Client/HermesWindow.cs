using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesWindow
{
    private enum HermesTab
    {
        ItemSearch,
        Hideout,
        Crafts,
        Stash,
        Loadout
    }

    private const int WindowId = 0x4845524D;

    private Rect _windowRect = new(70f, 70f, 1120f, 760f);
    private Vector2 _resultScroll;
    private Vector2 _detailScroll;
    private string _query = string.Empty;
    private string _status = "Search for an item or ask where it can be bought or sold.";
    private string _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";
    private bool _visible;
    private bool _searching;
    private bool _loadingDetails;
    private bool _saleComparisonExpanded;
    private bool _marketExpanded;
    private bool _hideoutUsageExpanded;
    private bool _stashInstancesExpanded = true;
    private bool _loadingInstancePrice;
    private HermesTab _activeTab;
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

    public void Toggle()
    {
        _visible = !_visible;
    }

    internal void OpenForInventoryItem(string profileItemId)
    {
        if (string.IsNullOrWhiteSpace(profileItemId))
        {
            return;
        }

        _visible = true;
        _activeTab = HermesTab.ItemSearch;
        _ = OpenForInventoryItemAsync(profileItemId);
    }

    internal void OpenForStashItem(string profileItemId)
    {
        OpenForInventoryItem(profileItemId);
    }

    internal void OpenForPreviewItem(string templateId, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        _visible = true;
        _activeTab = HermesTab.ItemSearch;
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
            await SelectItemAsync(response.Item, null, false);
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

    private async Task OpenForInventoryItemAsync(string profileItemId)
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
                _status = response.Message ?? "HERMES could not analyze the selected inventory item.";
                _detailStatus = _status;
                return;
            }

            _query = response.Item.Name;
            _results = [response.Item];
            _status = $"Selected exact PMC copy: {response.Item.Name} — {response.InventoryLocation}.";
            await SelectItemAsync(response.Item, response.Instance);
        }
        catch (Exception ex)
        {
            if (requestVersion == _openRequestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Exact PMC inventory-item analysis");
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

    public void Draw()
    {
        if (!_visible)
        {
            return;
        }

        if (!_cacheStatusLoading
            && (!_cacheStatusRequested || Time.realtimeSinceStartup >= _nextCacheStatusRefresh))
        {
            _cacheStatusRequested = true;
            _cacheStatusLoading = true;
            _ = LoadCacheStatusAsync();
        }

        _windowRect = GUI.Window(
            WindowId,
            _windowRect,
            DrawWindow,
            "HERMES 0.1.0-alpha11.3.1 — Loadout Value & Insurance");
    }

    private void DrawWindow(int windowId)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("READ-ONLY PERSONAL OPERATIONS ASSISTANT");
        GUILayout.Label("Right-click stash or equipped-character items and choose Ask HERMES. The Loadout Value view also has Ask HERMES buttons. Owned items use the exact PMC instance; trader and flea previews use the base item.");
        GUILayout.Space(6f);
        DrawTabs();
        GUILayout.Space(6f);

        switch (_activeTab)
        {
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
            default:
                DrawItemSearchTab();
                break;
        }

        GUILayout.Space(6f);
        DrawFooter();

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 24f));
    }

    private void DrawTabs()
    {
        GUILayout.BeginHorizontal();
        DrawTabButton(HermesTab.ItemSearch, "Item Search");
        DrawTabButton(HermesTab.Hideout, "Hideout");
        DrawTabButton(HermesTab.Crafts, "Crafts");
        DrawTabButton(HermesTab.Stash, "Stash");
        DrawTabButton(HermesTab.Loadout, "Loadout");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawTabButton(HermesTab tab, string label)
    {
        var selected = _activeTab == tab;
        if (GUILayout.Button((selected ? "● " : string.Empty) + label, GUILayout.Width(145f), GUILayout.Height(30f)))
        {
            _activeTab = tab;
        }
    }

    private void DrawItemSearchTab()
    {
        DrawSearchBar();

        GUILayout.Space(4f);
        GUILayout.Label(_status);
        GUILayout.Space(6f);

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
        _query = GUILayout.TextField(_query, GUILayout.ExpandWidth(true), GUILayout.Height(30f));

        GUI.enabled = !_searching && !string.IsNullOrWhiteSpace(_query);
        if (GUILayout.Button(_searching ? "Searching..." : "Search", GUILayout.Width(120f), GUILayout.Height(30f)))
        {
            _ = RunSearchAsync();
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (Event.current.type == EventType.KeyDown
            && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
            && !_searching
            && !string.IsNullOrWhiteSpace(_query))
        {
            Event.current.Use();
            _ = RunSearchAsync();
        }
    }

    private void DrawResultPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(330f), GUILayout.ExpandHeight(true));
        GUILayout.Label("SEARCH RESULTS");
        GUILayout.Space(4f);

        _resultScroll = GUILayout.BeginScrollView(_resultScroll, GUILayout.ExpandHeight(true));

        if (_results.Count == 0)
        {
            GUILayout.Label("No results loaded.");
        }
        else
        {
            foreach (var item in _results)
            {
                DrawResultButton(item);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawResultButton(HermesItemSummary item)
    {
        var selected = _selectedItem?.ItemKey == item.ItemKey;
        var displayName = string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase)
            ? item.Name
            : $"{item.Name}\n{item.ShortName}";

        var prefix = selected ? "▶ " : string.Empty;
        if (GUILayout.Button(prefix + displayName, GUILayout.MinHeight(48f), GUILayout.ExpandWidth(true)))
        {
            _ = SelectItemAsync(item);
        }

        GUILayout.Space(3f);
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
            GUILayout.Label("Select a search result to compare vanilla traders and the local SPT flea market.");
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

                if (_traderSummary is not null)
                {
                    DrawTraderPurchaseSection(_traderSummary);
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

    private static void DrawSelectedItemOverview(HermesItemSummary item)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(item.Name);

        if (!string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.Label($"Short name: {item.ShortName}");
        }

        GUILayout.Label(item.ReferencePrice.HasValue
            ? $"Handbook reference: ₽{item.ReferencePrice.Value:N0}"
            : "Handbook reference: unavailable");
        GUILayout.Label("Current quest, hideout, and crafting progress is shown below from the active PMC profile.");
        GUILayout.EndVertical();
    }

    private void DrawStashInstanceSection()
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _stashInstancesExpanded ? "▼" : "▶";
        var selectedLabel = _selectedStashInstanceKey is null
            ? "Base item estimate"
            : _stashInstances.FirstOrDefault(instance => instance.InstanceKey == _selectedStashInstanceKey)?.Label
              ?? "Selected PMC inventory copy";

        if (GUILayout.Button(
                $"{arrow}  PMC INVENTORY COPY FOR TRADER SALE — {selectedLabel}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _stashInstancesExpanded = !_stashInstancesExpanded;
        }

        if (_loadingInstancePrice)
        {
            GUILayout.Label("Recalculating trader prices for the selected PMC inventory copy...");
        }

        if (_stashInstancesExpanded)
        {
            GUILayout.Space(4f);
            GUILayout.Label("Select the exact PMC inventory copy HERMES should value.");

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
                    ? $" • root ₽{instance.RootConditionAdjustedReferenceValue:N0} + installed ₽{instance.InstalledComponentReferenceValue:N0}"
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
                    ? "Loading matching stash copies..."
                    : "No matching copy is currently stored in the PMC stash. The base-item estimate is being used.");
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

        if (!string.IsNullOrWhiteSpace(summary.Message))
        {
            GUILayout.Label(summary.Message);
        }

        if (!string.IsNullOrWhiteSpace(summary.SalePriceBasis))
        {
            GUILayout.Label(summary.SalePriceBasis);
            GUILayout.Space(4f);
        }

        var arrow = _saleComparisonExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  BEST ESTIMATED TRADER SALE PRICE",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _saleComparisonExpanded = !_saleComparisonExpanded;
        }

        DrawBestSale(summary);

        if (_saleComparisonExpanded)
        {
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
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestSale(HermesTraderSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(summary.UsesSelectedStashInstance
            ? "BEST ESTIMATED SALE FOR SELECTED PMC INVENTORY COPY"
            : "BEST ESTIMATED SALE FOR BASE ITEM");

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

            if (summary.UsesSelectedStashInstance)
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

        GUILayout.EndVertical();
    }

    private void DrawMarketSection(HermesMarketSummaryResponse summary)
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _marketExpanded ? "▼" : "▶";
        var headline = summary.MedianPrice.HasValue
            ? summary.MarketPriceFromActiveOffers
                ? $"Adjusted median ₽{summary.MedianPrice.Value:N0} • {summary.ValidCashOfferCount:N0} cash + {summary.ConvertedBarterOfferCount:N0} barter"
                : $"Market reference ₽{summary.MedianPrice.Value:N0} • {summary.MarketPriceSource}"
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

        GUILayout.Label(summary.SellRecommendation);

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
        GUILayout.Label($"Converted barter offers found: {summary.ConvertedBarterOfferCount:N0}");
        if (summary.BarterOffersUsingHandbookFallback > 0)
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
            GUILayout.Label($"Suggested base-item listing price: ₽{summary.SuggestedListPrice.Value:N0}");
            GUILayout.Label(summary.EstimatedListingFee.HasValue
                ? $"Estimated listing fee: ₽{summary.EstimatedListingFee.Value:N0}"
                : "Estimated listing fee: unavailable");
            GUILayout.Label(summary.EstimatedNetSale.HasValue
                ? $"Estimated base-item net sale: ₽{summary.EstimatedNetSale.Value:N0}"
                : "Estimated base-item net sale: unavailable");
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
            foreach (var offer in summary.LowestOffers)
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

                if (offer.InstalledComponentValue > 0
                    || offer.WeaponAttachmentCount > 0
                    || offer.ArmorInsertCount > 0)
                {
                    GUILayout.Label(
                        $"Installed value: ₽{offer.InstalledComponentValue:N0} • "
                        + $"Weapon attachments: {offer.WeaponAttachmentCount:N0} • "
                        + $"Armor inserts: {offer.ArmorInsertCount:N0}");
                }
                else
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

                        if (payment.Requirements.Count > 0)
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
            GUILayout.Label(usage.Message ?? "Quest, hideout, and crafting usage is unavailable.");
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _hideoutUsageExpanded ? "▼" : "▶";
        var totalUses = usage.QuestUses.Count + usage.UpgradeUses.Count + usage.ProducedBy.Count + usage.UsedBy.Count;
        if (GUILayout.Button(
                $"{arrow}  QUEST, HIDEOUT AND CRAFTING USAGE — {totalUses:N0}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _hideoutUsageExpanded = !_hideoutUsageExpanded;
        }

        if (_hideoutUsageExpanded)
        {
            GUILayout.Space(6f);
            GUILayout.Label($"Owned in PMC inventory: {usage.OwnedQuantity:N0} total • {usage.OwnedFoundInRaidQuantity:N0} FIR");

            GUILayout.Space(8f);
            GUILayout.Label("QUEST REQUIREMENTS");
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

            GUILayout.Space(8f);
            GUILayout.Label("HIDEOUT UPGRADES");
            if (usage.UpgradeUses.Count == 0)
            {
                GUILayout.Label("Not required by a player-facing hideout upgrade.");
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
                GUILayout.Label("No player-facing hideout recipe produces this item.");
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
                GUILayout.Label("Not used by a player-facing hideout recipe.");
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

    private void DrawFooter()
    {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Clear", GUILayout.Width(90f)))
        {
            ClearCurrentTab();
        }

        GUI.enabled = !_refreshingCurrent;
        if (GUILayout.Button(
                _refreshingCurrent ? "Refreshing..." : "Refresh current data",
                GUILayout.Width(155f)))
        {
            _ = RefreshCurrentDataAsync();
        }

        GUI.enabled = true;
        GUILayout.Space(8f);
        GUILayout.Label(FormatCacheStatus(), GUILayout.ExpandWidth(true));
        GUILayout.FlexibleSpace();
        GUILayout.Label("F8 toggles HERMES");
        GUILayout.Space(12f);

        if (GUILayout.Button("Close", GUILayout.Width(90f)))
        {
            _visible = false;
        }

        GUILayout.EndHorizontal();

        if (!string.IsNullOrWhiteSpace(_refreshStatus))
        {
            GUILayout.Label(_refreshStatus);
        }
    }

    private string FormatCacheStatus()
    {
        if (_cacheStatus is null || !_cacheStatus.Found)
        {
            return "Market cache: status unavailable";
        }

        var totalEntries = _cacheStatus.MarketUnitValueEntryCount + _cacheStatus.MarketSummaryEntryCount;
        return $"Market cache: {totalEntries:N0} entries • {_cacheStatus.CacheHits:N0} hits / {_cacheStatus.CacheMisses:N0} misses • {_cacheStatus.TtlSeconds}s TTL";
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
            _nextCacheStatusRefresh = Time.realtimeSinceStartup + 10f;
            _cacheStatusLoading = false;
        }
    }

    private async Task RefreshCurrentDataAsync()
    {
        if (_refreshingCurrent)
        {
            return;
        }

        _refreshingCurrent = true;
        _refreshStatus = "Clearing short-lived market caches and reloading the current view...";
        try
        {
            var cleared = await HermesApiClient.ClearCachesAsync();
            _cacheStatus = cleared.Status;
            _refreshStatus = cleared.Message;

            switch (_activeTab)
            {
                case HermesTab.Hideout:
                    await _hideoutPanel.RefreshFromServerAsync(false, true);
                    break;
                case HermesTab.Crafts:
                    await _craftPanel.RefreshFromServerAsync(false, true);
                    break;
                case HermesTab.Stash:
                    await _stashPanel.RefreshFromServerAsync(false, true);
                    break;
                case HermesTab.Loadout:
                    await _loadoutPanel.RefreshFromServerAsync(true);
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

            await LoadCacheStatusAsync();
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
        if (query.Length == 0 || _searching)
        {
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
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _hideoutUsageExpanded = false;
        _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";

        try
        {
            var response = await HermesApiClient.SearchAsync(query);
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
        _stashInstancesExpanded = true;
        _loadingInstancePrice = false;
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _hideoutUsageExpanded = false;
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
            var marketTask = HermesApiClient.GetMarketSummaryAsync(item.ItemKey);
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
                _detailStatus = !selectFirstMatchingStashInstance
                    ? _stashInstances.Count > 0
                        ? "Preview analysis uses the full-condition base item. Matching stash copies are available in the selector below."
                        : "Preview analysis uses the full-condition base item. No matching stash copy is currently owned."
                    : _stashInstances.Count > 0
                        ? "Current profile loaded. Trader sale prices use the selected PMC inventory copy; flea data remains a local market comparison."
                        : "Current profile loaded. No matching stash copy was found, so trader sale prices use the base-item estimate.";
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
            : "Calculating trader sale prices for the selected PMC inventory copy...";

        try
        {
            var response = await HermesApiClient.GetTraderSummaryAsync(item.ItemKey, instanceKey);
            if (requestVersion != _instanceRequestVersion
                || _selectedItem?.ItemKey != item.ItemKey
                || !string.Equals(_selectedStashInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _traderSummary = response;
            _detailStatus = response.UsesSelectedStashInstance
                ? "Trader sale prices now use the selected PMC inventory copy."
                : "Trader sale prices now use the full-condition base-item estimate.";
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
            if (requestVersion == _instanceRequestVersion
                && _selectedItem?.ItemKey == item.ItemKey)
            {
                _detailStatus = HermesApiClient.DescribeFailure(ex, "Selected PMC inventory-copy pricing");
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
        _stashInstancesExpanded = true;
        _loadingInstancePrice = false;
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _hideoutUsageExpanded = false;
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _status = "Search for an item or ask where it can be bought or sold.";
        _detailStatus = "Select an item to inspect trader, flea, hideout, and crafting information.";
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
