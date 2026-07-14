using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesStashPanel
{
    private enum StashView
    {
        Overview,
        SafeToSell,
        Keep,
        Review,
        Duplicates,
        Damaged
    }

    private Vector2 _scroll;
    private HermesStashSummaryResponse? _summary;
    private bool _loading;
    private bool _requested;
    private int _requestVersion;
    private StashView _view;
    private string _status = "Open this tab to build a read-only snapshot of the active PMC stash.";

    public void Draw()
    {
        if (!_requested && !_loading)
        {
            _requested = true;
            _ = RefreshFromServerAsync(false, false);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("STASH INTELLIGENCE — ALPHA10.2");
        GUILayout.FlexibleSpace();
        GUI.enabled = !_loading;
        if (GUILayout.Button(_loading ? "Refreshing..." : "Refresh", GUILayout.Width(110f)))
        {
            _ = RefreshFromServerAsync(true, true);
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label("Exact-instance trader and flea estimates, best sale destinations, duplicate review, and damaged/depleted-item reporting.");
        GUILayout.Space(4f);
        GUILayout.Label(_status);
        GUILayout.Space(6f);

        DrawViewTabs();
        GUILayout.Space(6f);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        if (_summary is not null)
        {
            DrawSummary(_summary);
        }
        GUILayout.EndScrollView();
    }

    public async Task RefreshFromServerAsync(bool clearMarketCaches, bool force)
    {
        if (_loading && !force)
        {
            return;
        }

        var requestVersion = ++_requestVersion;
        _loading = true;
        _requested = true;
        _status = "Building stash reservations, exact-instance trader values, flea nets, duplicate groups, and condition reports...";

        try
        {
            if (clearMarketCaches)
            {
                await HermesApiClient.ClearCachesAsync();
            }

            var response = await HermesApiClient.GetStashSummaryAsync();
            if (requestVersion != _requestVersion)
            {
                return;
            }

            _summary = response;
            _status = response.Found
                ? $"Snapshot complete: {response.IndependentItemCount:N0} independent items; "
                  + $"{response.SafeToSellInstanceCount + response.SellSurplusInstanceCount:N0} sell recommendation(s); "
                  + $"{response.DuplicateGroupCount:N0} duplicate group(s); {response.DamagedOrDepletedItemCount:N0} condition warning(s)."
                : response.Message ?? "HERMES could not build the stash snapshot.";
        }
        catch (Exception ex)
        {
            if (requestVersion == _requestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Stash analysis");
            }

            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _requestVersion)
            {
                _loading = false;
            }
        }
    }

    public void Clear()
    {
        _requestVersion++;
        _loading = false;
        _requested = false;
        _summary = null;
        _scroll = Vector2.zero;
        _view = StashView.Overview;
        _status = "Open this tab to build a read-only snapshot of the active PMC stash.";
    }

    private void DrawViewTabs()
    {
        GUILayout.BeginHorizontal();
        DrawViewButton("Overview", StashView.Overview, 100f);
        DrawViewButton("Safe to Sell", StashView.SafeToSell, 115f);
        DrawViewButton("Keep", StashView.Keep, 80f);
        DrawViewButton("Review", StashView.Review, 85f);
        DrawViewButton("Duplicates", StashView.Duplicates, 100f);
        DrawViewButton("Damaged", StashView.Damaged, 95f);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawViewButton(string label, StashView view, float width)
    {
        GUI.enabled = _view != view;
        if (GUILayout.Button(label, GUILayout.Width(width)))
        {
            _view = view;
            _scroll = Vector2.zero;
        }

        GUI.enabled = true;
    }

    private void DrawSummary(HermesStashSummaryResponse summary)
    {
        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Stash analysis is unavailable.");
            return;
        }

        switch (_view)
        {
            case StashView.SafeToSell:
                DrawRecommendationList(
                    summary,
                    "SAFE TO SELL AND SELLABLE SURPLUS",
                    summary.Recommendations.Where(item =>
                        item.Recommendation is "Safe to sell" or "Sell surplus"));
                break;
            case StashView.Keep:
                DrawRecommendationList(
                    summary,
                    "RESERVED / KEEP",
                    summary.Recommendations.Where(item => item.Recommendation == "Keep"));
                break;
            case StashView.Review:
                DrawRecommendationList(
                    summary,
                    "MANUAL REVIEW",
                    summary.Recommendations.Where(item => item.Recommendation == "Review"));
                break;
            case StashView.Duplicates:
                DrawDuplicates(summary);
                break;
            case StashView.Damaged:
                DrawDamaged(summary);
                break;
            default:
                DrawOverview(summary);
                break;
        }

        var generated = DateTimeOffset.FromUnixTimeSeconds(summary.GeneratedUnixTime).ToLocalTime();
        GUILayout.Space(8f);
        GUILayout.Label($"Snapshot generated {generated:yyyy-MM-dd HH:mm:ss} • server cache {summary.CacheTtlSeconds}s");
    }

    private static void DrawOverview(HermesStashSummaryResponse summary)
    {
        GUILayout.BeginHorizontal();
        DrawMetric("ITEM INSTANCES", summary.TotalItemInstances.ToString("N0"), "Includes installed parts and loaded ammunition.");
        DrawMetric("INDEPENDENT ITEMS", summary.IndependentItemCount.ToString("N0"), "Standalone stacks and sellable assemblies.");
        DrawMetric("VALUED ITEMS", summary.ValuedIndependentItemCount.ToString("N0"), $"Unsupported: {summary.UnsupportedIndependentItemCount:N0}");
        DrawMetric("OCCUPIED CELLS", summary.OccupiedCells.ToString("N0"), "Template footprint estimate; installed parts add no cells.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("RECOMMENDATIONS");
        GUILayout.BeginHorizontal();
        DrawMetric("SAFE TO SELL", summary.SafeToSellInstanceCount.ToString("N0"), "Reliable trader or flea destination; no reservation warning.");
        DrawMetric("SELL SURPLUS", summary.SellSurplusInstanceCount.ToString("N0"), "Keep reserved quantity; sell only the surplus.");
        DrawMetric("KEEP", summary.KeepInstanceCount.ToString("N0"), $"Recommended keep quantity: {FormatNumber(summary.RecommendedKeepQuantity)}");
        DrawMetric("REVIEW", summary.ReviewInstanceCount.ToString("N0"), "Built, filled, or weakly priced items.");
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        DrawMetric("DUPLICATE GROUPS", summary.DuplicateGroupCount.ToString("N0"), "Exact-template duplicate review; advisory only.");
        DrawMetric("CONDITION WARNINGS", summary.DamagedOrDepletedItemCount.ToString("N0"), "Low durability, low resources, or one-use keys.");
        DrawMetric("FLEA VALUED", summary.FleaValuedItemCount.ToString("N0"), $"Unavailable: {summary.NoFleaEstimateItemCount:N0}");
        DrawMetric("TRADER VALUED", summary.TraderValuedItemCount.ToString("N0"), $"No buyer: {summary.NoTraderBuyerItemCount:N0}");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("RECOMMENDED SELLABLE QUANTITY");
        GUILayout.BeginHorizontal();
        DrawValueCard(
            "Best-destination value",
            summary.PotentialBestSaleValue,
            $"{FormatNumber(summary.PotentiallySellQuantity)} recommended unit(s); reservations and review items excluded.");
        DrawValueCard("Trader alternative", summary.PotentialTraderSaleValue, "Best supported trader value for the same sellable quantity.");
        DrawValueCard("Flea net alternative", summary.PotentialFleaNetValue, "After estimated listing fees where a flea estimate was available.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("COMPLETE STASH VALUE");
        GUILayout.BeginHorizontal();
        DrawValueCard("Best trader liquidation", summary.BestTraderLiquidationValue, "Complete valued stash before reservations.");
        DrawValueCard("Estimated flea net", summary.EstimatedFleaNetValue, "Fee-backed estimates; not every item has a usable flea market.");
        DrawValueCard("Best reliable destination", summary.BestDestinationLiquidationValue, "Reliable flea estimate or best trader, selected per item.");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        DrawValueCard("Full handbook reference", summary.FullHandbookReferenceValue, "Full-condition reference before wear/resource adjustment.");
        DrawValueCard("Condition-adjusted handbook", summary.ConditionAdjustedHandbookValue, "Exact durability, resources, stacks, attachments, and armor inserts.");
        GUILayout.EndHorizontal();

        if (!string.IsNullOrWhiteSpace(summary.Message))
        {
            GUILayout.Space(6f);
            GUILayout.Label(summary.Message);
        }

        GUILayout.Space(8f);
        GUILayout.Label("BEST SALE DESTINATIONS");
        if (summary.SaleDestinationBreakdown.Count == 0)
        {
            GUILayout.Label("No reliable sale destination was available.");
        }
        else
        {
            foreach (var destination in summary.SaleDestinationBreakdown)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(destination.Destination, GUILayout.Width(180f));
                GUILayout.Label($"{destination.ItemCount:N0} item(s)", GUILayout.Width(120f));
                GUILayout.FlexibleSpace();
                GUILayout.Label($"₽{destination.RoubleEquivalent:N0}");
                GUILayout.EndHorizontal();
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("MOST VALUABLE ITEMS");
        if (summary.MostValuableItems.Count == 0)
        {
            GUILayout.Label("No valued stash items were found.");
        }
        else
        {
            foreach (var item in summary.MostValuableItems)
            {
                DrawItemRow(item);
            }
        }
    }

    private static void DrawRecommendationList(
        HermesStashSummaryResponse summary,
        string heading,
        IEnumerable<HermesStashValuationItem> items)
    {
        var rows = items.ToList();
        GUILayout.Label(heading);
        GUILayout.Label(
            "Reservations are allocated in this order: active quests, next hideout upgrade, future quests, then later hideout stages. "
            + "A flea destination is selected automatically only when at least three comparable offers support the estimate.");
        GUILayout.Space(5f);

        if (!string.IsNullOrWhiteSpace(summary.Message))
        {
            GUILayout.Label(summary.Message);
            GUILayout.Space(5f);
        }

        if (rows.Count == 0)
        {
            GUILayout.Label("No stash items match this view.");
            return;
        }

        foreach (var item in rows)
        {
            DrawItemRow(item);
        }
    }

    private static void DrawDuplicates(HermesStashSummaryResponse summary)
    {
        GUILayout.Label("DUPLICATE REVIEW");
        GUILayout.Label(
            "Exact-template duplicates only. Quest and hideout reservations remain authoritative. "
            + "When no explicit reserve exists, this advisory view keeps one baseline instance and reports the remainder as possible excess.");
        GUILayout.Space(5f);

        if (summary.DuplicateGroups.Count == 0)
        {
            GUILayout.Label("No duplicate groups with potential excess were found.");
            return;
        }

        foreach (var group in summary.DuplicateGroups)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(group.Name);
            GUILayout.FlexibleSpace();
            GUILayout.Label(group.PotentialExcessSaleValue > 0
                ? $"Potential value: ₽{group.PotentialExcessSaleValue:N0}"
                : "Value unavailable");
            GUILayout.EndHorizontal();
            GUILayout.Label(
                $"Instances: {group.InstanceCount:N0} • Owned: {FormatNumber(group.OwnedQuantity)} • "
                + $"Suggested reserve: {FormatNumber(group.SuggestedReserveQuantity)} • "
                + $"Potential excess: {FormatNumber(group.PotentialExcessQuantity)}");
            GUILayout.Label(
                $"Explicit quest/hideout reserve: {FormatNumber(group.ExplicitlyReservedQuantity)} • "
                + $"Occupied cells: {group.OccupiedCells:N0} • "
                + $"Best estimated destination: {group.BestSaleDestination ?? "Review manually"}");
            GUILayout.Label(group.Note);
            GUILayout.EndVertical();
        }
    }

    private static void DrawDamaged(HermesStashSummaryResponse summary)
    {
        GUILayout.Label("DAMAGED AND DEPLETED ITEMS");
        GUILayout.Label(
            "Thresholds: weapons below 70%, armor and generic durability below 50%, resources below 20%, and keys with one use remaining.");
        GUILayout.Space(5f);

        if (summary.DamagedOrDepletedItems.Count == 0)
        {
            GUILayout.Label("No damaged or depleted items crossed the current Alpha10.2 thresholds.");
            return;
        }

        foreach (var item in summary.DamagedOrDepletedItems)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{item.Status.ToUpperInvariant()} — {item.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{item.ConditionPercent:N0}%");
            GUILayout.EndHorizontal();
            GUILayout.Label(item.InstanceLabel);
            GUILayout.Label(
                $"{item.ConditionKind}: {FormatNumber(item.ConditionCurrent)}/{FormatNumber(item.ConditionMaximum)} • "
                + $"Report threshold: {item.ThresholdPercent:N0}%");
            if (item.BestSaleValue.HasValue)
            {
                GUILayout.Label($"Best estimated destination: {item.BestSaleDestination} • ₽{item.BestSaleValue.Value:N0}");
            }
            else
            {
                GUILayout.Label("Best estimated destination: unavailable");
            }
            GUILayout.Label(item.Recommendation);
            GUILayout.EndVertical();
        }
    }

    private static void DrawMetric(string label, string value, string note)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true));
        GUILayout.Label(label);
        GUILayout.Label(value);
        GUILayout.Label(note);
        GUILayout.EndVertical();
    }

    private static void DrawValueCard(string label, long value, string note)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(250f), GUILayout.ExpandWidth(true));
        GUILayout.Label(label);
        GUILayout.Label($"₽{value:N0}");
        GUILayout.Label(note);
        GUILayout.EndVertical();
    }

    private static void DrawItemRow(HermesStashValuationItem item)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{item.Recommendation.ToUpperInvariant()} — {item.Name}");
        GUILayout.FlexibleSpace();
        GUILayout.Label(item.BestSaleValue.HasValue
            ? $"Best: {item.BestSaleDestination} • ₽{item.BestSaleValue.Value:N0}"
            : "No reliable sale destination");
        GUILayout.EndHorizontal();

        GUILayout.Label(item.InstanceLabel);
        GUILayout.Label(
            $"Owned in this instance: {FormatNumber(item.Quantity)} • "
            + $"Keep: {FormatNumber(item.RecommendedKeepQuantity)} • "
            + $"Potentially sell: {FormatNumber(item.PotentiallySellQuantity)}");

        if (item.PotentialBestSaleValue > 0)
        {
            GUILayout.Label($"Estimated best-destination value of sellable quantity: ₽{item.PotentialBestSaleValue:N0}");
        }

        var saleOptions = new List<string>();
        if (item.BestTraderValue.HasValue)
        {
            saleOptions.Add($"{item.BestTraderName} ₽{item.BestTraderValue.Value:N0}");
        }
        if (item.FleaEstimateAvailable && item.EstimatedFleaNetValue.HasValue)
        {
            var reliability = item.FleaEstimateReliable ? "reliable" : "informational";
            saleOptions.Add($"flea net ₽{item.EstimatedFleaNetValue.Value:N0} ({item.FleaComparableOfferCount:N0} offers, {reliability})");
        }
        if (saleOptions.Count > 0)
        {
            GUILayout.Label("Sale options: " + string.Join(" • ", saleOptions));
        }

        if (item.FleaEstimateAvailable && item.EstimatedFleaListPrice.HasValue)
        {
            GUILayout.Label(
                $"Flea listing estimate: ₽{item.EstimatedFleaListPrice.Value:N0} • "
                + $"fee ₽{item.EstimatedFleaFee.GetValueOrDefault():N0} • source: {item.FleaEstimateSource}");
        }

        var reserves = new List<string>();
        if (item.ActiveQuestReserve > 0d)
        {
            reserves.Add($"active quests {FormatNumber(item.ActiveQuestReserve)}");
        }
        if (item.NextHideoutReserve > 0d)
        {
            reserves.Add($"next hideout {FormatNumber(item.NextHideoutReserve)}");
        }
        if (item.FutureQuestReserve > 0d)
        {
            reserves.Add($"future quests {FormatNumber(item.FutureQuestReserve)}");
        }
        if (item.FutureHideoutReserve > 0d)
        {
            reserves.Add($"future hideout {FormatNumber(item.FutureHideoutReserve)}");
        }
        if (reserves.Count > 0)
        {
            GUILayout.Label("Reserved for: " + string.Join(" • ", reserves));
        }

        GUILayout.Label(
            $"Condition-adjusted handbook: ₽{item.ConditionAdjustedHandbookValue:N0} • "
            + $"Full reference: ₽{item.FullHandbookReferenceValue:N0} • "
            + $"Cells: {item.OccupiedCells:N0} • Installed: {item.InstalledItemCount:N0} • "
            + $"Contained: {item.ContainedItemCount:N0}");

        foreach (var reason in item.Reasons)
        {
            GUILayout.Label("• " + reason);
        }

        GUILayout.EndVertical();
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }
}
