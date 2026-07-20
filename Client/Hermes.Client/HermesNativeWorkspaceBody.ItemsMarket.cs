using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

internal sealed partial class HermesNativeWorkspaceBody
{
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
                var selectedInstance = selected ? _state.SelectedStashInstance : null;
                var displayedValue = selectedInstance is { ConditionAdjustedReferenceValue: > 0 }
                    ? selectedInstance.ConditionAdjustedReferenceValue
                    : item.ReferencePrice;
                var displayedLabel = selectedInstance switch
                {
                    { ChildItemCount: > 0 } => "Assembly",
                    not null => "Instance",
                    _ => "Reference"
                };
                AddCard(
                    results.Content,
                    item.Name,
                    $"{item.ShortName} • {displayedLabel} {Money(displayedValue)}",
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

        var selectedStashInstance = _state.SelectedStashInstance;
        AddSectionHeader(parent, item.Name);
        AddMetricGrid(parent,
            ("SHORT NAME", item.ShortName),
            (_state.DisplayedItemReferenceLabel, Money(_state.DisplayedItemReferenceValue)),
            ("CHILD VALUE", selectedStashInstance is { ChildItemCount: > 0 }
                ? Money(selectedStashInstance.InstalledComponentReferenceValue)
                : "—"),
            ("OWNED COPIES", _state.StashInstances.Count.ToString("N0")),
            ("PROFILE", _state.StashInstances.Count > 0 ? "OWNED" : "NOT OWNED"));

        var stashSummary = _state.StashInstances.Count == 0
            ? "No matching owned copy is in the active PMC inventory. Trader pricing uses the full-condition base-item estimate."
            : selectedStashInstance is not null
                ? $"Selected: {selectedStashInstance.Label} • {selectedStashInstance.Location} • {selectedStashInstance.ConditionPercent}% {selectedStashInstance.ConditionDescription}"
                : $"{_state.StashInstances.Count:N0} matching owned copy/copies available • base-item estimate selected";
        var stashMeta = _state.StashInstances.Count == 0
            ? "NOT OWNED"
            : $"{_state.StashInstances.Count:N0} COPY/COPIES";
        var stashExpanded = AddItemDetailCollapsibleSection(
            parent,
            "stash-pricing",
            "OWNED COPY PRICING",
            stashSummary,
            stashMeta,
            _state.StashInstances.Count > 0 && !Plugin.Settings.CollapseSectionsByDefault.Value);
        if (stashExpanded)
        {
            AddCard(parent, "BASE ITEM", "Full-condition base-item estimate.", _state.SelectedStashInstanceKey is null ? "SELECTED" : "PREVIEW", () =>
            {
                _state.SelectStashInstance(null);
                Invalidate(0.20f);
            }, _state.SelectedStashInstanceKey is null ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            foreach (var instance in _state.StashInstances.Take(30))
            {
                var selected = string.Equals(_state.SelectedStashInstanceKey, instance.InstanceKey, StringComparison.OrdinalIgnoreCase);
                var card = AddCard(
                    parent,
                    instance.Label,
                    $"{instance.Location} • {FormatCount(instance.Quantity)} unit(s) • {instance.ConditionPercent}% {instance.ConditionDescription} • {instance.ChildItemCount} child item(s)",
                    $"REFERENCE {Money(instance.ConditionAdjustedReferenceValue)}{(instance.FoundInRaid ? " • FIR" : string.Empty)}",
                    null,
                    selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
                RenderOwnedCopyRowTagControls(card, instance, selected);
            }
        }

        RenderTraderSummary(parent, _state.TraderSummary);
        RenderMarketSummary(parent, _state.MarketSummary);
        RenderItemUsage(parent, _state.ItemUsage);
    }

    private void RenderOwnedCopyRowTagControls(Transform parent, HermesStashInstanceSummary instance, bool selectedForPricing)
    {
        var hasTag = !string.IsNullOrWhiteSpace(instance.TagName);
        var row = CreateToolbar(parent);
        AddButton(
            row,
            selectedForPricing ? "PRICING SELECTED" : "USE FOR PRICING",
            () =>
            {
                _state!.SelectStashInstance(instance.InstanceKey);
                Invalidate(0.20f);
            },
            132f,
            !_state!.DetailLoading,
            height: 28f,
            fontSize: 11.5f,
            selected: selectedForPricing);
        AddToolbarLabel(row, FormatInstanceTag(instance).ToUpperInvariant());
        AddFlexibleSpace(row);

        if (!Plugin.Settings.EnableConfirmedActions.Value || !Plugin.Settings.AllowInventoryTagActions.Value)
        {
            AddToolbarLabel(row, "TAG EDITS DISABLED");
            return;
        }

        if (!hasTag)
        {
            AddButton(
                row,
                "+ TAG",
                () =>
                {
                    OpenRowTagEditor(instance, "apply");
                    Invalidate(0.05f);
                },
                72f,
                !(_state?.ActionLoading ?? true),
                height: 28f,
                fontSize: 11.5f,
                selected: IsRowTagEditorOpen(instance, "apply"));
        }
        else
        {
            AddButton(
                row,
                "CHANGE",
                () =>
                {
                    OpenRowTagEditor(instance, "change");
                    Invalidate(0.05f);
                },
                82f,
                !(_state?.ActionLoading ?? true),
                height: 28f,
                fontSize: 11.5f,
                selected: IsRowTagEditorOpen(instance, "change"));
            AddButton(
                row,
                "RESET",
                () =>
                {
                    _state!.ProposeInventoryTagAction("remove", string.Empty, "blue", instance.InstanceKey);
                    Invalidate(0.20f);
                },
                82f,
                !(_state?.ActionLoading ?? true),
                height: 28f,
                fontSize: 11.5f);
        }

        if (IsRowTagEditorOpen(instance, _tagEditorMode))
        {
            RenderOwnedCopyRowTagEditor(parent, instance);
        }
    }

    private void RenderOwnedCopyRowTagEditor(Transform parent, HermesStashInstanceSummary instance)
    {
        var editor = CreateToolbar(parent);
        AddToolbarLabel(editor, _tagEditorMode == "change" ? "NEW TAG" : "TAG");
        Button? previewButton = null;
        var nameInput = AddInput(editor, "Tag name", _tagEditorDraftName, 180f);
        nameInput.onValueChanged.AddListener(value =>
        {
            _tagEditorDraftName = value;
            if (previewButton != null)
            {
                previewButton.interactable = CanProposeInventoryTagActionForInstance(_state!, instance, _tagEditorMode, _tagEditorDraftName);
            }
        });
        AddRowTagColorDropdown(editor, true);
        previewButton = AddButton(
            editor,
            "PREVIEW",
            () =>
            {
                _state!.ProposeInventoryTagAction(_tagEditorMode, _tagEditorDraftName, _tagEditorDraftColor, instance.InstanceKey);
                Invalidate(0.20f);
            },
            90f,
            CanProposeInventoryTagActionForInstance(_state!, instance, _tagEditorMode, _tagEditorDraftName),
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            editor,
            "CANCEL",
            () =>
            {
                _tagEditorInstanceKey = string.Empty;
                _tagColorDropdownOpen = false;
                Invalidate(0.05f);
            },
            82f,
            height: 28f,
            fontSize: 11.5f);
        AddFlexibleSpace(editor);

        if (_tagColorDropdownOpen)
        {
            RenderRowTagColorOptions(parent);
        }
    }

    private void OpenRowTagEditor(HermesStashInstanceSummary instance, string mode)
    {
        _tagEditorInstanceKey = instance.InstanceKey;
        _tagEditorMode = NormalizeTagActionMode(mode);
        _tagColorDropdownOpen = false;
        _state!.TagActionMode = _tagEditorMode;
        if (_tagEditorMode == "change")
        {
            _tagEditorDraftName = instance.TagName ?? string.Empty;
            _tagEditorDraftColor = string.IsNullOrWhiteSpace(instance.TagColor) ? "blue" : instance.TagColor;
        }
        else
        {
            _tagEditorDraftName = string.Empty;
            _tagEditorDraftColor = "blue";
        }
    }

    private bool IsRowTagEditorOpen(HermesStashInstanceSummary instance, string mode)
        => string.Equals(_tagEditorInstanceKey, instance.InstanceKey, StringComparison.OrdinalIgnoreCase)
           && string.Equals(_tagEditorMode, NormalizeTagActionMode(mode), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTagActionMode(string? mode)
        => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "change" => "change",
            "remove" => "remove",
            _ => "apply"
        };

    private void RenderTraderSummary(Transform parent, HermesTraderSummaryResponse? summary)
    {
        if (summary is null)
        {
            AddSectionHeader(parent, "TRADERS");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading trader data..." : "Trader data unavailable.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddSectionHeader(parent, "TRADERS");
            AddEmptyState(parent, "No trader analysis.", summary.Message ?? string.Empty);
            return;
        }

        var bestSale = summary.BestSellOffer;
        var bestPurchase = summary.PurchaseOffers
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

        var saleSummary = bestSale is null
            ? "Sell: no supported trader buyer"
            : $"Sell: {bestSale.TraderName} for {Money(bestSale.RoubleEquivalent)}";
        var purchaseSummary = bestPurchase is null
            ? "Buy: no currently available trader offer"
            : $"Buy: {bestPurchase.TraderName} for {bestPurchase.DisplayPrice} (about {Money(bestPurchase.EstimatedRoubleValue)})";
        var traderMeta = (HasUsefulTraderInfo(summary)
                ? $"{summary.SellOffers.Count:N0} sale option(s) • {summary.PurchaseOffers.Count:N0} purchase offer(s)"
                : "NO CURRENT TRADER OFFER")
                         + (summary.UsesSelectedStashInstance ? " • SELECTED OWNED COPY" : " • BASE ITEM");

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "traders",
            "TRADERS",
            $"{saleSummary}\n{purchaseSummary}",
            traderMeta,
            Plugin.Settings.ExpandTraderComparisonByDefault.Value && HasUsefulTraderInfo(summary));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("BEST SALE", bestSale is null ? "NO BUYER" : $"{bestSale.TraderName} • {Money(bestSale.RoubleEquivalent)}"),
            ("BASE REFERENCE", Money(summary.ReferencePrice)),
            ("PURCHASE OFFERS", summary.PurchaseOffers.Count.ToString("N0")));

        AddCard(
            parent,
            "SALE ESTIMATE",
            summary.SalePriceBasis,
            summary.UsesSelectedStashInstance ? "SELECTED OWNED COPY" : "FULL-CONDITION BASE ITEM");

        AddSectionHeader(parent, "SELL TO TRADERS");
        if (summary.SellOffers.Count == 0)
        {
            AddEmptyState(parent, "No supported trader buyer.", summary.Message ?? string.Empty);
        }
        foreach (var offer in summary.SellOffers.Take(12))
        {
            AddCard(parent,
                offer.TraderName,
                $"LL{offer.PlayerLoyaltyLevel} • {FormatCurrency(offer.Amount, offer.Currency)} • ₽{offer.RoubleEquivalent:N0} equivalent",
                offer.IsBest ? "BEST TRADER SALE" : "TRADER SALE");
        }

        AddSectionHeader(parent, "BUY FROM TRADERS");
        if (summary.PurchaseOffers.Count == 0)
        {
            AddEmptyState(parent, "No trader purchase offer.", "No current vanilla-trader offer was found for this item.");
        }
        foreach (var offer in summary.PurchaseOffers.Take(20))
        {
            var payment = offer.PaymentOptions
                .Where(option => option.EstimateAvailable && option.EstimatedRoubleValue > 0)
                .OrderBy(option => option.EstimatedRoubleValue)
                .FirstOrDefault()
                ?? offer.PaymentOptions.OrderBy(option => option.EstimatedRoubleValue).FirstOrDefault();
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
        if (market is null)
        {
            AddSectionHeader(parent, "FLEA MARKET");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading local flea data..." : "Market data unavailable.", string.Empty);
            return;
        }
        if (!market.Found)
        {
            AddSectionHeader(parent, "FLEA MARKET");
            AddEmptyState(parent, "No market analysis.", market.Message ?? string.Empty);
            return;
        }

        var configuredMinimum = Plugin.Settings.GetMinimumComparableFleaOffers();
        var reliable = market.MarketPriceFromActiveOffers && market.ComparableOfferCount >= configuredMinimum;
        var reliability = reliable
            ? "RELIABLE"
            : market.MarketPriceFromActiveOffers
                ? "LOW SAMPLE"
                : "REFERENCE";
        var saleSummary = !market.FleaUnlocked
            ? $"Locked until player level {market.RequiredPlayerLevel}."
            : !market.CanSellOnFlea
                ? $"Cannot list: {market.SellUnavailableReason ?? "listing unavailable"}."
                : $"Net {(market.UsesSelectedOwnedCopy ? "selected-copy" : "base-item")} sale {Money(market.EstimatedNetSale)} after fee {Money(market.EstimatedListingFee)} - suggested list {Money(market.SuggestedListPrice)}";
        var buySummary = market.LowestPrice.HasValue
            ? $"Lowest comparable offer {Money(market.LowestPrice)} • median {Money(market.MedianPrice)}"
            : "No comparable Flea offer is currently available.";
        var marketMeta = HasUsefulMarketInfo(market)
            ? $"{market.ComparableOfferCount:N0} comparable offer(s) • {reliability} • {market.MarketPriceSource}"
            : "NO USABLE MARKET VALUE";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "flea",
            "FLEA MARKET",
            $"{saleSummary}\n{buySummary}",
            marketMeta,
            Plugin.Settings.ExpandMarketByDefault.Value && HasUsefulMarketInfo(market));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("LOWEST", Money(market.LowestPrice)),
            ("MEDIAN", Money(market.MedianPrice)),
            ("SUGGESTED", Money(market.SuggestedListPrice)),
            ("NET SALE", Money(market.EstimatedNetSale)),
            ("COMPARABLE", market.ComparableOfferCount.ToString("N0")),
            ("SOURCE", market.MarketPriceSource));
        if (market.UsesSelectedOwnedCopy)
        {
            AddCard(
                parent,
                "SELECTED COPY FLEA BASIS",
                $"{market.SelectedOwnedCopyLabel} - {market.SelectedOwnedCopyLocation}\nRoot {Money(market.SelectedOwnedCopyRootValue)} - child items {Money(market.SelectedOwnedCopyChildValue)}",
                "OWNED COPY");
        }
        AddCard(parent, "BUY RECOMMENDATION", market.BuyRecommendation, market.FleaUnlocked ? "FLEA UNLOCKED" : $"UNLOCKS AT LEVEL {market.RequiredPlayerLevel}");
        AddCard(parent, "SELL RECOMMENDATION", market.SellRecommendation, market.CanSellOnFlea ? "CAN LIST" : market.SellUnavailableReason ?? "CANNOT LIST");

