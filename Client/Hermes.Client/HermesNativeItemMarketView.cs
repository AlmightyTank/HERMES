using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.Globalization;
using System.Reflection;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Native Flea-style Items & Market browser. Search results reuse the same animated category
/// control used by the workspace rail. The selected item is rendered as compact market rows
/// instead of the staged IMGUI report body.
/// </summary>
internal sealed class HermesNativeItemMarketView : MonoBehaviour
{
    private const BindingFlags WindowFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private HermesWindow? _window;
    private RectTransform? _root;
    private RectTransform? _resultsContent;
    private RectTransform? _detailsContent;
    private TMP_Text? _resultsHeader;
    private TMP_Text? _resultsStatus;
    private TMP_Text? _detailsHeader;
    private TMP_Text? _detailsStatus;
    private ToggleGroup? _resultsToggleGroup;
    private MethodInfo? _selectItemMethod;

    private object? _lastResultsReference;
    private int _lastResultCount = -1;
    private string _lastSelectedKey = string.Empty;
    private object? _lastSelectedReference;
    private object? _lastTraderSummary;
    private object? _lastMarketSummary;
    private object? _lastUsageSummary;
    private object? _lastStashInstances;
    private bool _lastLoadingDetails;
    private string _lastStatus = string.Empty;
    private string _lastDetailStatus = string.Empty;
    private bool _built;
    private bool _detailsBuilt;
    private float _nextSyncAt;

    internal void Initialize(HermesWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _selectItemMethod = typeof(HermesWindow)
            .GetMethods(WindowFlags)
            .FirstOrDefault(method => method.Name == "SelectItemAsync"
                                      && method.GetParameters().Length >= 1);
        Build();
        _root?.gameObject.SetActive(false);
    }

    internal bool IsVisibleForCurrentWorkspace
        => _window != null
           && HermesNativeWorkspaceRuntime.Active
           && HermesEftWindowReflection.IsSelected(_window, "ItemSearch");

    private void Build()
    {
        if (_built)
        {
            return;
        }

        _root = HermesNativeUiFramework.CreatePanel(
            "NativeItemsMarket",
            transform,
            new Color(0f, 0f, 0f, 0.18f));
        HermesNativeUiFramework.Stretch(_root, 0f, 0f, 0f, 0f);

        var split = HermesNativeUiFramework.CreateSplitView(_root, 390f);
        HermesNativeUiFramework.Stretch(split.Root, 0f, 0f, 0f, 0f);

        BuildResultsPane(split.Left);
        BuildDetailsPane(split.Right);
        _built = true;
    }

    private void BuildResultsPane(RectTransform pane)
    {
        var layout = pane.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var header = HermesNativeUiFramework.CreateSectionHeader(pane, "SEARCH RESULTS", 38f);
        _resultsHeader = header.GetComponentInChildren<TMP_Text>(true);

        var statusRow = CreateCompactStatusRow(pane, out _resultsStatus);
        statusRow.GetComponent<LayoutElement>().preferredHeight = 30f;

        var scroll = HermesNativeUiFramework.CreateScrollView(pane, "ResultCategoryScroll");
        var scrollLayout = scroll.Root.gameObject.AddComponent<LayoutElement>();
        scrollLayout.flexibleHeight = 1f;
        scrollLayout.minHeight = 120f;
        _resultsContent = scroll.Content;
        _resultsToggleGroup = scroll.Content.gameObject.AddComponent<ToggleGroup>();
        _resultsToggleGroup.allowSwitchOff = false;
    }

