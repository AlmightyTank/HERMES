using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Fully native uGUI renderer for every HERMES workspace. No workspace content is drawn
/// through IMGUI, so EFT messenger, confirmation dialogs, notifications, and modal windows
/// share one predictable Canvas ordering path with the HERMES workspace rail.
/// </summary>
internal sealed class HermesNativeWorkspaceBody : MonoBehaviour
{
    private const float SyncInterval = 0.20f;
    private const int MaximumRowsPerSection = 250;
    private const float CompactToolbarHeight = 36f;
    private const float CompactControlHeight = 32f;
    private const float CompactMetricHeight = 46f;
    private const float CompactSectionHeight = 30f;

    private readonly Dictionary<string, float> _savedScrollPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScrollRect> _activeScrolls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedRaidMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scrollsToForceTop = new(StringComparer.Ordinal);

    private HermesNativeWorkspaceState? _state;
    private RectTransform? _root;
    private GameObject? _contentRoot;
    private string _lastFingerprint = string.Empty;
    private float _nextSyncAt;
    private bool _forceRebuild;

    private string _assistantDraft = string.Empty;
    private string _hideoutSearch = string.Empty;
    private string _hideoutFilter = "ALL";
    private string _craftSearch = string.Empty;
    private string _craftFilter = "ALL";
    private string _stashSearch = string.Empty;
    private string _stashView = "OVERVIEW";
    private string _loadoutView = "OVERVIEW";
    private string _raidSearch = string.Empty;
    private string _lastItemResultSetKey = string.Empty;
    private string _lastSelectedItemKey = string.Empty;

    internal void Initialize(HermesWindow window)
    {
        _state = new HermesNativeWorkspaceState(window);
        _root = transform as RectTransform
                ?? throw new InvalidOperationException("HERMES native body requires a RectTransform.");

        var image = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
        image.color = HermesNativeUiFramework.PanelColor;
        image.raycastTarget = true;

        _forceRebuild = true;
        Rebuild();
    }

    private void OnEnable()
    {
        _forceRebuild = true;
        _nextSyncAt = 0f;
    }