        AddSectionHeader(parent, "LOWEST COMPARABLE OFFERS");
        if (market.LowestOffers.Count == 0)
        {
            AddEmptyState(parent, "No comparable Flea offers.", market.MarketPriceSource);
        }
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
        if (usage is null)
        {
            AddSectionHeader(parent, "QUEST, KEY, HIDEOUT & CRAFT USES");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading profile uses..." : "Profile usage unavailable.", string.Empty);
            return;
        }
        if (!usage.Found)
        {
            AddSectionHeader(parent, "QUEST, KEY, HIDEOUT & CRAFT USES");
            AddEmptyState(parent, "No profile usage analysis.", usage.Message ?? string.Empty);
            return;
        }

        RenderQuestRequirementSection(parent, usage);
        RenderQuestKeySection(parent, usage);
        RenderHideoutAndCraftUsageSection(parent, usage);
    }

    private void RenderQuestRequirementSection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = first is null
            ? "No standard quest item requirement uses this item."
            : first.ConditionCompleted || first.QuestCompleted
                ? $"Completed use: {first.QuestName}."
                : $"{(first.IsActive ? "ACTIVE" : "FUTURE")}: {first.QuestName} • {first.ProgressText}";
        var meta = HasUsefulQuestRequirements(usage)
            ? $"{active.Count:N0} active • {remaining:N0} remaining • {completed:N0} completed • owned {FormatCount(usage.OwnedQuantity)} ({FormatCount(usage.OwnedFoundInRaidQuantity)} FIR)"
            : $"NO CURRENT QUEST REQUIREMENT • {completed:N0} completed use(s)";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "quests",
            "QUEST REQUIREMENTS",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulQuestRequirements(usage));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("OWNED", FormatCount(usage.OwnedQuantity)),
            ("OWNED FIR", FormatCount(usage.OwnedFoundInRaidQuantity)),
            ("ACTIVE", active.Count.ToString("N0")),
            ("REMAINING", remaining.ToString("N0")),
            ("COMPLETED", completed.ToString("N0")));

        if (usage.QuestUses.Count == 0)
        {
            AddEmptyState(parent, "No quest requirements.", "No player-facing item requirement was found in standard quest completion conditions.");
        }
        foreach (var quest in usage.QuestUses.Take(30))
        {
            AddCard(parent,
                quest.QuestName,
                $"{quest.ProgressText}\nRequired {FormatCount(quest.Required)}{(quest.FoundInRaidRequired ? " FIR" : string.Empty)} • owned matching {FormatCount(quest.OwnedMatchingTargets)} • this item {FormatCount(quest.OwnedSelectedItem)}",
                $"{quest.TraderName} • {quest.QuestStatus}{(quest.ConditionCompleted ? " • OBJECTIVE COMPLETE" : string.Empty)}");
        }
    }

    private void RenderQuestKeySection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = first is null
            ? "This item is not linked to a known quest-key requirement."
            : $"{(first.IsActive && !first.QuestCompleted ? "ACTIVE" : first.QuestCompleted ? "COMPLETED" : "KNOWN")}: {first.QuestName} • {first.MapName} • {first.Opens}";
        var meta = HasUsefulQuestKeyKnowledge(usage)
            ? $"{active.Count:N0} active • {remaining:N0} remaining • {completed:N0} completed"
            : $"NO REMAINING QUEST-KEY USE • {completed:N0} completed";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "quest-keys",
            "QUEST KEY KNOWLEDGE",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulQuestKeyKnowledge(usage));
        if (!expanded)
        {
            return;
        }

        if (usage.QuestKeyUses.Count == 0)
        {
            AddEmptyState(parent, "No quest-key knowledge.", "The installed key and quest databases do not associate this item with a known quest lock.");
        }
        foreach (var keyUse in usage.QuestKeyUses.Take(30))
        {
            var status = keyUse.QuestCompleted
                ? "COMPLETED"
                : keyUse.IsActive
                    ? "ACTIVE QUEST"
                    : keyUse.QuestStatus.ToUpperInvariant();
            var details = new List<string>
            {
                $"{keyUse.MapName} • opens {keyUse.Opens}"
            };
            if (!string.IsNullOrWhiteSpace(keyUse.Purpose))
            {
                details.Add(keyUse.Purpose);
            }
            if (!string.IsNullOrWhiteSpace(keyUse.Acquisition))
            {
                details.Add($"Acquisition: {keyUse.Acquisition}");
            }

            AddCard(parent,
                keyUse.QuestName,
                string.Join("\n", details),
                $"{status}{(keyUse.AcquireInRaid ? " • ACQUIRE IN RAID" : string.Empty)}");
        }
    }

    private void RenderHideoutAndCraftUsageSection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = nextUpgrade is not null
            ? $"Next upgrade: {nextUpgrade.AreaName} L{nextUpgrade.TargetLevel} • owned {FormatCount(nextUpgrade.Owned)} / {FormatCount(nextUpgrade.Required)} • missing {FormatCount(nextUpgrade.Missing)}"
            : readyCraft is not null
                ? $"Craft use: {readyCraft.StationName} L{readyCraft.RequiredStationLevel} • {readyCraft.Status}"
                : "No Hideout upgrade or player-facing recipe currently uses this item.";
        var meta = HasUsefulHideoutOrCraftInfo(usage)
            ? $"{usage.UpgradeUses.Count:N0} upgrade(s) • {usage.ProducedBy.Count:N0} produced-by recipe(s) • {usage.UsedBy.Count:N0} ingredient recipe(s)"
            : "NO CURRENT HIDEOUT OR CRAFT USE";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "hideout-crafts",
            "HIDEOUT & CRAFT USES",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulHideoutOrCraftInfo(usage));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("UPGRADES", usage.UpgradeUses.Count.ToString("N0")),
            ("PRODUCED BY", usage.ProducedBy.Count.ToString("N0")),
            ("USED BY", usage.UsedBy.Count.ToString("N0")));

        AddSectionHeader(parent, "HIDEOUT UPGRADES");
        if (usage.UpgradeUses.Count == 0)
        {
            AddEmptyState(parent, "No Hideout upgrade use.", "This item is not required by a player-facing Hideout upgrade.");
        }
        foreach (var upgrade in usage.UpgradeUses.Take(30))
        {
            AddCard(parent,
                $"{upgrade.AreaName} L{upgrade.TargetLevel}",
                $"Current L{upgrade.CurrentLevel} • owned {FormatCount(upgrade.Owned)} / {FormatCount(upgrade.Required)} • missing {FormatCount(upgrade.Missing)}"
                + (upgrade.EstimatedMissingCost.HasValue ? $" • estimated missing cost {Money(upgrade.EstimatedMissingCost)}" : string.Empty),
                $"{upgrade.Status}{(upgrade.FoundInRaidRequired ? " • FIR" : string.Empty)}");
        }

        AddSectionHeader(parent, "USED AS A CRAFT INGREDIENT");
        if (usage.UsedBy.Count == 0)
        {
            AddEmptyState(parent, "No ingredient use.", "This item is not used by a player-facing Hideout recipe.");
        }
        foreach (var craft in usage.UsedBy.Take(30))
        {
            AddCard(parent,
                craft.OutputName,
                $"{craft.StationName} L{craft.RequiredStationLevel} • requires {FormatCount(craft.ItemCount)} • owned {FormatCount(craft.Owned)} • missing {FormatCount(craft.Missing)}",
                craft.Status,
                () => _state!.Navigate("Crafts"));
        }

        AddSectionHeader(parent, "PRODUCED BY CRAFTS");
        if (usage.ProducedBy.Count == 0)
        {
            AddEmptyState(parent, "No production recipe.", "No player-facing Hideout recipe produces this item.");
        }
        foreach (var craft in usage.ProducedBy.Take(30))
        {
            AddCard(parent,
                craft.OutputName,
                $"{craft.StationName} L{craft.RequiredStationLevel} • output {craft.OutputQuantity:N0} • {FormatDuration(craft.DurationSeconds)}",
                craft.Status,
                () => _state!.Navigate("Crafts"));
        }
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

    private static bool HasUsefulQuestRequirements(HermesItemHideoutUsageResponse usage)
    {
        return usage.QuestUses.Any(quest => !quest.ConditionCompleted && !quest.QuestCompleted);
    }

    private static bool HasUsefulQuestKeyKnowledge(HermesItemHideoutUsageResponse usage)
    {
        return usage.QuestKeyUses.Any(key => !key.QuestCompleted);
    }

    private static bool HasUsefulHideoutOrCraftInfo(HermesItemHideoutUsageResponse usage)
    {
        return usage.UpgradeUses.Any(upgrade => !upgrade.IsMet && upgrade.TargetLevel > upgrade.CurrentLevel)
               || usage.ProducedBy.Count > 0
               || usage.UsedBy.Count > 0;
    }

    private bool AddItemDetailCollapsibleSection(
        Transform parent,
        string sectionId,
        string title,
        string summary,
        string meta,
        bool defaultExpanded)
    {
        var itemKey = _state?.SelectedItem?.ItemKey ?? "none";
        var key = $"{itemKey}|{sectionId}";
        if (!_itemDetailSectionExpansion.TryGetValue(key, out var expanded))
        {
            if (_itemDetailSectionExpansion.Count >= 256)
            {
                // Section state is convenience-only. Bound it so long item-search sessions do
                // not retain expansion flags for every item examined during the process lifetime.
                _itemDetailSectionExpansion.Clear();
            }

            expanded = defaultExpanded;
            _itemDetailSectionExpansion[key] = expanded;
        }

        var arrow = expanded ? "▼" : "▶";
        var actionText = expanded ? "SELECT TO COLLAPSE" : "SELECT TO EXPAND";
        AddCard(
            parent,
            $"{arrow}  {title}",
            summary,
            string.IsNullOrWhiteSpace(meta) ? actionText : $"{meta} • {actionText}",
            () =>
            {
                _itemDetailSectionExpansion[key] = !expanded;
                Invalidate(0.05f);
            },
            expanded ? new Color(0.10f, 0.12f, 0.11f, 0.86f) : null);
        return expanded;
    }

    #endregion
}
