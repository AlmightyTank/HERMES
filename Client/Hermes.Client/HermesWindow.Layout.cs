using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Layout

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
        GUILayout.Label("CONFIRMED TAG ACTIONS");
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

    private void DrawInventoryBody(Rect rect)
    {
        GUI.BeginGroup(rect);
        try
        {
            GUILayout.BeginArea(new Rect(0f, 0f, rect.width, rect.height));
            DrawActiveTabContent();
            DrawActionConfirmationPopout(rect);
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
                _assistantPanel.Draw(
                    GetAssistantSelectedItem(),
                    GetAssistantSelectedInstanceKey(),
                    NavigateToTab,
                    ClearAssistantContext);
                break;
            case HermesTab.Hideout:
                _hideoutPanel.Draw();
                break;
            case HermesTab.Actions:
                DrawItemSearchTab();
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

    #endregion
}
