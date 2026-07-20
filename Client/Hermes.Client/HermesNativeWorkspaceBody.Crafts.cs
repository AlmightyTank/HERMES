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
    #region Crafts

    private void ApplyPendingCraftFocus()
    {
        var focus = HermesNativeCraftFocus.Read();
        if (focus.Revision == _craftFocusRevision)
        {
            return;
        }

        _craftFocusRevision = focus.Revision;
        _craftView = "RECIPES";
        _craftSearch = focus.DisplayName;
        _craftFilter = "ALL";
        _craftAvailableOnly = false;
        _craftFocusKeys.Clear();
        foreach (var key in focus.CraftKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _craftFocusKeys.Add(key);
            }
        }

        _savedScrollPositions["craft-list"] = 1f;
        _scrollsToForceTop.Add("craft-list");
        _forceRebuild = true;
    }

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

        var completedProductionKeys = response.Crafts
            .Where(craft => craft.IsComplete && !string.IsNullOrWhiteSpace(craft.ProductionKey))
            .Select(craft => craft.ProductionKey)
            .ToArray();

        var viewToolbar = CreateToolbar(root);
        AddToolbarLabel(viewToolbar, "CRAFTS & HIDEOUT");
        AddButton(viewToolbar, "NOW", () => SetCraftView("NOW"), 74f, selected: _craftView == "NOW");
        AddButton(viewToolbar, "RECIPES", () => SetCraftView("RECIPES"), 92f, selected: _craftView == "RECIPES");
        AddFlexibleSpace(viewToolbar);
        AddButton(
            viewToolbar,
            "COLLECT ALL",
            () => _state!.ProposeCraftCollectAction(collectAllCompleted: true),
            118f,
            Plugin.Settings.EnableConfirmedActions.Value
            && Plugin.Settings.AllowCraftActions.Value
            && completedProductionKeys.Length > 0);

        AddMetricGrid(root,
            ("TOTAL", response.Crafts.Count.ToString("N0")),
            ("AVAILABLE", response.Crafts.Count(craft => craft.StationLevelMet).ToString("N0")),
            ("READY", response.Crafts.Count(craft => craft.IsComplete).ToString("N0")),
            ("PROFITABLE", response.Crafts.Count(IsCraftProfitable).ToString("N0")),
            ("IN HIDEOUT", response.Crafts.Count(craft => craft.IsActive || craft.IsComplete).ToString("N0")),
            ("STARTABLE", response.Crafts.Count(craft => craft.CanStartNow).ToString("N0")));

        if (_craftView == "RECIPES")
        {
            RenderCraftRecipeBrowser(root, response);
        }
        else
        {
            RenderCraftNowDashboard(root, response);
        }
    }

    private void RenderCraftRecipeBrowser(Transform root, HermesCraftsResponse response)
    {
        var toolbar = CreateToolbar(root);
        AddToolbarLabel(toolbar, "RECIPES");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER RECIPES", _craftSearch, 240f);
        search.onEndEdit.AddListener(value =>
        {
            _craftSearch = value.Trim();
            _craftFocusKeys.Clear();
            Invalidate();
        });
        AddButton(toolbar, "READY", () => SetCraftFilter("READY"), 82f, selected: _craftFilter == "READY");
        AddButton(toolbar, "PROFITABLE", () => SetCraftFilter("PROFITABLE"), 104f, selected: _craftFilter == "PROFITABLE");
        AddButton(toolbar, "ACTIVE", () => SetCraftFilter("ACTIVE"), 82f, selected: _craftFilter == "ACTIVE");
        AddButton(toolbar, "ALL", () => SetCraftFilter("ALL"), 68f, selected: _craftFilter == "ALL");
        AddCheckbox(toolbar, "AVAILABLE", _craftAvailableOnly, value =>
        {
            _craftAvailableOnly = value;
            Invalidate();
        }, 118f);

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
            var selected = string.Equals(_state!.SelectedCraft?.CraftKey, craft.CraftKey, StringComparison.OrdinalIgnoreCase);
            var canShowDetails = !IsProductionOnlyCraft(craft);
            AddCard(
                list.Content,
                $"{craft.OutputQuantity:N0}× {craft.OutputName}",
                $"{craft.StationName} • YOUR L{craft.CurrentStationLevel} / REQ L{craft.RequiredStationLevel} • {FormatDuration(craft.DurationSeconds)} • input {Money(craft.EstimatedEconomicInputValue)} • best sale {Money(craft.EstimatedBestSaleValue)}",
                $"{craft.Status} • BEST PROFIT {Money(BestCraftProfit(craft))} • {CraftProfitSource(craft)}",
                canShowDetails ? () =>
                {
                    _state.SelectCraft(craft);
                    Invalidate(0.20f);
                } : null,
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }
        if (crafts.Count == 0)
        {
            AddEmptyState(list.Content, "No recipes match the filter.", "Uninstalled-station recipes are removed server-side before reaching this list.");
        }

        RenderCraftDetails(details.Content);
    }

    private void RenderCraftNowDashboard(Transform root, HermesCraftsResponse response)
    {
        var split = HermesNativeUiFramework.CreateSplitView(root, 430f);
        var splitLayout = split.Root.gameObject.AddComponent<LayoutElement>();
        splitLayout.flexibleHeight = 1f;
        splitLayout.flexibleWidth = 1f;
        AddVerticalLayout(split.Left, 5, 5, 5, 5, 2f);
        AddVerticalLayout(split.Right, 5, 5, 5, 5, 2f);
        var actions = CreateScroll(split.Left, "craft-now-actions", true);
        var planning = CreateScroll(split.Right, "craft-now-planning", true);

        var ready = response.Crafts
            .Where(craft => craft.IsComplete)
            .OrderBy(craft => craft.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRowsPerSection)
            .ToList();
        AddSectionHeader(actions.Content, $"READY TO COLLECT  {ready.Count:N0}");
        foreach (var craft in ready)
        {
            AddCraftNowCard(actions.Content, craft, "READY", allowCollect: true);
        }
        if (ready.Count == 0)
        {
            AddEmptyState(actions.Content, "Nothing is ready to collect.", "Completed regular Hideout crafts will appear here with Collect actions.");
        }

        var producing = response.Crafts
            .Where(craft => craft.IsActive && !craft.IsComplete)
            .OrderBy(craft => craft.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRowsPerSection)
            .ToList();
        AddSectionHeader(actions.Content, $"PRODUCING  {producing.Count:N0}");
        foreach (var craft in producing)
        {
            AddCraftNowCard(actions.Content, craft, "PRODUCING", allowCollect: false);
        }
        if (producing.Count == 0)
        {
            AddEmptyState(actions.Content, "No regular craft is currently producing.", "Started productions will appear here until completed or collected.");
        }

        var startable = response.Crafts
            .Where(craft => craft.CanStartNow && !craft.IsActive && !craft.IsComplete)
            .OrderByDescending(BestCraftProfit)
            .ThenByDescending(BestCraftProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(MaximumRowsPerSection, 12))
            .ToList();
        AddSectionHeader(planning.Content, $"STARTABLE  {startable.Count:N0}");
        foreach (var craft in startable)
        {
            AddCraftNowCard(planning.Content, craft, "STARTABLE", allowCollect: false);
        }
        if (startable.Count == 0)
        {
            AddEmptyState(planning.Content, "No startable craft is available.", "Unlocked recipes with all ingredients and a free station will appear here.");
        }

        var blocked = response.Crafts
            .Where(craft => !craft.IsComplete && !craft.IsActive && !craft.CanStartNow)
            .OrderByDescending(craft => craft.StationLevelMet)
            .ThenBy(craft => craft.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(MaximumRowsPerSection, 12))
            .ToList();
        AddSectionHeader(planning.Content, $"BLOCKED / WAITING  {blocked.Count:N0}");
        foreach (var craft in blocked)
        {
            AddCraftNowCard(planning.Content, craft, "BLOCKED", allowCollect: false);
        }
        if (blocked.Count == 0)
        {
            AddEmptyState(planning.Content, "No blocked craft surfaced.", "Missing ingredients, station locks, quest locks, and busy stations are tracked here.");
        }
    }

    private void AddCraftNowCard(
        Transform parent,
        HermesCraftSummary craft,
        string stateLabel,
        bool allowCollect)
    {
        var card = AddCard(
            parent,
            $"{craft.OutputQuantity:N0}x {craft.OutputName}",
            $"{craft.StationName} - your L{craft.CurrentStationLevel} / required L{craft.RequiredStationLevel} - {craft.Status}",
            $"{stateLabel} - best profit {Money(BestCraftProfit(craft))} - {CraftProfitSource(craft)}",
            color: craft.IsComplete
                ? new Color(0.15f, 0.23f, 0.18f, 0.86f)
                : craft.IsActive
                    ? new Color(0.20f, 0.20f, 0.15f, 0.82f)
                    : null);
        var toolbar = CreateToolbar(card);
        if (!IsProductionOnlyCraft(craft))
        {
            AddButton(toolbar, "DETAILS", () =>
            {
                _craftView = "RECIPES";
                _state!.SelectCraft(craft);
                Invalidate(0.20f);
            }, 92f);
        }
        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId))
        {
            AddButton(toolbar, "ASK", () => _state!.Window.OpenForPreviewItem(craft.OutputTemplateId!, "Craft output"), 70f);
        }
        if (allowCollect && !string.IsNullOrWhiteSpace(craft.ProductionKey))
        {
            AddButton(
                toolbar,
                "COLLECT",
                () => _state!.ProposeCraftCollectAction(false, craft.ProductionKey),
                96f,
                Plugin.Settings.EnableConfirmedActions.Value && Plugin.Settings.AllowCraftActions.Value);
        }
        AddFlexibleSpace(toolbar);
    }

    private static bool IsProductionOnlyCraft(HermesCraftSummary craft)
        => craft.CraftKey.StartsWith("production:", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<HermesCraftSummary> FilterCrafts(IEnumerable<HermesCraftSummary> crafts)
    {
        var query = _craftSearch.Trim();
        var filtered = crafts
            .Where(craft => _craftFocusKeys.Count == 0 || _craftFocusKeys.Contains(craft.CraftKey))
            .Where(craft => _craftFocusKeys.Count > 0
                            || query.Length == 0
                            || craft.OutputName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.StationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            // The four buttons are the primary status filter. Available is only an
            // additional narrowing condition and never replaces or resets that filter.
            .Where(craft => _craftFilter switch
            {
                "READY" => craft.IsComplete,
                "PROFITABLE" => IsCraftProfitable(craft),
                "ACTIVE" => craft.IsActive || craft.IsComplete,
                _ => true
            })
            .Where(MatchesCraftAvailabilityFilter);

        if (_craftFilter == "PROFITABLE")
        {
            return filtered
                .OrderByDescending(BestCraftProfit)
                .ThenByDescending(BestCraftProfitPerHour)
                .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
        }

        return filtered
            .OrderByDescending(craft => craft.IsComplete)
            .ThenByDescending(craft => craft.IsActive || craft.IsComplete)
            .ThenByDescending(BestCraftProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesCraftAvailabilityFilter(HermesCraftSummary craft)
    {
        if (!_craftAvailableOnly)
        {
            return true;
        }

        // AVAILABLE follows the current profile's station level. Ingredients, station
        // occupancy, and collection/start state belong to status filters and must not hide a recipe
        // that the player's current station level can access.
        return craft.StationLevelMet || craft.IsActive || craft.IsComplete;
    }

    private static bool IsCraftProfitable(HermesCraftSummary craft)
    {
        var minimumProfit = Plugin.Settings.GetMinimumCraftProfit();
        var minimumPercent = Plugin.Settings.GetMinimumCraftProfitPercent();
        var percent = craft.EstimatedEconomicInputValue <= 0L
            ? (craft.EstimatedBestSaleProfit > 0L ? 100d : 0d)
            : craft.EstimatedBestSaleProfit * 100d / craft.EstimatedEconomicInputValue;
        return craft.EstimatedBestSaleProfit >= minimumProfit && percent >= minimumPercent;
    }

    private static long BestCraftProfit(HermesCraftSummary craft)
        => craft.EstimatedBestSaleProfit;

    private static long BestCraftProfitPerHour(HermesCraftSummary craft)
        => craft.EstimatedBestSaleProfitPerHour;

    private static string CraftProfitSource(HermesCraftSummary craft)
    {
        if (string.Equals(craft.BestSaleSource, "Flea Market", StringComparison.OrdinalIgnoreCase))
        {
            return "SELL ON FLEA";
        }

        return string.IsNullOrWhiteSpace(craft.BestSaleSource)
               || string.Equals(craft.BestSaleSource, "No available buyer", StringComparison.OrdinalIgnoreCase)
            ? "NO AVAILABLE BUYER"
            : $"SELL TO {craft.BestSaleSource.ToUpperInvariant()}";
    }

    private static string CraftFleaSaleText(HermesCraftSummary craft)
    {
        if (!craft.FleaUnlocked)
        {
            return "LOCKED";
        }

        return craft.CanSellOnFlea && craft.EstimatedFleaNetSaleValue > 0
            ? Money(craft.EstimatedFleaNetSaleValue)
            : "UNAVAILABLE";
    }

    private static string CraftFleaProfitText(HermesCraftSummary craft)
    {
        if (!craft.FleaUnlocked)
        {
            return "LOCKED";
        }

        return craft.CanSellOnFlea && craft.EstimatedFleaNetSaleValue > 0
            ? Money(craft.EstimatedFleaProfit)
            : "UNAVAILABLE";
    }

    private void SetCraftFilter(string filter)
    {
        if (string.Equals(_craftFilter, filter, StringComparison.Ordinal))
        {
            return;
        }

        _craftFilter = filter;
        Invalidate();
    }

    private void SetCraftView(string view)
    {
        var normalized = string.Equals(view, "RECIPES", StringComparison.OrdinalIgnoreCase)
            ? "RECIPES"
            : "NOW";
        if (string.Equals(_craftView, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _craftView = normalized;
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
            ("STATION", $"{craft.StationName} • your L{craft.CurrentStationLevel} / required L{craft.RequiredStationLevel}"),
            ("OUTPUT", craft.OutputQuantity.ToString("N0")),
            ("DURATION", FormatDuration(craft.DurationSeconds)),
            ("STATUS", craft.Status),
            ("ECONOMIC INPUT", Money(craft.EstimatedEconomicInputValue)),
            ("TRADER SALE", Money(craft.EstimatedTraderSaleValue)),
            ("TRADER PROFIT", Money(craft.EstimatedTraderProfit)),
            ("FLEA NET", CraftFleaSaleText(craft)),
            ("FLEA PROFIT", CraftFleaProfitText(craft)),
            ("BEST SALE", Money(craft.EstimatedBestSaleValue)),
            ("BEST PROFIT", Money(craft.EstimatedBestSaleProfit)),
            ("BEST SELL PATH", CraftProfitSource(craft)),
            ("BEST PROFIT / HOUR", Money(BestCraftProfitPerHour(craft))),
            ("PLAN", craft.AcquisitionPlanComplete ? "COMPLETE" : "INCOMPLETE"));

        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId)
            || (craft.IsComplete && !string.IsNullOrWhiteSpace(craft.ProductionKey)))
        {
            var toolbar = CreateToolbar(parent);
            if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId))
            {
                AddButton(toolbar, "ASK HERMES ABOUT OUTPUT", () => _state.Window.OpenForPreviewItem(craft.OutputTemplateId!, "Craft output"), 210f);
            }
            if (craft.IsComplete && !string.IsNullOrWhiteSpace(craft.ProductionKey))
            {
                AddButton(
                    toolbar,
                    "COLLECT",
                    () => _state.ProposeCraftCollectAction(false, craft.ProductionKey),
                    104f,
                    Plugin.Settings.EnableConfirmedActions.Value && Plugin.Settings.AllowCraftActions.Value);
            }
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
}