    private void BuildDetailsPane(RectTransform pane)
    {
        var layout = pane.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var header = HermesNativeUiFramework.CreateSectionHeader(pane, "MARKET INTELLIGENCE", 38f);
        _detailsHeader = header.GetComponentInChildren<TMP_Text>(true);

        var columns = new GameObject(
            "ColumnHeader",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement),
            typeof(HorizontalLayoutGroup));
        columns.transform.SetParent(pane, false);
        columns.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.082f, 0.98f);
        columns.GetComponent<LayoutElement>().preferredHeight = 32f;
        var columnsLayout = columns.GetComponent<HorizontalLayoutGroup>();
        columnsLayout.padding = new RectOffset(12, 12, 4, 4);
        columnsLayout.spacing = 8f;
        columnsLayout.childAlignment = TextAnchor.MiddleLeft;
        columnsLayout.childControlWidth = true;
        columnsLayout.childControlHeight = true;
        columnsLayout.childForceExpandWidth = false;
        columnsLayout.childForceExpandHeight = true;
        CreateColumnLabel(columns.transform, "SOURCE", 160f, TextAlignmentOptions.Left);
        CreateColumnLabel(columns.transform, "INTELLIGENCE", -1f, TextAlignmentOptions.Left);
        CreateColumnLabel(columns.transform, "VALUE", 185f, TextAlignmentOptions.Right);
        CreateColumnLabel(columns.transform, "STATUS", 126f, TextAlignmentOptions.Center);

        var statusRow = CreateCompactStatusRow(pane, out _detailsStatus);
        statusRow.GetComponent<LayoutElement>().preferredHeight = 30f;

        var scroll = HermesNativeUiFramework.CreateScrollView(pane, "MarketIntelligenceScroll");
        var scrollLayout = scroll.Root.gameObject.AddComponent<LayoutElement>();
        scrollLayout.flexibleHeight = 1f;
        scrollLayout.minHeight = 120f;
        _detailsContent = scroll.Content;
    }

    private void Update()
    {
        if (!_built || _window == null || _root == null)
        {
            return;
        }

        var visible = IsVisibleForCurrentWorkspace;
        if (_root.gameObject.activeSelf != visible)
        {
            _root.gameObject.SetActive(visible);
        }

        if (!visible || Time.unscaledTime < _nextSyncAt)
        {
            return;
        }

        _nextSyncAt = Time.unscaledTime + 0.15f;
        SyncFromWindow();
    }

    private void SyncFromWindow()
    {
        if (_window == null)
        {
            return;
        }

        var resultsReference = GetWindowField("_results");
        var results = Enumerate(resultsReference).ToList();
        var selected = GetWindowField("_selectedItem");
        var selectedKey = GetString(selected, "ItemKey");
        var traderSummary = GetWindowField("_traderSummary");
        var marketSummary = GetWindowField("_marketSummary");
        var usageSummary = GetWindowField("_hideoutUsage");
        var stashInstances = GetWindowField("_stashInstances");
        var loadingDetails = GetWindowBool("_loadingDetails");
        var status = GetWindowString("_status");
        var detailStatus = GetWindowString("_detailStatus");

        if (!ReferenceEquals(resultsReference, _lastResultsReference)
            || results.Count != _lastResultCount
            || !string.Equals(selectedKey, _lastSelectedKey, StringComparison.Ordinal))
        {
            _lastResultsReference = resultsReference;
            _lastResultCount = results.Count;
            _lastSelectedKey = selectedKey;
            RebuildResults(results, selectedKey);
        }

        if (!ReferenceEquals(selected, null)
            && (_detailsHeader == null || string.IsNullOrWhiteSpace(_detailsHeader.text)))
        {
            _detailsHeader!.text = "MARKET INTELLIGENCE";
        }

        if (!_detailsBuilt
            || !ReferenceEquals(selected, _lastSelectedReference)
            || !ReferenceEquals(traderSummary, _lastTraderSummary)
            || !ReferenceEquals(marketSummary, _lastMarketSummary)
            || !ReferenceEquals(usageSummary, _lastUsageSummary)
            || !ReferenceEquals(stashInstances, _lastStashInstances)
            || loadingDetails != _lastLoadingDetails
            || !string.Equals(detailStatus, _lastDetailStatus, StringComparison.Ordinal)
            || !string.Equals(selectedKey, _lastSelectedKey, StringComparison.Ordinal))
        {
            _detailsBuilt = true;
            _lastSelectedReference = selected;
            _lastTraderSummary = traderSummary;
            _lastMarketSummary = marketSummary;
            _lastUsageSummary = usageSummary;
            _lastStashInstances = stashInstances;
            _lastLoadingDetails = loadingDetails;
            _lastDetailStatus = detailStatus;
            RebuildDetails(selected, traderSummary, marketSummary, usageSummary, stashInstances, loadingDetails);
        }

        if (!string.Equals(status, _lastStatus, StringComparison.Ordinal))
        {
            _lastStatus = status;
            if (_resultsStatus != null)
            {
                _resultsStatus.text = CompactStatus(status, results.Count == 0
                    ? "SEARCH FOR AN ITEM TO BEGIN"
                    : $"{results.Count:N0} MATCHING ITEM{(results.Count == 1 ? string.Empty : "S")}");
            }
        }

        if (_resultsHeader != null)
        {
            _resultsHeader.text = $"SEARCH RESULTS  ({results.Count:N0})";
        }

        if (_detailsStatus != null)
        {
            _detailsStatus.text = CompactStatus(
                detailStatus,
                selected == null ? "SELECT A RESULT TO INSPECT CURRENT SOURCES" : "CURRENT LOCAL PROFILE AND MARKET DATA");
        }
    }

    private void RebuildResults(IReadOnlyList<object> results, string selectedKey)
    {
        if (_resultsContent == null || _resultsToggleGroup == null)
        {
            return;
        }

        ClearChildren(_resultsContent);

        if (results.Count == 0)
        {
            CreateEmptyRow(
                _resultsContent,
                "NO RESULTS LOADED",
                "Quest-only items and entries without a positive handbook value remain excluded.");
            return;
        }

        for (var index = 0; index < results.Count; index++)
        {
            var item = results[index];
            var key = GetString(item, "ItemKey");
            var name = GetString(item, "Name", "UNKNOWN ITEM");
            var shortName = GetString(item, "ShortName");
            var reference = GetNullableLong(item, "ReferencePrice");
            var selected = !string.IsNullOrWhiteSpace(key)
                           && string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase);
            CreateResultCategoryRow(item, name, shortName, reference, selected, index);
        }
    }

    private void CreateResultCategoryRow(
        object item,
        string name,
        string shortName,
        long? referencePrice,
        bool selected,
        int rowIndex)
    {
        if (_resultsContent == null || _resultsToggleGroup == null)
        {
            return;
        }

        GameObject row;
        if (HermesRagfairNativeAssets.AnimatedToggleTemplate != null)
        {
            row = UnityEngine.Object.Instantiate(
                HermesRagfairNativeAssets.AnimatedToggleTemplate,
                _resultsContent,
                false);
            HermesNativeUiFramework.NormalizeClonedControl(row);
        }
        else
        {
            row = CreateFallbackResultToggle(_resultsContent);
        }

        row.name = $"HERMES_Result_{GetString(item, "ItemKey", rowIndex.ToString(CultureInfo.InvariantCulture))}";
        HermesNativeUiFramework.AddWorkspaceRowChrome(row, rowIndex);
        var layout = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
        layout.minHeight = 42f;
        layout.preferredHeight = 42f;
        layout.flexibleWidth = 1f;

        var toggle = row.GetComponent<AnimatedToggle>();
        if (toggle == null)
        {
            UnityEngine.Object.Destroy(row);
            return;
        }

        var spawnable = row.GetComponent<UISpawnableToggle>();
        if (spawnable != null)
        {
            spawnable.method_1(_resultsToggleGroup);
            spawnable.method_2(name, 17, null, null);
        }

        toggle.group = _resultsToggleGroup;
        toggle.interactable = true;
        toggle.onValueChanged.RemoveAllListeners();
        toggle.onValueChanged.AddListener(toggle.ToggleSilent);
        toggle.onValueChanged.AddListener(value =>
        {
            if (value)
            {
                SelectItem(item);
            }
        });

        var label = row.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(text => text != null && text.name == "Label")
            ?? row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        if (label != null)
        {
            label.text = string.IsNullOrWhiteSpace(shortName)
                         || string.Equals(name, shortName, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}  [{shortName}]";
            label.fontSize = 17f;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.rectTransform.offsetMax = new Vector2(-98f, label.rectTransform.offsetMax.y);
        }

        var reference = HermesNativeUiFramework.CreateText(
            "ReferencePrice",
            row.transform,
            14f,
            true,
            TextAlignmentOptions.Right);
        reference.text = referencePrice.HasValue ? $"₽{referencePrice.Value:N0}" : "—";
        reference.color = referencePrice.HasValue
            ? HermesNativeUiFramework.AccentTextColor
            : HermesNativeUiFramework.MutedTextColor;
        reference.rectTransform.anchorMin = new Vector2(1f, 0f);
        reference.rectTransform.anchorMax = new Vector2(1f, 1f);
        reference.rectTransform.pivot = new Vector2(1f, 0.5f);
        reference.rectTransform.anchoredPosition = new Vector2(-12f, 0f);
        reference.rectTransform.sizeDelta = new Vector2(88f, 0f);

        toggle.ToggleSilent(selected);
    }

    private void RebuildDetails(
        object? selected,
        object? traderSummary,
        object? marketSummary,
        object? usageSummary,
        object? stashInstances,
        bool loadingDetails)
    {
        if (_detailsContent == null)
        {
            return;
        }

        ClearChildren(_detailsContent);
        var rowIndex = 0;

        if (selected == null)
        {
            CreateEmptyRow(
                _detailsContent,
                "SELECT A SEARCH RESULT",
                "Trader offers, flea values, stash copies, quests, hideout areas, and crafts will appear here as market rows.");
            return;
        }

        var name = GetString(selected, "Name", "SELECTED ITEM");
        var shortName = GetString(selected, "ShortName");
        var referencePrice = GetNullableLong(selected, "ReferencePrice");
        CreateIntelligenceRow(
            rowIndex++,
            "ITEM",
            name,
            string.IsNullOrWhiteSpace(shortName) || string.Equals(shortName, name, StringComparison.OrdinalIgnoreCase)
                ? "Selected HERMES catalog entry"
                : $"Short name: {shortName}",
            referencePrice.HasValue ? $"₽{referencePrice.Value:N0}" : "NO REFERENCE",
            "SELECTED");

        var stashRows = Enumerate(stashInstances).Take(6).ToList();
        foreach (var instance in stashRows)
        {
            var label = GetString(instance, "Label", "PMC STASH COPY");
            var condition = GetInt(instance, "ConditionPercent");
            var foundInRaid = GetBool(instance, "FoundInRaid");
            var quantity = GetDouble(instance, "Quantity", 1d);
            var value = GetLong(instance, "ConditionAdjustedReferenceValue");
            CreateIntelligenceRow(
                rowIndex++,
                "STASH",
                label,
                $"Quantity {FormatNumber(quantity)} • {(condition > 0 ? $"condition {condition}%" : "condition not applicable")} • {(foundInRaid ? "FIR" : "not FIR")}",
                value > 0 ? $"₽{value:N0}" : "OWNED",
                "PMC COPY");
        }

        if (loadingDetails)
        {
            CreateIntelligenceRow(
                rowIndex++,
                "HERMES",
                "Loading current sources",
                "Checking trader assortments, local flea offers, quest progress, hideout usage, and recipes.",
                "…",
                "SYNCING");
        }

        rowIndex = AddTraderRows(traderSummary, rowIndex);
        rowIndex = AddMarketRows(marketSummary, rowIndex);
        rowIndex = AddUsageRows(usageSummary, rowIndex);

        if (rowIndex <= 1 && !loadingDetails)
        {
            CreateIntelligenceRow(
                rowIndex,
                "HERMES",
                "No current intelligence was returned",
                "Refresh the current view or inspect another item.",
                "—",
                "NO DATA");
        }
    }

    private int AddTraderRows(object? summary, int rowIndex)
    {
        if (summary == null)
        {
            return rowIndex;
        }

        var found = GetBool(summary, "Found");
        var message = GetString(summary, "Message");
        if (!found && !string.IsNullOrWhiteSpace(message))
        {
            CreateIntelligenceRow(rowIndex++, "TRADERS", "Trader intelligence unavailable", message, "—", "UNAVAILABLE");
            return rowIndex;
        }

        var best = GetMember(summary, "BestSellOffer");
        if (best != null)
        {
            CreateIntelligenceRow(
                rowIndex++,
                GetString(best, "TraderName", "TRADER"),
                "Best estimated trader sale",
                GetString(summary, "SalePriceBasis", "Current supported vanilla trader valuation"),
                FormatCurrency(GetLong(best, "Amount"), GetString(best, "Currency", "RUB")),
                "BEST SALE");
        }

        foreach (var offer in Enumerate(GetMember(summary, "SellOffers"))
                     .Where(offer => !GetBool(offer, "IsBest"))
                     .Take(7))
        {
            var trader = GetString(offer, "TraderName", "TRADER");
            var loyalty = GetInt(offer, "PlayerLoyaltyLevel");
            var ignored = GetInt(offer, "IgnoredInstalledItemCount");
            CreateIntelligenceRow(
                rowIndex++,
                trader,
                "Sell to trader",
                $"Current loyalty {loyalty} • installed items ignored {ignored}",
                FormatCurrency(GetLong(offer, "Amount"), GetString(offer, "Currency", "RUB")),
                "SELL");
        }

        foreach (var offer in Enumerate(GetMember(summary, "PurchaseOffers")).Take(8))
        {
            var trader = GetString(offer, "TraderName", "TRADER");
            var available = GetBool(offer, "IsAvailable");
            var requiredLoyalty = GetInt(offer, "RequiredLoyaltyLevel");
            var payment = Enumerate(GetMember(offer, "PaymentOptions"))
                .OrderByDescending(option => GetBool(option, "EstimateAvailable"))
                .FirstOrDefault();
            var displayPrice = payment == null
                ? "—"
                : GetString(payment, "DisplayPrice",
                    GetLong(payment, "EstimatedRoubleValue") > 0
                        ? $"₽{GetLong(payment, "EstimatedRoubleValue"):N0}"
                        : "—");
            var reason = available
                ? $"Loyalty {requiredLoyalty} • {StockText(offer)}"
                : GetString(offer, "AvailabilityReason", "Progression requirement not met");
            CreateIntelligenceRow(
                rowIndex++,
                trader,
                "Buy from trader",
                reason,
                displayPrice,
                available ? "AVAILABLE" : "LOCKED");
        }

        return rowIndex;
    }

    private int AddMarketRows(object? summary, int rowIndex)
    {
        if (summary == null)
        {
            return rowIndex;
        }

        var found = GetBool(summary, "Found");
        var median = GetNullableLong(summary, "MedianPrice") ?? GetNullableLong(summary, "LowestPrice");
        var unlocked = GetBool(summary, "FleaUnlocked");
        var canSell = GetBool(summary, "CanSellOnFlea");
        var source = GetString(summary, "MarketPriceSource", "Local SPT flea market");
        var recommendation = GetString(summary, "SellRecommendation", GetString(summary, "Message"));

        CreateIntelligenceRow(
            rowIndex++,
            "FLEA MARKET",
            found ? "Current local market value" : "Market value unavailable",
            string.IsNullOrWhiteSpace(recommendation) ? source : recommendation,
            median.HasValue ? $"₽{median.Value:N0}" : "—",
            !unlocked ? "LOCKED" : canSell ? "LISTABLE" : "READ ONLY");

        var suggested = GetNullableLong(summary, "SuggestedListPrice");
        if (suggested.HasValue)
        {
            var fee = GetNullableLong(summary, "EstimatedListingFee");
            var net = GetNullableLong(summary, "EstimatedNetSale");
            CreateIntelligenceRow(
                rowIndex++,
                "FLEA MARKET",
                "Suggested listing",
                $"Estimated fee {(fee.HasValue ? $"₽{fee.Value:N0}" : "unavailable")} • estimated net {(net.HasValue ? $"₽{net.Value:N0}" : "unavailable")}",
                $"₽{suggested.Value:N0}",
                "LIST");
        }

        foreach (var offer in Enumerate(GetMember(summary, "LowestOffers")).Take(7))
        {
            var barter = GetBool(offer, "IsBarter");
            var quantity = GetInt(offer, "Quantity", 1);
            var condition = GetInt(offer, "ConditionPercent");
            var unitPrice = GetLong(offer, "UnitPrice");
            var sourceLabel = GetString(offer, "PriceSource", barter ? "Converted barter" : "Active cash offer");
            CreateIntelligenceRow(
                rowIndex++,
                "FLEA OFFER",
                barter ? "Converted barter offer" : "Cash offer",
                $"Quantity {quantity:N0} • {(condition > 0 ? $"condition {condition}%" : "condition n/a")} • {sourceLabel}",
                unitPrice > 0 ? $"₽{unitPrice:N0}" : "—",
                barter ? "BARTER" : "ACTIVE");
        }

        return rowIndex;
    }

    private int AddUsageRows(object? usage, int rowIndex)
    {
        if (usage == null)
        {
            return rowIndex;
        }

        var found = GetBool(usage, "Found");
        if (!found)
        {
            var message = GetString(usage, "Message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                CreateIntelligenceRow(rowIndex++, "USAGE", "Quest and hideout usage unavailable", message, "—", "UNAVAILABLE");
            }
            return rowIndex;
        }

        var owned = GetDouble(usage, "OwnedQuantity");
        var fir = GetDouble(usage, "OwnedFoundInRaidQuantity");
        var questUses = Enumerate(GetMember(usage, "QuestUses")).ToList();
        var upgrades = Enumerate(GetMember(usage, "UpgradeUses")).ToList();
        var producedBy = Enumerate(GetMember(usage, "ProducedBy")).ToList();
        var usedBy = Enumerate(GetMember(usage, "UsedBy")).ToList();
        CreateIntelligenceRow(
            rowIndex++,
            "PROFILE USE",
            "Owned and required",
            $"Quests {questUses.Count:N0} • hideout upgrades {upgrades.Count:N0} • produced by {producedBy.Count:N0} • ingredient in {usedBy.Count:N0}",
            $"{FormatNumber(owned)} OWNED",
            fir > 0 ? $"{FormatNumber(fir)} FIR" : "PROFILE");

        foreach (var quest in questUses.Take(6))
        {
            var completed = GetBool(quest, "ConditionCompleted");
            var active = GetBool(quest, "IsActive");
            var required = GetDouble(quest, "Required");
            var missing = GetDouble(quest, "Missing");
            CreateIntelligenceRow(
                rowIndex++,
                GetString(quest, "TraderName", "QUEST"),
                GetString(quest, "QuestName", "Quest requirement"),
                GetString(quest, "ProgressText", GetString(quest, "QuestStatus")),
                missing > 0 ? $"{FormatNumber(missing)} MISSING" : $"{FormatNumber(required)} REQUIRED",
                completed ? "COMPLETE" : active ? "ACTIVE" : "QUEST");
        }

        foreach (var upgrade in upgrades.Take(6))
        {
            var area = GetString(upgrade, "AreaName", "HIDEOUT");
            var target = GetInt(upgrade, "TargetLevel");
            var missing = GetDouble(upgrade, "Missing");
            var met = GetBool(upgrade, "IsMet");
            var cost = GetNullableLong(upgrade, "EstimatedMissingCost");
            CreateIntelligenceRow(
                rowIndex++,
                "HIDEOUT",
                $"{area} Level {target}",
                GetString(upgrade, "Status", "Upgrade requirement"),
                cost.HasValue ? $"₽{cost.Value:N0}" : missing > 0 ? $"{FormatNumber(missing)} MISSING" : "READY",
                met ? "READY" : "REQUIRED");
        }

        foreach (var craft in producedBy.Take(5))
        {
            CreateIntelligenceRow(
                rowIndex++,
                GetString(craft, "StationName", "CRAFT"),
                $"Produces {GetString(craft, "OutputName", "selected item")}",
                $"Station L{GetInt(craft, "RequiredStationLevel")} • {GetString(craft, "Status", "recipe")}",
                FormatDuration(GetLong(craft, "DurationSeconds")),
                "PRODUCES");
        }

        foreach (var craft in usedBy.Take(5))
        {
            var count = GetDouble(craft, "ItemCount");
            CreateIntelligenceRow(
                rowIndex++,
                GetString(craft, "StationName", "CRAFT"),
                $"Ingredient for {GetString(craft, "OutputName", "recipe")}",
                $"Station L{GetInt(craft, "RequiredStationLevel")} • {GetString(craft, "Status", "recipe")}",
                $"{FormatNumber(count)} NEEDED",
                "INGREDIENT");
        }

        return rowIndex;
    }

    private void CreateIntelligenceRow(
        int rowIndex,
        string source,
        string title,
        string detail,
        string value,
        string status)
    {
        if (_detailsContent == null)
        {
            return;
        }

        var row = new GameObject(
            $"IntelligenceRow_{rowIndex}",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement),
            typeof(HorizontalLayoutGroup));
        row.transform.SetParent(_detailsContent, false);
        row.GetComponent<Image>().color = rowIndex % 2 == 0
            ? HermesNativeUiFramework.RowColor
            : HermesNativeUiFramework.RowAlternateColor;
        var rowLayout = row.GetComponent<LayoutElement>();
        rowLayout.minHeight = 70f;
        rowLayout.preferredHeight = 70f;
        var horizontal = row.GetComponent<HorizontalLayoutGroup>();
        horizontal.padding = new RectOffset(12, 12, 7, 7);
        horizontal.spacing = 8f;
        horizontal.childAlignment = TextAnchor.MiddleLeft;
        horizontal.childControlWidth = true;
        horizontal.childControlHeight = true;
        horizontal.childForceExpandWidth = false;
        horizontal.childForceExpandHeight = true;

        CreateStackedCell(row.transform, source.ToUpperInvariant(), "LOCAL SOURCE", 160f, TextAlignmentOptions.Left, true);
        CreateStackedCell(row.transform, title, detail, -1f, TextAlignmentOptions.Left, false);
        CreateStackedCell(row.transform, value, "CURRENT ESTIMATE", 185f, TextAlignmentOptions.Right, true);
        CreateStatusCell(row.transform, status, 126f);

        var separator = new GameObject("Separator", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        separator.transform.SetParent(row.transform, false);
        var separatorRect = (RectTransform)separator.transform;
        separatorRect.anchorMin = new Vector2(0f, 0f);
        separatorRect.anchorMax = new Vector2(1f, 0f);
        separatorRect.pivot = new Vector2(0.5f, 0f);
        separatorRect.offsetMin = Vector2.zero;
        separatorRect.offsetMax = new Vector2(0f, 1f);
        var separatorImage = separator.GetComponent<Image>();
        separatorImage.color = HermesNativeUiFramework.SeparatorColor;
        separatorImage.raycastTarget = false;
        separator.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
    }

    private static void CreateStackedCell(
        Transform parent,
        string primary,
        string secondary,
        float width,
        TextAlignmentOptions alignment,
        bool accentPrimary)
    {
        var cell = new GameObject(
            "Cell",
            typeof(RectTransform),
            typeof(LayoutElement),
            typeof(VerticalLayoutGroup));
        cell.transform.SetParent(parent, false);
        var cellLayout = cell.GetComponent<LayoutElement>();
        if (width > 0f)
        {
            cellLayout.minWidth = width;
            cellLayout.preferredWidth = width;
            cellLayout.flexibleWidth = 0f;
        }
        else
        {
            cellLayout.flexibleWidth = 1f;
        }
        var vertical = cell.GetComponent<VerticalLayoutGroup>();
        vertical.spacing = 1f;
        vertical.childAlignment = alignment is TextAlignmentOptions.Right or TextAlignmentOptions.TopRight
            ? TextAnchor.MiddleRight
            : TextAnchor.MiddleLeft;
        vertical.childControlWidth = true;
        vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;

        var main = HermesNativeUiFramework.CreateText("Primary", cell.transform, 16f, true, alignment);
        main.text = string.IsNullOrWhiteSpace(primary) ? "—" : primary;
        main.color = accentPrimary ? HermesNativeUiFramework.AccentTextColor : HermesNativeUiFramework.NormalTextColor;
        main.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;

        var sub = HermesNativeUiFramework.CreateText("Secondary", cell.transform, 11f, false, alignment);
        sub.text = string.IsNullOrWhiteSpace(secondary) ? " " : secondary;
        sub.color = HermesNativeUiFramework.MutedTextColor;
        sub.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;
    }

    private static void CreateStatusCell(Transform parent, string status, float width)
    {
        var cell = new GameObject(
            "Status",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement));
        cell.transform.SetParent(parent, false);
        var image = cell.GetComponent<Image>();
        image.color = StatusColor(status);
        image.raycastTarget = false;
        var layout = cell.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = 34f;
        layout.preferredHeight = 34f;

        var label = HermesNativeUiFramework.CreateText("Label", cell.transform, 13f, true, TextAlignmentOptions.Center);
        label.text = string.IsNullOrWhiteSpace(status) ? "INFO" : status.ToUpperInvariant();
        label.color = HermesNativeUiFramework.NormalTextColor;
        HermesNativeUiFramework.Stretch(label.rectTransform, 5f, 3f, 5f, 3f);
    }

    private static Color StatusColor(string status)
    {
        if (status.Contains("LOCK", StringComparison.OrdinalIgnoreCase)
            || status.Contains("MISSING", StringComparison.OrdinalIgnoreCase)
            || status.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || status.Contains("NO DATA", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.19f, 0.075f, 0.06f, 0.98f);
        }

        if (status.Contains("READY", StringComparison.OrdinalIgnoreCase)
            || status.Contains("AVAILABLE", StringComparison.OrdinalIgnoreCase)
            || status.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase)
            || status.Contains("LISTABLE", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.10f, 0.18f, 0.16f, 0.98f);
        }

        return HermesNativeUiFramework.HeaderColor;
    }

    private static RectTransform CreateCompactStatusRow(Transform parent, out TMP_Text label)
    {
        var row = new GameObject(
            "CompactStatus",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<Image>().color = new Color(0.025f, 0.035f, 0.037f, 0.98f);
        label = HermesNativeUiFramework.CreateText("Label", row.transform, 11f, false, TextAlignmentOptions.Left);
        label.color = HermesNativeUiFramework.MutedTextColor;
        HermesNativeUiFramework.Stretch(label.rectTransform, 10f, 4f, 10f, 4f);
        return (RectTransform)row.transform;
    }

    private static void CreateColumnLabel(Transform parent, string text, float width, TextAlignmentOptions alignment)
    {
        var label = HermesNativeUiFramework.CreateText(text, parent, 12f, true, alignment);
        label.text = text;
        label.color = HermesNativeUiFramework.MutedTextColor;
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (width > 0f)
        {
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0f;
        }
        else
        {
            layout.flexibleWidth = 1f;
        }
    }

    private static void CreateEmptyRow(Transform parent, string title, string detail)
    {
        var row = new GameObject(
            "EmptyRow",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement),
            typeof(VerticalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<Image>().color = HermesNativeUiFramework.RowColor;
        row.GetComponent<LayoutElement>().preferredHeight = 74f;
        var layout = row.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.spacing = 3f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var titleLabel = HermesNativeUiFramework.CreateText("Title", row.transform, 15f, true, TextAlignmentOptions.Left);
        titleLabel.text = title;
        titleLabel.color = HermesNativeUiFramework.AccentTextColor;
        titleLabel.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
        var detailLabel = HermesNativeUiFramework.CreateText("Detail", row.transform, 12f, false, TextAlignmentOptions.Left);
        detailLabel.text = detail;
        detailLabel.color = HermesNativeUiFramework.MutedTextColor;
        detailLabel.enableWordWrapping = true;
        detailLabel.overflowMode = TextOverflowModes.Ellipsis;
        detailLabel.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
    }

    private void SelectItem(object item)
    {
        if (_window == null || _selectItemMethod == null)
        {
            Plugin.Log?.LogWarning("HERMES native item row could not invoke SelectItemAsync.");
            return;
        }

        try
        {
            var parameters = _selectItemMethod.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = item;
            for (var index = 1; index < parameters.Length; index++)
            {
                arguments[index] = parameters[index].HasDefaultValue
                    ? parameters[index].DefaultValue
                    : parameters[index].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[index].ParameterType)
                        : null;
            }
            _ = _selectItemMethod.Invoke(_window, arguments);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native item selection failed: {ex}");
        }
    }

    private object? GetWindowField(string name)
    {
        if (_window == null)
        {
            return null;
        }

        return typeof(HermesWindow).GetField(name, WindowFlags)?.GetValue(_window);
    }

    private string GetWindowString(string name)
        => GetWindowField(name) as string ?? string.Empty;

    private bool GetWindowBool(string name)
        => GetWindowField(name) is true;

    private static IEnumerable<object> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable || value is string)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item != null)
            {
                yield return item;
            }
        }
    }

    private static object? GetMember(object? owner, params string[] names)
    {
        if (owner == null)
        {
            return null;
        }

        var type = owner.GetType();
        foreach (var name in names)
        {
            for (var cursor = type; cursor != null; cursor = cursor.BaseType)
            {
                var property = cursor.GetProperty(name, MemberFlags);
                if (property != null)
                {
                    try
                    {
                        return property.GetValue(owner);
                    }
                    catch
                    {
                        break;
                    }
                }

                var field = cursor.GetField(name, MemberFlags);
                if (field != null)
                {
                    try
                    {
                        return field.GetValue(owner);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        return null;
    }

    private static string GetString(object? owner, string name, string fallback = "")
    {
        var value = GetMember(owner, name);
        return value?.ToString() is { Length: > 0 } text ? text : fallback;
    }

    private static bool GetBool(object? owner, string name)
    {
        var value = GetMember(owner, name);
        return value is bool boolean && boolean;
    }

    private static int GetInt(object? owner, string name, int fallback = 0)
    {
        var value = GetMember(owner, name);
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static long GetLong(object? owner, string name, long fallback = 0L)
    {
        var value = GetMember(owner, name);
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static long? GetNullableLong(object? owner, string name)
    {
        var value = GetMember(owner, name);
        if (value == null)
        {
            return null;
        }
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static double GetDouble(object? owner, string name, double fallback = 0d)
    {
        var value = GetMember(owner, name);
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static string StockText(object offer)
    {
        if (GetBool(offer, "UnlimitedStock"))
        {
            return "unlimited stock";
        }

        var stock = GetMember(offer, "StockRemaining");
        return stock == null ? "stock not reported" : $"{GetInt(offer, "StockRemaining"):N0} in stock";
    }

    private static string FormatCurrency(long amount, string currency)
        => currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            _ => $"₽{amount:N0}"
        };

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "INSTANT";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1d
            ? $"{(int)duration.TotalHours}H {duration.Minutes}M"
            : $"{duration.Minutes}M {duration.Seconds}S";
    }

    private static string FormatNumber(double value)
        => Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);

    private static string CompactStatus(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var compact = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length <= 150 ? compact.ToUpperInvariant() : compact[..147].ToUpperInvariant() + "...";
    }

    private static void ClearChildren(Transform parent)
    {
        for (var index = parent.childCount - 1; index >= 0; index--)
        {
            var child = parent.GetChild(index).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }

    private static GameObject CreateFallbackResultToggle(Transform parent)
    {
        var row = new GameObject(
            "FallbackResultToggle",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Animator),
            typeof(AnimatedToggle));
        row.transform.SetParent(parent, false);
        var image = row.GetComponent<Image>();
        image.color = HermesNativeUiFramework.RowColor;
        var label = HermesNativeUiFramework.CreateText("Label", row.transform, 17f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(label.rectTransform, 12f, 4f, 96f, 4f);
        var toggle = row.GetComponent<AnimatedToggle>();
        toggle.targetGraphic = image;
        return row;
    }
}