    private void Update()
    {
        if (_state is null || !HermesNativeWorkspaceRuntime.Active || Time.unscaledTime < _nextSyncAt)
        {
            return;
        }

        _nextSyncAt = Time.unscaledTime + SyncInterval;
        var fingerprint = _state.BuildFingerprint();
        if (_forceRebuild || !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            _lastFingerprint = fingerprint;
            _forceRebuild = false;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (_state is null || _root is null)
        {
            return;
        }

        var activeTab = _state.ActiveTab;
        var itemResultSetKey = activeTab == "ItemSearch"
            ? BuildItemResultSetKey()
            : _lastItemResultSetKey;
        var selectedItemKey = activeTab == "ItemSearch"
            ? _state.SelectedItem?.ItemKey ?? string.Empty
            : _lastSelectedItemKey;
        var resetItemResultsToTop = activeTab == "ItemSearch"
                                    && !string.Equals(itemResultSetKey, _lastItemResultSetKey, StringComparison.Ordinal);
        var resetItemDetailsToTop = activeTab == "ItemSearch"
                                    && (resetItemResultsToTop
                                        || !string.Equals(selectedItemKey, _lastSelectedItemKey, StringComparison.OrdinalIgnoreCase));

        CaptureScrollPositions();
        if (resetItemResultsToTop)
        {
            _savedScrollPositions["item-results"] = 1f;
            _scrollsToForceTop.Add("item-results");
        }
        if (resetItemDetailsToTop)
        {
            _savedScrollPositions["item-details"] = 1f;
            _scrollsToForceTop.Add("item-details");
        }
        _lastItemResultSetKey = itemResultSetKey;
        _lastSelectedItemKey = selectedItemKey;

        if (_contentRoot != null)
        {
            _contentRoot.SetActive(false);
            Destroy(_contentRoot);
        }

        _activeScrolls.Clear();
        _contentRoot = new GameObject("NativeWorkspaceContent", typeof(RectTransform));
        _contentRoot.transform.SetParent(_root, false);
        var contentRect = (RectTransform)_contentRoot.transform;
        HermesNativeUiFramework.Stretch(contentRect, 6f, 6f, 6f, 6f);

        try
        {
            switch (_state.ActiveTab)
            {
                case "Assistant":
                    RenderAssistant(contentRect);
                    break;
                case "Hideout":
                    RenderHideout(contentRect);
                    break;
                case "Crafts":
                    RenderCrafts(contentRect);
                    break;
                case "Stash":
                    RenderStash(contentRect);
                    break;
                case "Loadout":
                    RenderLoadout(contentRect, false);
                    break;
                case "RaidPlanner":
                    RenderLoadout(contentRect, true);
                    break;
                default:
                    RenderItemSearch(contentRect);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native workspace body failed to build: {ex}");
            var fallback = CreateVerticalRoot(contentRect);
            AddSectionHeader(fallback, "NATIVE WORKSPACE ERROR");
            AddCard(fallback, "Rendering failed", ex.Message, "ERROR");
        }

        StartCoroutine(RestoreScrollPositionsNextFrame());
    }

    #region Assistant

    private void RenderAssistant(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.AssistantStatus, _state.AssistantLoading, _state.RefreshActive);

        var activeNotices = _state.Notices.Where(notice => !notice.Dismissed).Take(4).ToList();
        var noticePanel = CreatePanel(root, "AssistantNotices", HermesNativeUiFramework.PanelColor);
        var noticePanelElement = noticePanel.gameObject.AddComponent<LayoutElement>();
        noticePanelElement.minHeight = activeNotices.Count == 0 ? 78f : 136f;
        noticePanelElement.preferredHeight = activeNotices.Count == 0
            ? 78f
            : Mathf.Min(208f, 94f + activeNotices.Count * 54f);
        noticePanelElement.flexibleHeight = 0f;
        AddVerticalLayout(noticePanel, 5, 5, 4, 4, 3f);

        var noticeHeader = CreateToolbar(noticePanel);
        AddToolbarLabel(noticeHeader, _state.Notices.Count == 0 ? "ALERTS" : $"ALERTS  {_state.Notices.Count:N0}");
        AddFlexibleSpace(noticeHeader);
        AddButton(noticeHeader, _state.NoticesLoading ? "CHECKING" : "CHECK", _state.RefreshNotices, 82f, !_state.NoticesLoading);
        AddButton(noticeHeader, "CLEAR", _state.ClearNotices, 64f, _state.Notices.Count > 0);

        var noticeScroll = CreateScroll(noticePanel, "assistant-notices", true);
        noticeScroll.Root.GetComponent<LayoutElement>().minHeight = 34f;

        var noticePreview = _state.NoticesLoading
            ? "Checking the active PMC profile..."
            : _state.NoticeStatus;
        if (!string.IsNullOrWhiteSpace(noticePreview))
        {
            AddText(
                noticeScroll.Content,
                noticePreview,
                12.5f,
                false,
                HermesNativeUiFramework.MutedTextColor);
        }

        if (activeNotices.Count > 0)
        {
            foreach (var notice in activeNotices)
            {
                var row = AddCard(
                    noticeScroll.Content,
                    notice.Title,
                    notice.Message,
                    $"{notice.Severity.ToUpperInvariant()} • {notice.Category.ToUpperInvariant()}",
                    () => _state.OpenNotice(notice));
                var actions = CreateToolbar(row);
                AddFlexibleSpace(actions);
                AddButton(actions, "OPEN", () => _state.OpenNotice(notice), 62f);
                AddButton(actions, "DISMISS", () =>
                {
                    _state.DismissNotice(notice);
                    Invalidate();
                }, 76f);
            }
        }

        var conversation = CreateScroll(root, "assistant-conversation", true);
        AddSectionHeader(conversation.Content, "CONVERSATION");
        foreach (var message in _state.AssistantMessages)
        {
            var card = AddCard(
                conversation.Content,
                message.IsUser ? "YOU" : "HERMES",
                message.Text,
                message.Source,
                null,
                message.IsUser ? new Color(0.08f, 0.10f, 0.10f, 0.78f) : HermesNativeUiFramework.RowColor);
            if (message.Actions.Count > 0)
            {
                var actions = CreateToolbar(card);
                foreach (var action in message.Actions)
                {
                    var label = action.Label.ToUpperInvariant();
                    AddButton(
                        actions,
                        label,
                        () => _state.Navigate(action.TabName),
                        AssistantActionButtonWidth(label));
                }
                AddFlexibleSpace(actions);
            }
        }

        if (_state.AssistantMessages.Count == 0)
        {
            AddEmptyState(conversation.Content, "No conversation yet.", "Ask HERMES about the active profile, stash, loadout, hideout, crafts, or selected item.");
        }

        var composer = CreatePanel(root, "AssistantComposer", HermesNativeUiFramework.HeaderColor);
        var composerLayout = composer.gameObject.AddComponent<HorizontalLayoutGroup>();
        composerLayout.padding = new RectOffset(7, 7, 5, 5);
        composerLayout.spacing = 6f;
        composerLayout.childControlWidth = true;
        composerLayout.childControlHeight = true;
        composerLayout.childForceExpandWidth = false;
        composerLayout.childForceExpandHeight = false;
        var composerElement = composer.gameObject.AddComponent<LayoutElement>();
        composerElement.minHeight = 44f;
        composerElement.preferredHeight = 44f;
        composerElement.flexibleHeight = 0f;

        var input = AddInput(composer, "ASK HERMES ABOUT THE CURRENT PROFILE", _assistantDraft, 120f);
        input.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        input.onValueChanged.AddListener(value => _assistantDraft = value);
        input.onSubmit.AddListener(_ => SubmitAssistant(input));
        AddButton(composer, _state.AssistantLoading ? "WORKING" : "ASK", () => SubmitAssistant(input), 72f, !_state.AssistantLoading);
        AddButton(composer, "CLEAR CHAT", () =>
        {
            _state.ClearAssistant();
            _assistantDraft = string.Empty;
            Invalidate();
        }, 100f);
    }

    private void SubmitAssistant(TMP_InputField input)
    {
        var prompt = input.text.Trim();
        if (prompt.Length == 0 || _state is null)
        {
            return;
        }

        _assistantDraft = string.Empty;
        input.SetTextWithoutNotify(string.Empty);
        _state.SubmitAssistant(prompt);
        Invalidate(0.25f);
    }

    #endregion

    #region Items and market

    private void RenderItemSearch(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        var combinedStatus = _state!.DetailLoading
            ? _state.DetailStatus
            : _state.SearchLoading
                ? _state.SearchStatus
                : !string.IsNullOrWhiteSpace(_state.DetailStatus)
                    ? _state.DetailStatus
                    : _state.SearchStatus;
        AddStatusStrip(root, combinedStatus, _state.SearchLoading || _state.DetailLoading, _state.RefreshActive);

        var split = HermesNativeUiFramework.CreateSplitView(root, 350f);
        var splitElement = split.Root.gameObject.AddComponent<LayoutElement>();
        splitElement.minHeight = 180f;
        splitElement.flexibleHeight = 1f;
        splitElement.flexibleWidth = 1f;

        AddVerticalLayout(split.Left, 6, 6, 6, 6, 3f);
        var results = CreateScroll(split.Left, "item-results", true);
        AddSectionHeader(results.Content, $"SEARCH RESULTS  {_state.SearchResults.Count:N0}");
        if (_state.SearchResults.Count == 0)
        {
            AddEmptyState(results.Content, _state.SearchLoading ? "Searching item templates..." : "No item results.", "Use the native search field above this panel.");
        }
        else
        {
            foreach (var item in _state.SearchResults.Take(MaximumRowsPerSection))
            {
                var selected = string.Equals(_state.SelectedItem?.ItemKey, item.ItemKey, StringComparison.OrdinalIgnoreCase);
                AddCard(
                    results.Content,
                    item.Name,
                    $"{item.ShortName} • Reference {Money(item.ReferencePrice)}",
                    string.Join(" • ", new[]
                    {
                        item.AppearsInTraderData ? "TRADER" : null,
                        item.AppearsInHideoutData ? "HIDEOUT" : null,
                        item.AppearsInQuestData ? "QUEST" : null
                    }.Where(value => value is not null)),
                    () =>
                    {
                        _state.SelectItem(item);
                        Invalidate(0.20f);
                    },
                    selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            }
        }

        AddVerticalLayout(split.Right, 6, 6, 6, 6, 3f);
        var details = CreateScroll(split.Right, "item-details", true);
        RenderSelectedItemDetails(details.Content);
    }

    private void RenderSelectedItemDetails(Transform parent)
    {
        var item = _state!.SelectedItem;
        if (item is null)
        {
            AddEmptyState(parent, "Select an item.", "Trader, flea, stash-instance, quest, hideout, and crafting intelligence will appear here.");
            return;
        }

        AddSectionHeader(parent, item.Name);
        AddMetricGrid(parent,
            ("SHORT NAME", item.ShortName),
            ("REFERENCE", Money(item.ReferencePrice)),
            ("STASH COPIES", _state.StashInstances.Count.ToString("N0")),
            ("PROFILE", _state.StashInstances.Count > 0 ? "OWNED" : "NOT OWNED"));

        AddSectionHeader(parent, "STASH INSTANCE PRICING");
        AddCard(parent, "BASE ITEM", "Full-condition base-item estimate.", _state.SelectedStashInstanceKey is null ? "SELECTED" : "PREVIEW", () =>
        {
            _state.SelectStashInstance(null);
            Invalidate(0.20f);
        }, _state.SelectedStashInstanceKey is null ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        foreach (var instance in _state.StashInstances.Take(30))
        {
            var selected = string.Equals(_state.SelectedStashInstanceKey, instance.InstanceKey, StringComparison.OrdinalIgnoreCase);
            AddCard(
                parent,
                instance.Label,
                $"{FormatCount(instance.Quantity)} unit(s) • {instance.ConditionPercent}% {instance.ConditionDescription} • {instance.ChildItemCount} installed/contained",
                $"REFERENCE {Money(instance.ConditionAdjustedReferenceValue)}{(instance.FoundInRaid ? " • FIR" : string.Empty)}",
                () =>
                {
                    _state.SelectStashInstance(instance.InstanceKey);
                    Invalidate(0.20f);
                },
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }

        RenderTraderSummary(parent, _state.TraderSummary);
        RenderMarketSummary(parent, _state.MarketSummary);
        RenderItemUsage(parent, _state.ItemUsage);
    }

    private void RenderTraderSummary(Transform parent, HermesTraderSummaryResponse? summary)
    {
        AddSectionHeader(parent, "TRADERS");
        if (summary is null)
        {
            AddEmptyState(parent, _state!.DetailLoading ? "Loading trader data..." : "Trader data unavailable.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(parent, "No trader analysis.", summary.Message ?? string.Empty);
            return;
        }

        var best = summary.BestSellOffer;
        AddMetricGrid(parent,
            ("BEST SALE", best is null ? "NO BUYER" : $"{best.TraderName} • {Money(best.RoubleEquivalent)}"),
            ("REFERENCE", Money(summary.ReferencePrice)),
            ("SALE BASIS", summary.SalePriceBasis),
            ("PURCHASE OFFERS", summary.PurchaseOffers.Count.ToString("N0")));

        foreach (var offer in summary.SellOffers.Take(12))
        {
            AddCard(parent,
                offer.TraderName,
                $"LL{offer.PlayerLoyaltyLevel} • {FormatCurrency(offer.Amount, offer.Currency)} • ₽{offer.RoubleEquivalent:N0} equivalent",
                offer.IsBest ? "BEST TRADER SALE" : "TRADER SALE");
        }

        foreach (var offer in summary.PurchaseOffers.Take(20))
        {
            var payment = offer.PaymentOptions.OrderBy(option => option.EstimatedRoubleValue).FirstOrDefault();
            AddCard(parent,
                offer.TraderName,
                payment is null
                    ? offer.AvailabilityReason
                    : $"{payment.DisplayPrice} • estimated ₽{payment.EstimatedRoubleValue:N0} • {offer.AvailabilityReason}",
                offer.IsAvailable ? $"AVAILABLE • LL{offer.RequiredLoyaltyLevel}" : $"LOCKED • LL{offer.RequiredLoyaltyLevel}");
        }
    }

    private void RenderMarketSummary(Transform parent, HermesMarketSummaryResponse? market)
    {
        AddSectionHeader(parent, "FLEA MARKET");
        if (market is null)
        {
            AddEmptyState(parent, _state!.DetailLoading ? "Loading local flea data..." : "Market data unavailable.", string.Empty);
            return;
        }
        if (!market.Found)
        {
            AddEmptyState(parent, "No market analysis.", market.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(parent,
            ("LOWEST", Money(market.LowestPrice)),
            ("MEDIAN", Money(market.MedianPrice)),
            ("SUGGESTED", Money(market.SuggestedListPrice)),
            ("NET SALE", Money(market.EstimatedNetSale)),
            ("COMPARABLE", market.ComparableOfferCount.ToString("N0")),
            ("SOURCE", market.MarketPriceSource));
        AddCard(parent, "BUY RECOMMENDATION", market.BuyRecommendation, market.FleaUnlocked ? "FLEA UNLOCKED" : $"UNLOCKS AT LEVEL {market.RequiredPlayerLevel}");
        AddCard(parent, "SELL RECOMMENDATION", market.SellRecommendation, market.CanSellOnFlea ? "CAN LIST" : market.SellUnavailableReason ?? "CANNOT LIST");

        foreach (var offer in market.LowestOffers.Take(12))
        {
            AddCard(parent,
                offer.IsBarter ? "BARTER OFFER" : "CASH OFFER",
                $"Unit ₽{offer.UnitPrice:N0} • listed ₽{offer.ListedUnitPrice:N0} • qty {offer.Quantity:N0} • {offer.ConditionPercent}% {offer.ConditionLabel}",
                $"{offer.PriceSource} • {FormatDuration(offer.SecondsRemaining)} remaining");
        }
    }

    private void RenderItemUsage(Transform parent, HermesItemHideoutUsageResponse? usage)
    {
        AddSectionHeader(parent, "QUEST, HIDEOUT & CRAFT USES");
        if (usage is null)
        {
            AddEmptyState(parent, _state!.DetailLoading ? "Loading profile uses..." : "Profile usage unavailable.", string.Empty);
            return;
        }
        if (!usage.Found)
        {
            AddEmptyState(parent, "No profile usage analysis.", usage.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(parent,
            ("OWNED", FormatCount(usage.OwnedQuantity)),
            ("OWNED FIR", FormatCount(usage.OwnedFoundInRaidQuantity)),
            ("QUEST USES", usage.QuestUses.Count.ToString("N0")),
            ("UPGRADES", usage.UpgradeUses.Count.ToString("N0")),
            ("PRODUCED BY", usage.ProducedBy.Count.ToString("N0")),
            ("USED BY", usage.UsedBy.Count.ToString("N0")));

        foreach (var quest in usage.QuestUses.Take(30))
        {
            AddCard(parent,
                quest.QuestName,
                quest.ProgressText,
                $"{quest.TraderName} • {quest.QuestStatus}{(quest.FoundInRaidRequired ? " • FIR" : string.Empty)}");
        }
        foreach (var upgrade in usage.UpgradeUses.Take(30))
        {
            AddCard(parent,
                $"{upgrade.AreaName} L{upgrade.TargetLevel}",
                $"Owned {FormatCount(upgrade.Owned)} / {FormatCount(upgrade.Required)} • missing {FormatCount(upgrade.Missing)} • {upgrade.AcquisitionSource}",
                upgrade.Status);
        }
        foreach (var craft in usage.UsedBy.Take(30))
        {
            AddCard(parent,
                $"USED BY • {craft.OutputName}",
                $"{craft.StationName} L{craft.RequiredStationLevel} • requires {FormatCount(craft.ItemCount)} • owned {FormatCount(craft.Owned)} • missing {FormatCount(craft.Missing)}",
                craft.Status,
                () => _state.Navigate("Crafts"));
        }
        foreach (var craft in usage.ProducedBy.Take(30))
        {
            AddCard(parent,
                $"PRODUCED BY • {craft.OutputName}",
                $"{craft.StationName} L{craft.RequiredStationLevel} • output {craft.OutputQuantity:N0} • {FormatDuration(craft.DurationSeconds)}",
                craft.Status,
                () => _state.Navigate("Crafts"));
        }
    }

    #endregion

    #region Hideout

    private void RenderHideout(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.HideoutStatus, _state.HideoutLoading, _state.RefreshActive);
        var summary = _state.HideoutSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.HideoutLoading ? "Reading the active PMC hideout..." : "No hideout snapshot loaded.", "Refresh after the active profile is available.");
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Hideout data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("READY", summary.ReadyAreaCount.ToString("N0")),
            ("MATERIAL BLOCKED", summary.MaterialBlockedAreaCount.ToString("N0")),
            ("PROGRESSION BLOCKED", summary.ProgressionBlockedAreaCount.ToString("N0")),
            ("ACTIVE PRODUCTIONS", summary.Resources.ActiveProductionCount.ToString("N0")),
            ("READY TO COLLECT", summary.Resources.CompletedProductionCount.ToString("N0")),
            ("GENERATOR", summary.Resources.GeneratorActive ? "RUNNING" : "OFF"));

        var toolbar = CreateToolbar(root);
        AddToolbarLabel(toolbar, "AREAS");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER AREAS", _hideoutSearch, 220f);
        search.onEndEdit.AddListener(value =>
        {
            _hideoutSearch = value.Trim();
            Invalidate();
        });
        AddButton(toolbar, $"FILTER: {_hideoutFilter}", CycleHideoutFilter, 156f);

        var split = HermesNativeUiFramework.CreateSplitView(root, 340f);
        var splitLayout = split.Root.gameObject.AddComponent<LayoutElement>();
        splitLayout.flexibleHeight = 1f;
        splitLayout.flexibleWidth = 1f;
        AddVerticalLayout(split.Left, 5, 5, 5, 5, 2f);
        AddVerticalLayout(split.Right, 5, 5, 5, 5, 2f);
        var areas = CreateScroll(split.Left, "hideout-areas", true);
        var details = CreateScroll(split.Right, "hideout-details", true);

        var filtered = FilterHideoutAreas(summary.Areas).Take(MaximumRowsPerSection).ToList();
        AddSectionHeader(areas.Content, $"AREAS  {filtered.Count:N0}");
        foreach (var area in filtered)
        {
            var selected = string.Equals(_state.SelectedHideoutArea?.AreaKey, area.AreaKey, StringComparison.OrdinalIgnoreCase);
            var requirementPreview = BuildHideoutRequirementPreview(area);
            AddCard(
                areas.Content,
                area.Name,
                $"Current L{area.CurrentLevel} / L{area.MaximumLevel} • target {(area.TargetLevel.HasValue ? $"L{area.TargetLevel}" : "complete")}\n{requirementPreview}",
                area.Status,
                () =>
                {
                    _state.SelectHideoutArea(area);
                    Invalidate(0.20f);
                },
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }
        if (filtered.Count == 0)
        {
            AddEmptyState(areas.Content, "No areas match the filter.", string.Empty);
        }

        RenderHideoutDetails(details.Content, summary);
    }

    private IEnumerable<HermesHideoutAreaSummary> FilterHideoutAreas(IEnumerable<HermesHideoutAreaSummary> areas)
    {
        var query = _hideoutSearch.Trim();
        return areas
            .Where(area => query.Length == 0
                           || area.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || area.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(area => _hideoutFilter switch
            {
                "READY" => area.Status.Contains("ready", StringComparison.OrdinalIgnoreCase),
                "BLOCKED" => area.Status.Contains("blocked", StringComparison.OrdinalIgnoreCase) || area.MissingItemTypes > 0,
                "BUILDING" => area.IsConstructing,
                "COMPLETE" => !area.TargetLevel.HasValue,
                _ => true
            })
            .OrderByDescending(area => area.IsConstructing)
            .ThenByDescending(area => area.MissingItemTypes == 0 && area.TargetLevel.HasValue)
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void CycleHideoutFilter()
    {
        _hideoutFilter = _hideoutFilter switch
        {
            "ALL" => "READY",
            "READY" => "BLOCKED",
            "BLOCKED" => "BUILDING",
            "BUILDING" => "COMPLETE",
            _ => "ALL"
        };
        Invalidate();
    }

    private void RenderHideoutDetails(Transform parent, HermesHideoutSummaryResponse summary)
    {
        var area = _state!.SelectedHideoutArea;
        var detail = _state.HideoutDetail;
        if (area is null)
        {
            AddSectionHeader(parent, "RESOURCES");
            var resources = summary.Resources;
            AddMetricGrid(parent,
                ("FUEL CONTAINERS", resources.FuelContainerCount.ToString("N0")),
                ("FUEL REMAINING", resources.FuelResourceRemaining.ToString("0.##")),
                ("GENERATOR RUNTIME", FormatDuration(resources.EstimatedGeneratorRuntimeSeconds ?? 0L)),
                ("FUEL COUNTER", resources.FuelCounter?.ToString("0.##") ?? "N/A"),
                ("AIR FILTER", resources.AirFilterCounter?.ToString("0.##") ?? "N/A"),
                ("WATER FILTER", resources.WaterFilterCounter?.ToString("0.##") ?? "N/A"));

            AddSectionHeader(parent, "ACTIVE PRODUCTIONS");
            if (summary.ActiveProductions.Count == 0)
            {
                AddEmptyState(parent, "No active productions.", string.Empty);
            }
            foreach (var production in summary.ActiveProductions.Take(40))
            {
                AddCard(parent,
                    production.OutputName,
                    $"{production.StationName} • output {production.OutputQuantity:N0} • {FormatDuration(production.SecondsRemaining)}",
                    production.Status,
                    string.IsNullOrWhiteSpace(production.OutputTemplateId)
                        ? null
                        : () => _state.Window.OpenForPreviewItem(production.OutputTemplateId!, "Hideout production"));
            }
            return;
        }

        AddSectionHeader(parent, area.Name);
        AddMetricGrid(parent,
            ("CURRENT", $"L{area.CurrentLevel}"),
            ("TARGET", area.TargetLevel.HasValue ? $"L{area.TargetLevel}" : "COMPLETE"),
            ("STATUS", area.Status),
            ("MISSING TYPES", area.MissingItemTypes.ToString("N0")),
            ("MISSING COST", Money(detail?.EstimatedMissingAcquisitionCost ?? area.EstimatedMissingHandbookCost)),
            ("CONSTRUCTION", detail is null ? "N/A" : FormatDuration(detail.ConstructionSeconds)));

        if (_state.HideoutLoading && detail is null)
        {
            AddEmptyState(parent, "Loading area requirements...", string.Empty);
            return;
        }
        if (detail is null || !detail.Found)
        {
            AddEmptyState(parent, "Area detail unavailable.", detail?.Message ?? string.Empty);
            return;
        }

        AddSectionHeader(parent, "REQUIREMENTS");
        foreach (var requirement in detail.Requirements)
        {
            var description = $"Owned {FormatCount(requirement.Owned)} / {FormatCount(requirement.Required)} • missing {FormatCount(requirement.Missing)}"
                              + (requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)
                              + (!string.IsNullOrWhiteSpace(requirement.AcquisitionSource) ? $" • {requirement.AcquisitionSource}" : string.Empty);
            AddCard(
                parent,
                requirement.Name,
                description,
                requirement.IsMet ? "MET" : $"MISSING • {Money(requirement.EstimatedMissingCost)}",
                string.IsNullOrWhiteSpace(requirement.ItemTemplateId)
                    ? null
                    : () => _state.Window.OpenForPreviewItem(requirement.ItemTemplateId!, "Hideout requirement"));
        }
    }

    #endregion

    #region Crafts

    private void RenderCrafts(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.CraftStatus, _state.CraftLoading, _state.RefreshActive);
        var response = _state.CraftSummary;
        if (response is null)
        {
            AddEmptyState(root, _state.CraftLoading ? "Indexing profile-scoped recipes..." : "No craft snapshot loaded.", "Only recipes belonging to installed stations on the active PMC profile are shown.");
            return;
        }
        if (!response.Found)
        {
            AddEmptyState(root, "Craft data unavailable.", response.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("TOTAL", response.Crafts.Count.ToString("N0")),
            ("AVAILABLE", response.Crafts.Count(craft => craft.IsAvailable).ToString("N0")),
            ("READY NOW", response.Crafts.Count(craft => craft.CanStartNow).ToString("N0")),
            ("PROFITABLE", response.Crafts.Count(craft => craft.EstimatedEconomicProfit > 0).ToString("N0")),
            ("ACTIVE", response.Crafts.Count(craft => craft.IsActive).ToString("N0")),
            ("COMPLETE", response.Crafts.Count(craft => craft.IsComplete).ToString("N0")));

        var toolbar = CreateToolbar(root);
        AddToolbarLabel(toolbar, "RECIPES");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER RECIPES", _craftSearch, 240f);
        search.onEndEdit.AddListener(value =>
        {
            _craftSearch = value.Trim();
            Invalidate();
        });
        AddButton(toolbar, $"FILTER: {_craftFilter}", CycleCraftFilter, 166f);

        var split = HermesNativeUiFramework.CreateSplitView(root, 360f);
        var splitLayout = split.Root.gameObject.AddComponent<LayoutElement>();
        splitLayout.flexibleHeight = 1f;
        splitLayout.flexibleWidth = 1f;
        AddVerticalLayout(split.Left, 5, 5, 5, 5, 2f);
        AddVerticalLayout(split.Right, 5, 5, 5, 5, 2f);
        var list = CreateScroll(split.Left, "craft-list", true);
        var details = CreateScroll(split.Right, "craft-details", true);

        var crafts = FilterCrafts(response.Crafts).Take(MaximumRowsPerSection).ToList();
        AddSectionHeader(list.Content, $"RECIPES  {crafts.Count:N0}");
        foreach (var craft in crafts)
        {
            var selected = string.Equals(_state.SelectedCraft?.CraftKey, craft.CraftKey, StringComparison.OrdinalIgnoreCase);
            AddCard(
                list.Content,
                $"{craft.OutputQuantity:N0}× {craft.OutputName}",
                $"{craft.StationName} L{craft.RequiredStationLevel} • {FormatDuration(craft.DurationSeconds)} • input {Money(craft.EstimatedEconomicInputValue)} • output {Money(craft.EstimatedOutputValue)}",
                $"{craft.Status} • PROFIT {Money(craft.EstimatedEconomicProfit)}",
                () =>
                {
                    _state.SelectCraft(craft);
                    Invalidate(0.20f);
                },
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }
        if (crafts.Count == 0)
        {
            AddEmptyState(list.Content, "No recipes match the filter.", "Uninstalled-station recipes are removed server-side before reaching this list.");
        }

        RenderCraftDetails(details.Content);
    }

    private IEnumerable<HermesCraftSummary> FilterCrafts(IEnumerable<HermesCraftSummary> crafts)
    {
        var query = _craftSearch.Trim();
        return crafts
            .Where(craft => query.Length == 0
                            || craft.OutputName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.StationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(craft => _craftFilter switch
            {
                "AVAILABLE" => craft.IsAvailable,
                "READY" => craft.CanStartNow,
                "PROFITABLE" => craft.EstimatedEconomicProfit > 0,
                "ACTIVE" => craft.IsActive || craft.IsComplete,
                _ => true
            })
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.IsActive || craft.IsComplete)
            .ThenByDescending(craft => craft.EstimatedEconomicProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
    }

    private void CycleCraftFilter()
    {
        _craftFilter = _craftFilter switch
        {
            "ALL" => "AVAILABLE",
            "AVAILABLE" => "READY",
            "READY" => "PROFITABLE",
            "PROFITABLE" => "ACTIVE",
            _ => "ALL"
        };
        Invalidate();
    }

    private void RenderCraftDetails(Transform parent)
    {
        var craft = _state!.SelectedCraft;
        var detail = _state.CraftDetail;
        if (craft is null)
        {
            AddEmptyState(parent, "Select a recipe.", "Ingredient readiness, purchase plan, station level, quest lock, and value estimates will appear here.");
            return;
        }

        AddSectionHeader(parent, craft.OutputName);
        AddMetricGrid(parent,
            ("STATION", $"{craft.StationName} L{craft.RequiredStationLevel}"),
            ("OUTPUT", craft.OutputQuantity.ToString("N0")),
            ("DURATION", FormatDuration(craft.DurationSeconds)),
            ("STATUS", craft.Status),
            ("CASH NEEDED", Money(craft.EstimatedAdditionalCashCost)),
            ("ECONOMIC PROFIT", Money(craft.EstimatedEconomicProfit)),
            ("PROFIT / HOUR", Money(craft.EstimatedEconomicProfitPerHour)),
            ("PLAN", craft.AcquisitionPlanComplete ? "COMPLETE" : "INCOMPLETE"));

        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId))
        {
            var toolbar = CreateToolbar(parent);
            AddButton(toolbar, "ASK HERMES ABOUT OUTPUT", () => _state.Window.OpenForPreviewItem(craft.OutputTemplateId!, "Craft output"), 210f);
            AddFlexibleSpace(toolbar);
        }

        if (_state.CraftLoading && detail is null)
        {
            AddEmptyState(parent, "Loading recipe details...", string.Empty);
            return;
        }
        if (detail is null || !detail.Found)
        {
            AddEmptyState(parent, "Recipe detail unavailable.", detail?.Message ?? string.Empty);
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail.RequiredQuestName))
        {
            AddCard(parent,
                "QUEST REQUIREMENT",
                detail.RequiredQuestName!,
                detail.RequiredQuestComplete ? "COMPLETE" : "LOCKED");
        }

        AddSectionHeader(parent, "INGREDIENTS");
        foreach (var ingredient in detail.Ingredients)
        {
            var body = $"Owned {FormatCount(ingredient.Owned)} / {FormatCount(ingredient.Required)} • used {FormatCount(ingredient.OwnedUsed)} • missing {FormatCount(ingredient.Missing)}"
                       + (ingredient.FoundInRaidRequired ? " • FIR" : string.Empty)
                       + (ingredient.IsReusableTool ? " • reusable tool" : string.Empty);
            var card = AddCard(
                parent,
                ingredient.Name,
                body,
                ingredient.IsMet ? "READY" : $"PURCHASE {Money(ingredient.EstimatedPurchaseCost)}",
                string.IsNullOrWhiteSpace(ingredient.TemplateId)
                    ? null
                    : () => _state.Window.OpenForPreviewItem(ingredient.TemplateId, "Craft ingredient"));
            foreach (var acquisition in ingredient.AcquisitionPlan.Take(8))
            {
                AddText(card,
                    $"• {acquisition.Source}: {FormatCount(acquisition.Quantity)} × ₽{acquisition.UnitPrice:N0} = ₽{acquisition.TotalCost:N0}{(acquisition.IsFallback ? " (estimate)" : string.Empty)}",
                    12f,
                    false,
                    HermesNativeUiFramework.MutedTextColor);
            }
            if (!string.IsNullOrWhiteSpace(ingredient.CostNote))
            {
                AddText(card, ingredient.CostNote!, 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }

        AddSectionHeader(parent, "VALUATION");
        AddCard(parent, "VALUATION BASIS", detail.ValuationBasis, "READ ONLY");
    }

    #endregion

    #region Stash

    private void RenderStash(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.StashStatus, _state.StashLoading, _state.RefreshActive);
        var summary = _state.StashSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.StashLoading ? "Building the active PMC stash snapshot..." : "No stash snapshot loaded.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Stash data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("INDEPENDENT ITEMS", summary.IndependentItemCount.ToString("N0")),
            ("OCCUPIED CELLS", summary.OccupiedCells.ToString("N0")),
            ("BEST LIQUIDATION", Money(summary.BestDestinationLiquidationValue)),
            ("SAFE / SURPLUS", (summary.SafeToSellInstanceCount + summary.SellSurplusInstanceCount).ToString("N0")),
            ("RECOVERABLE CELLS", summary.RecoverableCells.ToString("N0")),
            ("DUPLICATES", summary.DuplicateGroupCount.ToString("N0")),
            ("CONDITION WARNINGS", summary.DamagedOrDepletedItemCount.ToString("N0")),
            ("POTENTIAL SALE", Money(summary.PotentialBestSaleValue)));

        var tabs = CreateToolbar(root);
        foreach (var view in new[] { "OVERVIEW", "SELL", "CLEANUP", "KEEP", "REVIEW", "DUPLICATES", "DAMAGED" })
        {
            var captured = view;
            AddButton(tabs, view, () =>
            {
                _stashView = captured;
                Invalidate();
            }, view.Length * 8f + 34f, true, string.Equals(_stashView, view, StringComparison.Ordinal));
        }
        AddFlexibleSpace(tabs);
        if (_stashView is not "OVERVIEW")
        {
            var search = AddInput(tabs, "FILTER ITEMS", _stashSearch, 210f);
            search.onEndEdit.AddListener(value =>
            {
                _stashSearch = value.Trim();
                Invalidate();
            });
        }

        var scroll = CreateScroll(root, "stash-content", true);
        RenderStashView(scroll.Content, summary);
    }

    private void RenderStashView(Transform parent, HermesStashSummaryResponse summary)
    {
        switch (_stashView)
        {
            case "SELL":
                AddSectionHeader(parent, "SAFE TO SELL & SURPLUS");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.PotentiallySellQuantity > 0d));
                break;
            case "CLEANUP":
                AddSectionHeader(parent, $"CLEANUP • {summary.RecoverableCells:N0} RECOVERABLE CELLS • {Money(summary.CleanupBestSaleValue)}");
                RenderStashItems(parent, summary.CleanupCandidates);
                break;
            case "KEEP":
                AddSectionHeader(parent, "KEEP");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.Recommendation.Contains("keep", StringComparison.OrdinalIgnoreCase)));
                break;
            case "REVIEW":
                AddSectionHeader(parent, "REVIEW");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.Recommendation.Contains("review", StringComparison.OrdinalIgnoreCase)));
                break;
            case "DUPLICATES":
                AddSectionHeader(parent, "DUPLICATE GROUPS");
                foreach (var group in summary.DuplicateGroups.Where(MatchesStashSearch).Take(MaximumRowsPerSection))
                {
                    AddCard(parent,
                        group.Name,
                        $"{group.InstanceCount:N0} instance(s) • owned {FormatCount(group.OwnedQuantity)} • reserve {FormatCount(group.SuggestedReserveQuantity)} • excess {FormatCount(group.PotentialExcessQuantity)} • {group.OccupiedCells:N0} cells",
                        $"{group.BestSaleDestination} • {Money(group.PotentialExcessSaleValue)}",
                        () => _state!.Window.OpenForPreviewItem(group.ItemKey, "Stash duplicate"));
                }
                break;
            case "DAMAGED":
                AddSectionHeader(parent, "DAMAGED OR DEPLETED");
                foreach (var item in summary.DamagedOrDepletedItems.Where(MatchesStashSearch).Take(MaximumRowsPerSection))
                {
                    AddCard(parent,
                        item.Name,
                        $"{item.InstanceLabel} • {item.ConditionPercent}% • {item.ConditionCurrent:0.##}/{item.ConditionMaximum:0.##} • threshold {item.ThresholdPercent}%",
                        $"{item.Status} • {item.BestSaleDestination} {Money(item.BestSaleValue)}",
                        () => _state!.Window.OpenForInventoryItem(item.InstanceKey));
                }
                break;
            default:
                AddSectionHeader(parent, "VALUATION");
                AddMetricGrid(parent,
                    ("HANDBOOK", Money(summary.FullHandbookReferenceValue)),
                    ("CONDITION ADJUSTED", Money(summary.ConditionAdjustedHandbookValue)),
                    ("TRADER LIQUIDATION", Money(summary.BestTraderLiquidationValue)),
                    ("FLEA NET", Money(summary.EstimatedFleaNetValue)));
                AddSectionHeader(parent, "SALE DESTINATIONS");
                foreach (var destination in summary.SaleDestinationBreakdown)
                {
                    AddCard(parent, destination.Destination, $"{destination.ItemCount:N0} item(s)", Money(destination.RoubleEquivalent));
                }
                AddSectionHeader(parent, "MOST VALUABLE ITEMS");
                RenderStashItems(parent, summary.MostValuableItems.Take(40));
                break;
        }
    }

    private void RenderStashItems(Transform parent, IEnumerable<HermesStashValuationItem> source)
    {
        var rows = source.Where(MatchesStashSearch).Take(MaximumRowsPerSection).ToList();
        if (rows.Count == 0)
        {
            AddEmptyState(parent, "No items match this view.", string.Empty);
            return;
        }

        foreach (var item in rows)
        {
            AddCard(parent,
                item.Name,
                $"{item.InstanceLabel} • qty {FormatCount(item.Quantity)} • {item.ConditionPercent}% • {item.OccupiedCells:N0} cell(s) • reserve {FormatCount(item.RecommendedKeepQuantity)} • sell {FormatCount(item.PotentiallySellQuantity)}",
                $"{item.Recommendation} • {item.BestSaleDestination} {Money(item.BestSaleValue)}",
                () => _state!.Window.OpenForInventoryItem(item.InstanceKey));
        }
    }

    private bool MatchesStashSearch(HermesStashValuationItem item)
        => _stashSearch.Length == 0
           || item.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Category.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Recommendation.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || (item.BestSaleDestination?.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool MatchesStashSearch(HermesStashDuplicateGroup group)
        => _stashSearch.Length == 0
           || group.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || group.Note.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || (group.BestSaleDestination?.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool MatchesStashSearch(HermesStashConditionItem item)
        => _stashSearch.Length == 0
           || item.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Status.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Recommendation.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Loadout and raid planner

    private void RenderLoadout(RectTransform parent, bool forceRaidPlanner)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.LoadoutStatus, _state.LoadoutLoading, _state.RefreshActive);
        var summary = _state.LoadoutSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.LoadoutLoading ? "Analyzing the active PMC equipment tree..." : "No loadout snapshot loaded.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Loadout data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        if (forceRaidPlanner)
        {
            _loadoutView = "RAID PLANNER";
        }
        else if (string.Equals(_loadoutView, "RAID PLANNER", StringComparison.Ordinal))
        {
            // Raid Planner is a separate top-level workspace. Never carry its internal
            // view state back into the Loadout workspace when the user switches tabs.
            _loadoutView = "OVERVIEW";
        }

        AddMetricGrid(root,
            ("READINESS", summary.Readiness),
            ("SCORE", $"{summary.ReadinessScore}/100"),
            ("CRITICAL", summary.CriticalCount.ToString("N0")),
            ("WARNINGS", summary.WarningCount.ToString("N0")),
            ("AT RISK", Money(summary.ValueSummary.AtRiskReplacementValue)),
            ("UNINSURED", summary.ValueSummary.UninsuredItemCount.ToString("N0")));

        if (!forceRaidPlanner)
        {
            var tabs = CreateToolbar(root);
            foreach (var view in new[] { "OVERVIEW", "WEAPONS", "ARMOR", "MEDICAL", "QUESTS", "VALUE" })
            {
                var captured = view;
                AddButton(tabs, view, () =>
                {
                    _loadoutView = captured;
                    Invalidate();
                }, view.Length * 8f + 34f, true, string.Equals(_loadoutView, view, StringComparison.Ordinal));
            }
            AddFlexibleSpace(tabs);
        }

        var scroll = CreateScroll(root, forceRaidPlanner ? "raid-planner" : "loadout-content", true);
        switch (_loadoutView)
        {
            case "WEAPONS":
                RenderWeapons(scroll.Content, summary);
                break;
            case "ARMOR":
                RenderArmor(scroll.Content, summary);
                break;
            case "MEDICAL":
                RenderMedical(scroll.Content, summary);
                break;
            case "QUESTS":
                RenderLoadoutQuests(scroll.Content, summary);
                break;
            case "VALUE":
                RenderLoadoutValue(scroll.Content, summary.ValueSummary);
                break;
            case "RAID PLANNER":
                RenderRaidPlans(scroll.Content, summary);
                break;
            default:
                RenderLoadoutOverview(scroll.Content, summary);
                break;
        }
    }

    private void RenderLoadoutOverview(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "VITALS");
        AddMetricGrid(parent,
            ("HEALTH", $"{summary.Vitals.CurrentHealth:0}/{summary.Vitals.MaximumHealth:0} • {summary.Vitals.HealthPercent}%"),
            ("HYDRATION", $"{summary.Vitals.CurrentHydration:0}/{summary.Vitals.MaximumHydration:0} • {summary.Vitals.HydrationPercent}%"),
            ("ENERGY", $"{summary.Vitals.CurrentEnergy:0}/{summary.Vitals.MaximumEnergy:0} • {summary.Vitals.EnergyPercent}%"));

        AddSectionHeader(parent, "WARNINGS");
        if (summary.Warnings.Count == 0)
        {
            AddEmptyState(parent, "No loadout warnings.", string.Empty);
        }
        foreach (var warning in summary.Warnings)
        {
            AddCard(parent, warning.Category, warning.Message, warning.Severity);
        }

        AddSectionHeader(parent, "EQUIPPED SLOTS");
        foreach (var slot in summary.EquippedSlots)
        {
            AddCard(parent,
                slot.SlotName,
                $"{slot.ItemName} • {slot.ConditionPercent}% {slot.ConditionDescription} • {slot.ChildItemCount:N0} child item(s)",
                slot.Status,
                () => _state!.OpenNamedItem(slot.ItemName, "equipped loadout"));
        }
    }

    private void RenderWeapons(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "WEAPONS");
        foreach (var weapon in summary.Weapons)
        {
            var body = $"{weapon.SlotName} • {weapon.DurabilityPercent}% {weapon.DurabilityDescription} • {weapon.Caliber} • magazine {weapon.MagazineName ?? "none"} {FormatCount(weapon.LoadedRounds)}/{weapon.MagazineCapacity}"
                       + $" • spare mags {weapon.CompatibleSpareMagazineCount:N0} • spare rounds {FormatCount(weapon.SpareMagazineRounds)} • loose {FormatCount(weapon.LooseCompatibleRounds)}";
            var card = AddCard(
                parent,
                weapon.Name,
                body,
                weapon.Status,
                () => _state!.OpenNamedItem(weapon.Name, "equipped weapon"));
            foreach (var warning in weapon.Warnings)
            {
                AddText(card, $"• {warning}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }
    }

    private void RenderArmor(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "ARMOR");
        foreach (var armor in summary.Armor)
        {
            var card = AddCard(parent,
                armor.Name,
                $"{armor.SlotName} • class {armor.MaximumArmorClass} • {armor.ConditionPercent}% {armor.ConditionDescription} • inserts {armor.InstalledArmorInsertCount}/{armor.ArmorInsertSlotCount} • missing required {armor.MissingRequiredArmorInsertCount}",
                armor.Status,
                () => _state!.OpenNamedItem(armor.Name, "equipped armor"));
            foreach (var warning in armor.Warnings)
            {
                AddText(card, $"• {warning}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }
    }

    private void RenderMedical(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        var medical = summary.Medical;
        AddSectionHeader(parent, "MEDICAL COVERAGE");
        AddMetricGrid(parent,
            ("MED ITEMS", medical.MedicalItemCount.ToString("N0")),
            ("HEALING RESOURCE", FormatCount(medical.TotalHealingResource)),
            ("LIGHT BLEED", YesNo(medical.HasLightBleedTreatment)),
            ("HEAVY BLEED", YesNo(medical.HasHeavyBleedTreatment)),
            ("FRACTURE", YesNo(medical.HasFractureTreatment)),
            ("PAIN", YesNo(medical.HasPainTreatment)),
            ("SURGERY", YesNo(medical.HasSurgeryKit)),
            ("FOOD / WATER", $"{medical.EnergyProvisionCount}/{medical.HydrationProvisionCount}"));
        foreach (var item in medical.Items)
        {
            AddCard(
                parent,
                item.Name,
                $"{item.CurrentResource:0.##}/{item.MaximumResource:0.##}",
                item.Coverage,
                () => _state!.OpenNamedItem(item.Name, "carried medical"));
        }
    }

    private void RenderLoadoutQuests(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "QUEST LOADOUT REQUIREMENTS");
        foreach (var requirement in summary.QuestRequirements)
        {
            AddCard(parent,
                requirement.QuestName,
                $"{requirement.MapName} • {requirement.RequiredEquipment} • carried {FormatCount(requirement.CarriedQuantity)}/{FormatCount(requirement.RequiredQuantity)}"
                + (requirement.FoundInRaidRequired ? $" • FIR carried {FormatCount(requirement.FoundInRaidCarriedQuantity)}" : string.Empty)
                + $" • {requirement.Note}",
                requirement.IsSatisfied ? "SATISFIED" : requirement.IsRaidCritical ? "CRITICAL" : "MISSING");
        }
    }

    private void RenderLoadoutValue(Transform parent, HermesLoadoutValueSummary value)
    {
        AddSectionHeader(parent, "VALUE & INSURANCE");
        AddMetricGrid(parent,
            ("TRADER LIQUIDATION", Money(value.TraderLiquidationValue)),
            ("MARKET REPLACEMENT", Money(value.MarketReplacementValue)),
            ("BEST REPLACEMENT", Money(value.BestReplacementValue)),
            ("AT RISK", Money(value.AtRiskReplacementValue)),
            ("PROTECTED", Money(value.ProtectedReplacementValue)),
            ("UNINSURED", Money(value.UninsuredReplacementValue)),
            ("INSURANCE COST", Money(value.EstimatedInsuranceCost)),
            ("STATUS", value.InsuranceStatus));

        foreach (var category in value.Categories)
        {
            AddCard(parent,
                category.Category,
                $"{category.ItemCount:N0} item(s) • liquidation {Money(category.TraderLiquidationValue)} • replacement {Money(category.BestReplacementValue)}",
                $"AT RISK {Money(category.AtRiskReplacementValue)}");
        }

        AddSectionHeader(parent, "EQUIPPED VALUE ITEMS");
        foreach (var item in value.Items.OrderByDescending(item => item.BestReplacementValue ?? 0L))
        {
            AddCard(parent,
                item.Name,
                $"{item.SlotName} • {item.Category} • {item.ConditionPercent}% {item.ConditionDescription} • liquidation {Money(item.TraderLiquidationValue)} • replacement {Money(item.BestReplacementValue)}",
                $"{item.InsuranceStatus}{(item.IsHighValueUninsured ? " • HIGH VALUE" : string.Empty)}",
                () => _state!.OpenLoadoutItem(item.ProfileItemId, item.Name));
        }
    }

    private void RenderRaidPlans(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        var toolbar = CreateToolbar(parent);
        AddToolbarLabel(toolbar, "RAID PLANS");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER MAPS / QUESTS", _raidSearch, 260f);
        search.onEndEdit.AddListener(value =>
        {
            _raidSearch = value.Trim();
            Invalidate();
        });

        var plans = summary.RaidPlans
            .Where(plan => _raidSearch.Length == 0
                           || plan.MapName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)
                           || plan.Quests.Any(quest => quest.QuestName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(plan => plan.MissingRequirementCount)
            .ThenByDescending(plan => plan.ActiveQuestCount)
            .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var plan in plans)
        {
            var expanded = _expandedRaidMaps.Contains(plan.MapName);
            var card = AddCard(parent,
                plan.MapName,
                $"{plan.ActiveQuestCount:N0} active quest(s) • objectives {plan.CompletedObjectiveCount}/{plan.ObjectiveCount} • missing requirements {plan.MissingRequirementCount}/{plan.RaidRequirementCount}",
                plan.Status,
                () =>
                {
                    if (!_expandedRaidMaps.Add(plan.MapName))
                    {
                        _expandedRaidMaps.Remove(plan.MapName);
                    }
                    Invalidate();
                },
                expanded ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            if (!expanded)
            {
                continue;
            }

            AddText(card, "REQUIRED GEAR", 13f, true, HermesNativeUiFramework.AccentTextColor);
            foreach (var requirement in plan.CombinedRequirements)
            {
                AddText(card,
                    $"• {(requirement.IsSatisfied ? "✓" : "!")} {requirement.RequiredEquipment}: carried {FormatCount(requirement.CarriedQuantity)}/{FormatCount(requirement.RequiredQuantity)} • missing {FormatCount(requirement.MissingQuantity)} • {requirement.Note}",
                    12f,
                    false,
                    requirement.IsSatisfied ? HermesNativeUiFramework.MutedTextColor : HermesNativeUiFramework.NormalTextColor);
            }

            AddText(card, "QUESTS", 13f, true, HermesNativeUiFramework.AccentTextColor);
            foreach (var quest in plan.Quests)
            {
                AddText(card, $"• {quest.QuestName} • {quest.TraderName} • {quest.Status}", 12f, true, HermesNativeUiFramework.NormalTextColor);
                foreach (var objective in quest.Objectives)
                {
                    AddText(card,
                        $"   {(objective.IsCompleted ? "✓" : "•")} {objective.Description} • {objective.Status}",
                        12f,
                        false,
                        HermesNativeUiFramework.MutedTextColor);
                }
            }
            foreach (var note in plan.Notes)
            {
                AddText(card, $"NOTE • {note}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }

        if (plans.Count == 0)
        {
            AddEmptyState(parent, "No raid plans match the filter.", string.Empty);
        }
    }

    #endregion

    #region Native element helpers

    private RectTransform CreateVerticalRoot(RectTransform parent)
    {
        var root = new GameObject("VerticalWorkspace", typeof(RectTransform), typeof(VerticalLayoutGroup));
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        HermesNativeUiFramework.Stretch(rect, 0f, 0f, 0f, 0f);
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return rect;
    }

    private static RectTransform CreatePanel(Transform parent, string name, Color color)
    {
        var panel = HermesNativeUiFramework.CreatePanel(name, parent, color);
        var image = panel.GetComponent<Image>();
        image.raycastTarget = true;
        return panel;
    }

    private static VerticalLayoutGroup AddVerticalLayout(RectTransform panel, int left, int right, int top, int bottom, float spacing)
    {
        var layout = panel.gameObject.GetComponent<VerticalLayoutGroup>() ?? panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(left, right, top, bottom);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    private void AddStatusStrip(Transform parent, string status, bool loading, Action refresh)
    {
        var strip = CreatePanel(parent, "StatusStrip", new Color(0f, 0f, 0f, 0.22f));
        var layout = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(9, 6, 3, 3);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var stripElement = strip.gameObject.AddComponent<LayoutElement>();
        stripElement.minHeight = CompactToolbarHeight;
        stripElement.preferredHeight = CompactToolbarHeight;
        stripElement.flexibleHeight = 0f;
        var label = AddText(strip, string.IsNullOrWhiteSpace(status) ? "CURRENT PROFILE DATA" : status, 12.5f, false, HermesNativeUiFramework.MutedTextColor);
        label.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        if (loading)
        {
            AddText(strip, "WORKING", 11.5f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Center, 76f);
        }
        AddButton(strip, "REFRESH", () => refresh(), 76f, !loading);
        AddAnchoredBottomRule(strip);
    }

    private static RectTransform CreateToolbar(Transform parent)
    {
        var toolbar = new GameObject("Toolbar", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        toolbar.transform.SetParent(parent, false);
        var rect = (RectTransform)toolbar.transform;
        var layout = toolbar.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(2, 2, 2, 2);
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var element = toolbar.GetComponent<LayoutElement>();
        element.minHeight = CompactToolbarHeight;
        element.preferredHeight = CompactToolbarHeight;
        element.flexibleHeight = 0f;
        return rect;
    }

    private static void AddToolbarLabel(Transform parent, string text)
    {
        AddText(parent, text, 13f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Left, 145f);
    }

    private static void AddFlexibleSpace(Transform parent)
    {
        var spacer = new GameObject("FlexibleSpace", typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(parent, false);
        var spacerElement = spacer.GetComponent<LayoutElement>();
        spacerElement.flexibleWidth = 1f;
        spacerElement.flexibleHeight = 0f;
    }

    private static TMP_InputField AddInput(Transform parent, string placeholderText, string value, float preferredWidth)
    {
        var root = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.SearchBorderSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        image.raycastTarget = true;
        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = 120f;
        layout.preferredWidth = preferredWidth;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;

        var viewport = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(root.transform, false);
        var viewportRect = (RectTransform)viewport.transform;
        HermesNativeUiFramework.Stretch(viewportRect, 12f, 5f, 12f, 5f);

        var text = HermesNativeUiFramework.CreateText("Text", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.color = HermesNativeUiFramework.NormalTextColor;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Masking;

        var placeholder = HermesNativeUiFramework.CreateText("Placeholder", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.text = placeholderText;
        placeholder.color = HermesNativeUiFramework.MutedTextColor;
        placeholder.enableWordWrapping = false;

        var input = root.GetComponent<TMP_InputField>();
        input.targetGraphic = image;
        input.textViewport = viewportRect;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 160;
        input.SetTextWithoutNotify(value ?? string.Empty);
        return input;
    }

    private static float AssistantActionButtonWidth(string label)
    {
        var normalized = string.IsNullOrWhiteSpace(label) ? "OPEN" : label.Trim();
        return Mathf.Clamp(72f + normalized.Length * 5.4f, 106f, 176f);
    }

    private static string BuildHideoutRequirementPreview(HermesHideoutAreaSummary area)
    {
        if (!area.TargetLevel.HasValue)
        {
            return "No further upgrade requirements.";
        }

        if (area.RequiredItems.Count == 0)
        {
            return area.Status.Contains("progression", StringComparison.OrdinalIgnoreCase)
                ? "No item materials • blocked by a non-item requirement."
                : "No item materials required for the next level.";
        }

        var items = area.RequiredItems
            .OrderBy(requirement => requirement.IsMet)
            .ThenByDescending(requirement => requirement.Missing)
            .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .Select(requirement =>
            {
                var owned = FormatCount(requirement.Owned);
                var required = FormatCount(requirement.Required);
                var fir = requirement.FoundInRaidRequired ? " FIR" : string.Empty;
                return requirement.IsMet
                    ? $"• {requirement.Name}: {owned}/{required}{fir} ✓"
                    : $"• {requirement.Name}: {owned}/{required}{fir} — missing {FormatCount(requirement.Missing)}";
            });

        return "Required items for next upgrade:\n" + string.Join("\n", items);
    }

    private static Button AddButton(
        Transform parent,
        string text,
        UnityAction action,
        float width,
        bool interactable = true,
        bool selected = false)
    {
        var root = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.ButtonBackgroundSprite;
        image.type = image.sprite != null && image.sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        image.color = selected ? new Color(0.78f, 0.77f, 0.70f, 0.95f) : new Color(0.24f, 0.25f, 0.24f, 0.82f);
        image.raycastTarget = true;
        var button = root.GetComponent<Button>();
        button.targetGraphic = image;
        button.interactable = interactable;
        button.transition = Selectable.Transition.ColorTint;
        button.onClick.AddListener(action);
        var colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = selected ? new Color(0.90f, 0.89f, 0.82f, 1f) : new Color(0.37f, 0.38f, 0.36f, 1f);
        colors.pressedColor = new Color(0.16f, 0.17f, 0.16f, 1f);
        colors.disabledColor = new Color(0.12f, 0.13f, 0.13f, 0.55f);
        button.colors = colors;
        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;
        var label = HermesNativeUiFramework.CreateText("Label", root.transform, 12.5f, true, TextAlignmentOptions.Center);
        label.text = text;
        label.color = selected ? new Color32(26, 28, 27, 255) : HermesNativeUiFramework.NormalTextColor;
        HermesNativeUiFramework.Stretch(label.rectTransform, 7f, 2f, 7f, 2f);
        return button;
    }

    private static TMP_Text AddText(
        Transform parent,
        string text,
        float size,
        bool bold,
        Color color,
        TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        float? preferredWidth = null)
    {
        var label = HermesNativeUiFramework.CreateText("Text", parent, size, bold, alignment);
        label.text = text ?? string.Empty;
        label.color = color;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (preferredWidth.HasValue)
        {
            layout.minWidth = preferredWidth.Value;
            layout.preferredWidth = preferredWidth.Value;
        }
        else
        {
            layout.flexibleWidth = 1f;
        }
        layout.minHeight = Math.Max(18f, size + 5f);
        layout.flexibleHeight = 0f;
        var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return label;
    }

    private static RectTransform AddCard(
        Transform parent,
        string title,
        string body,
        string meta,
        Action? onClick = null,
        Color? color = null)
    {
        var types = onClick is null
            ? new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) }
            : new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) };
        var root = new GameObject("Card", types);
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        var image = root.GetComponent<Image>();
        image.color = color ?? HermesNativeUiFramework.RowColor;
        image.raycastTarget = onClick is not null;
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(9, 9, 6, 6);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var cardElement = root.GetComponent<LayoutElement>();
        cardElement.minHeight = 48f;
        cardElement.flexibleHeight = 0f;
        if (onClick is not null)
        {
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            button.onClick.AddListener(() => onClick());
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(image.color.r + 0.08f, image.color.g + 0.08f, image.color.b + 0.08f, Math.Max(0.88f, image.color.a));
            colors.pressedColor = new Color(0.12f, 0.13f, 0.13f, 0.92f);
            button.colors = colors;
        }

        AddText(root.transform, title, 15f, true, HermesNativeUiFramework.NormalTextColor);
        if (!string.IsNullOrWhiteSpace(body))
        {
            AddText(root.transform, body, 13f, false, HermesNativeUiFramework.NormalTextColor);
        }
        if (!string.IsNullOrWhiteSpace(meta))
        {
            AddText(root.transform, meta, 11.5f, false, HermesNativeUiFramework.MutedTextColor);
        }
        AddBottomSeparator(root.transform);
        return rect;
    }

    private static void AddSectionHeader(Transform parent, string title)
    {
        HermesNativeUiFramework.CreateSectionHeader(parent, title, CompactSectionHeight);
    }

    private static void AddEmptyState(Transform parent, string title, string detail)
    {
        var card = AddCard(parent, title, detail, "");
        card.GetComponent<Image>().color = new Color(0.02f, 0.025f, 0.025f, 0.42f);
    }

    private static void AddMetricGrid(Transform parent, params (string Label, string Value)[] metrics)
    {
        if (metrics.Length == 0)
        {
            return;
        }

        var root = new GameObject("MetricGrid", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var rootLayout = root.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = 3f;
        rootLayout.childAlignment = TextAnchor.UpperLeft;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var rootElement = root.GetComponent<LayoutElement>();
        rootElement.minHeight = CompactMetricHeight;
        rootElement.flexibleHeight = 0f;

        var columns = metrics.Length switch
        {
            <= 4 => metrics.Length,
            5 or 6 => 3,
            _ => 4
        };
        for (var offset = 0; offset < metrics.Length; offset += columns)
        {
            var row = new GameObject("MetricRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(root.transform, false);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 3f;
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            var rowElement = row.GetComponent<LayoutElement>();
            rowElement.minHeight = CompactMetricHeight;
            rowElement.preferredHeight = CompactMetricHeight;
            rowElement.flexibleHeight = 0f;

            var count = Math.Min(columns, metrics.Length - offset);
            for (var index = 0; index < count; index++)
            {
                var metric = metrics[offset + index];
                var cell = HermesNativeUiFramework.CreatePanel("Metric", row.transform, HermesNativeUiFramework.RowColor);
                var cellElement = cell.gameObject.AddComponent<LayoutElement>();
                cellElement.minWidth = 145f;
                cellElement.preferredWidth = 210f;
                cellElement.flexibleWidth = 1f;
                cellElement.minHeight = CompactMetricHeight;
                cellElement.preferredHeight = CompactMetricHeight;
                cellElement.flexibleHeight = 0f;
                var cellLayout = cell.gameObject.AddComponent<VerticalLayoutGroup>();
                cellLayout.padding = new RectOffset(8, 8, 4, 4);
                cellLayout.spacing = 0f;
                cellLayout.childAlignment = TextAnchor.MiddleLeft;
                cellLayout.childControlWidth = true;
                cellLayout.childControlHeight = true;
                cellLayout.childForceExpandWidth = true;
                cellLayout.childForceExpandHeight = false;
                AddText(cell, metric.Label, 10f, true, HermesNativeUiFramework.MutedTextColor);
                AddText(cell, metric.Value, 13.5f, true, HermesNativeUiFramework.NormalTextColor);
            }

            for (var index = count; index < columns; index++)
            {
                var spacer = new GameObject("MetricSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacer.transform.SetParent(row.transform, false);
                var spacerElement = spacer.GetComponent<LayoutElement>();
                spacerElement.minWidth = 145f;
                spacerElement.preferredWidth = 210f;
                spacerElement.flexibleWidth = 1f;
                spacerElement.minHeight = CompactMetricHeight;
                spacerElement.preferredHeight = CompactMetricHeight;
                spacerElement.flexibleHeight = 0f;
            }
        }
    }

    private (RectTransform Root, RectTransform Content, ScrollRect Scroll) CreateScroll(Transform parent, string key, bool flexibleHeight)
    {
        var scroll = HermesNativeUiFramework.CreateScrollView(parent, key);
        var layout = scroll.Root.gameObject.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.flexibleHeight = flexibleHeight ? 1f : 0f;
        layout.minHeight = flexibleHeight ? 120f : 72f;
        _activeScrolls[key] = scroll.ScrollRect;
        return (scroll.Root, scroll.Content, scroll.ScrollRect);
    }

    private static void AddAnchoredBottomRule(RectTransform parent)
    {
        var separator = new GameObject("BottomRule", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var rect = (RectTransform)separator.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = new Vector2(0f, 1f);
        var layout = separator.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
    }

    private static void AddBottomSeparator(Transform parent)
    {
        var separator = new GameObject("Separator", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
        separator.GetComponent<LayoutElement>().preferredHeight = 1f;
        separator.GetComponent<LayoutElement>().minHeight = 1f;
    }

    private void CaptureScrollPositions()
    {
        foreach (var pair in _activeScrolls)
        {
            if (pair.Value != null)
            {
                _savedScrollPositions[pair.Key] = pair.Value.verticalNormalizedPosition;
            }
        }
    }

    private string BuildItemResultSetKey()
    {
        if (_state is null)
        {
            return string.Empty;
        }

        var results = _state.SearchResults;
        return string.Join("|",
            _state.SearchQuery.Trim(),
            _state.SearchLoading,
            RuntimeHelpers.GetHashCode(results),
            results.Count);
    }

    private IEnumerator RestoreScrollPositionsNextFrame()
    {
        var forceTop = new HashSet<string>(_scrollsToForceTop, StringComparer.Ordinal);
        _scrollsToForceTop.Clear();

        yield return null;
        Canvas.ForceUpdateCanvases();
        RestoreScrollPositions(forceTop);

        // ContentSizeFitter and ScrollRect can perform one more layout pass after the first
        // canvas update. Reassert forced-top lists once more so a new item search never lands
        // at the bottom because the prior empty result view reported a normalized value of 0.
        if (forceTop.Count > 0)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            RestoreScrollPositions(forceTop);
        }
    }

    private void RestoreScrollPositions(HashSet<string> forceTop)
    {
        foreach (var pair in _activeScrolls)
        {
            var scroll = pair.Value;
            if (scroll == null)
            {
                continue;
            }

            var shouldForceTop = forceTop.Contains(pair.Key);
            if (!shouldForceTop && !_savedScrollPositions.TryGetValue(pair.Key, out _))
            {
                continue;
            }

            var position = shouldForceTop ? 1f : Mathf.Clamp01(_savedScrollPositions[pair.Key]);
            scroll.StopMovement();
            scroll.velocity = Vector2.zero;
            scroll.verticalNormalizedPosition = position;

            if (shouldForceTop && scroll.content != null)
            {
                var anchored = scroll.content.anchoredPosition;
                scroll.content.anchoredPosition = new Vector2(anchored.x, 0f);
                _savedScrollPositions[pair.Key] = 1f;
            }
        }
    }

    private void Invalidate(float delay = 0f)
    {
        _forceRebuild = true;
        _nextSyncAt = Time.unscaledTime + Math.Max(0f, delay);
    }

    private static string Money(long? amount) => amount.HasValue ? $"₽{amount.Value:N0}" : "N/A";
    private static string FormatCount(double value) => Math.Abs(value - Math.Round(value)) < 0.0001d ? Math.Round(value).ToString("N0") : value.ToString("0.##");
    private static string YesNo(bool value) => value ? "COVERED" : "MISSING";

    private static string FormatCurrency(long amount, string currency)
        => currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }
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
            return $"{(int)duration.TotalMinutes}m";
        }
        return $"{Math.Max(1, duration.Seconds)}s";
    }

    #endregion
}
