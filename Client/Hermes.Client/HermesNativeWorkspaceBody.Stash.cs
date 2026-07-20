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
                        () => _state!.Window.OpenForStashItem(item.InstanceKey, item.Name));
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
                () => _state!.Window.OpenForStashItem(item.InstanceKey, item.Name));
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
}
