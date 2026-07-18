using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesStashPanel
{
    private enum StashView
    {
        Overview,
        SafeToSell,
        Cleanup,
        Keep,
        Review,
        Duplicates,
        Damaged
    }

    private enum StashSort
    {
        Recommendation,
        Name,
        SellValue,
        SellableQuantity,
        ValuePerCell,
        OccupiedCells,
        Condition,
        Destination,
        ReservedQuantity
    }

    private enum FoundInRaidFilter
    {
        All,
        FoundInRaid,
        NotFoundInRaid
    }

    private Vector2 _scroll;
    private HermesStashSummaryResponse? _summary;
    private bool _loading;
    private bool _requested;
    private bool _defaultsLoaded;
    private int _requestVersion;
    private StashView _view;
    private StashSort _sort;
    private FoundInRaidFilter _foundInRaidFilter;
    private string _textFilter = string.Empty;
    private string _categoryFilter = "All";
    private string _destinationFilter = "All";
    private string _status = "Open this tab to build a read-only snapshot of the active PMC stash.";

    public void Draw()
    {
        EnsureDefaults();
        if (!_requested && !_loading)
        {
            _requested = true;
            _ = RefreshFromServerAsync(false, false);
        }

        HermesUi.DrawPanelHeader(
            "STASH INTELLIGENCE",
            "Exact-instance sale intelligence, configurable reservations, space recovery, duplicates, and condition reports.",
            _status,
            _loading,
            () => _ = RefreshFromServerAsync(true, true));

        DrawViewTabs();
        GUILayout.Space(HermesUi.SmallSpace);

        if (_summary is { Found: true } summary)
        {
            DrawPinnedSummary(summary);
            if (_view is not StashView.Overview)
            {
                DrawFilterToolbar(summary);
            }
        }

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
        var settings = Plugin.Settings.CreateStashRequestSettings();
        _loading = true;
        _requested = true;
        _status = "Building reservations, exact sale values, cleanup candidates, duplicate groups, and condition reports...";

        try
        {
            if (clearMarketCaches)
            {
                await HermesApiClient.ClearCachesAsync();
            }

            var response = await HermesApiClient.GetStashSummaryAsync(settings);
            if (requestVersion != _requestVersion)
            {
                return;
            }

            _summary = response;
            _status = response.Found
                ? $"Snapshot complete: {response.IndependentItemCount:N0} independent items; "
                  + $"{response.SafeToSellInstanceCount + response.SellSurplusInstanceCount:N0} sell recommendation(s); "
                  + $"{response.RecoverableCells:N0} recoverable cell(s); "
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
        _defaultsLoaded = false;
        _textFilter = string.Empty;
        _categoryFilter = "All";
        _destinationFilter = "All";
        _foundInRaidFilter = FoundInRaidFilter.All;
        _status = "Open this tab to build a read-only snapshot of the active PMC stash.";
    }

    private void EnsureDefaults()
    {
        if (_defaultsLoaded)
        {
            return;
        }

        _view = ParseView(Plugin.Settings.DefaultStashView.Value);
        _sort = ParseSort(Plugin.Settings.DefaultStashSorting.Value);
        _defaultsLoaded = true;
    }

    private void DrawViewTabs()
    {
        GUILayout.BeginHorizontal();
        DrawViewButton("Overview", StashView.Overview, 95f);
        DrawViewButton("Safe to Sell", StashView.SafeToSell, 110f);
        DrawViewButton("Cleanup", StashView.Cleanup, 90f);
        DrawViewButton("Keep", StashView.Keep, 75f);
        DrawViewButton("Review", StashView.Review, 85f);
        DrawViewButton("Duplicates", StashView.Duplicates, 100f);
        DrawViewButton("Damaged", StashView.Damaged, 95f);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawViewButton(string label, StashView view, float width)
    {
        if (HermesUi.DrawTabButton(label, _view == view, width))
        {
            _view = view;
            _scroll = Vector2.zero;
        }
    }

    private static void DrawPinnedSummary(HermesStashSummaryResponse summary)
    {
        GUILayout.BeginHorizontal();
        DrawMetric(
            "SELLABLE",
            FormatNumber(summary.PotentiallySellQuantity),
            $"Best value ₽{summary.PotentialBestSaleValue:N0}");
        DrawMetric(
            "RECOVERABLE SPACE",
            $"{summary.RecoverableCells:N0} cells",
            $"{summary.CleanupCandidateInstanceCount:N0} exact instance(s)");
        DrawMetric(
            "RESERVED",
            FormatNumber(summary.RecommendedKeepQuantity),
            $"Keep rows {summary.KeepInstanceCount:N0}");
        DrawMetric(
            "REVIEW",
            summary.ReviewInstanceCount.ToString("N0"),
            $"Condition warnings {summary.DamagedOrDepletedItemCount:N0}");
        GUILayout.EndHorizontal();
    }

    private void DrawFilterToolbar(HermesStashSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Filter", GUILayout.Width(45f));
        _textFilter = GUILayout.TextField(_textFilter, GUILayout.Width(220f));

        var categories = new[] { "All" }
            .Concat(summary.Recommendations
                .Select(item => item.Category)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (GUILayout.Button($"Category: {_categoryFilter}", GUILayout.Width(155f)))
        {
            _categoryFilter = NextValue(categories, _categoryFilter);
        }

        var destinations = new[] { "All" }
            .Concat(summary.Recommendations
                .Select(item => item.BestSaleDestination ?? "Unavailable")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (GUILayout.Button($"Destination: {_destinationFilter}", GUILayout.Width(180f)))
        {
            _destinationFilter = NextValue(destinations, _destinationFilter);
        }

        if (GUILayout.Button($"FIR: {FriendlyFoundInRaidFilter(_foundInRaidFilter)}", GUILayout.Width(125f)))
        {
            _foundInRaidFilter = (FoundInRaidFilter)(((int)_foundInRaidFilter + 1) % 3);
        }

        if (GUILayout.Button($"Sort: {FriendlySort(_sort)}", GUILayout.Width(175f)))
        {
            _sort = (StashSort)(((int)_sort + 1) % Enum.GetValues(typeof(StashSort)).Length);
        }

        if (GUILayout.Button("Clear", GUILayout.Width(65f)))
        {
            _textFilter = string.Empty;
            _categoryFilter = "All";
            _destinationFilter = "All";
            _foundInRaidFilter = FoundInRaidFilter.All;
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label(
            "Filters apply to exact recommendation rows. Totals above remain based on the complete configured server analysis.",
            GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Copy summary", GUILayout.Width(115f)))
        {
            GUIUtility.systemCopyBuffer = BuildClipboardSummary(summary);
            _status = "Stash summary copied to the clipboard.";
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void DrawSummary(HermesStashSummaryResponse summary)
    {
        if (!summary.Found)
        {
            HermesUi.DrawError(summary.Message ?? "Stash analysis is unavailable.");
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
            case StashView.Cleanup:
                DrawCleanup(summary);
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
        GUILayout.Label($"Snapshot generated {generated:yyyy-MM-dd HH:mm:ss} • profile-aware server cache {summary.CacheTtlSeconds}s");
    }

    private static void DrawOverview(HermesStashSummaryResponse summary)
    {
        GUILayout.Label("STASH SNAPSHOT");
        GUILayout.BeginHorizontal();
        DrawMetric("ITEM INSTANCES", summary.TotalItemInstances.ToString("N0"), "Includes installed parts and loaded ammunition.");
        DrawMetric("INDEPENDENT ITEMS", summary.IndependentItemCount.ToString("N0"), "Standalone stacks and sellable assemblies.");
        DrawMetric("VALUED ITEMS", summary.ValuedIndependentItemCount.ToString("N0"), $"Unsupported: {summary.UnsupportedIndependentItemCount:N0}");
        DrawMetric("OCCUPIED CELLS", summary.OccupiedCells.ToString("N0"), "Template footprint estimate.");
        GUILayout.EndHorizontal();

        if (Plugin.Settings.ShowUnsupportedStashItems.Value
            && summary.UnsupportedIndependentItemCount > 0)
        {
            HermesUi.DrawStatusLine(
                $"{summary.UnsupportedIndependentItemCount:N0} quest-only or handbook-less independent item(s) were excluded from valuation and recommendations.");
        }

        GUILayout.Space(8f);
        GUILayout.Label("RECOMMENDATIONS");
        GUILayout.BeginHorizontal();
        DrawMetric("SAFE TO SELL", summary.SafeToSellInstanceCount.ToString("N0"), "No configured reservation or review restriction.");
        DrawMetric("SELL SURPLUS", summary.SellSurplusInstanceCount.ToString("N0"), "Keeps reserved quantity and identifies only the excess.");
        DrawMetric("KEEP", summary.KeepInstanceCount.ToString("N0"), $"Reserved quantity {FormatNumber(summary.RecommendedKeepQuantity)}");
        DrawMetric("REVIEW", summary.ReviewInstanceCount.ToString("N0"), "Built, filled, protected, or weakly priced items.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("SELLABLE QUANTITY");
        GUILayout.BeginHorizontal();
        DrawValueCard("BEST DESTINATION", summary.PotentialBestSaleValue, $"{FormatNumber(summary.PotentiallySellQuantity)} unit(s)");
        DrawValueCard("TRADER ALTERNATIVE", summary.PotentialTraderSaleValue, "Best supported trader value.");
        DrawValueCard("FLEA NET ALTERNATIVE", summary.PotentialFleaNetValue, "After estimated listing fees.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("COMPLETE STASH VALUE");
        GUILayout.BeginHorizontal();
        DrawValueCard("BEST TRADER LIQUIDATION", summary.BestTraderLiquidationValue, "Complete valued stash before reservations.");
        DrawValueCard("ESTIMATED FLEA NET", summary.EstimatedFleaNetValue, "Items with usable flea estimates.");
        DrawValueCard("BEST RELIABLE DESTINATION", summary.BestDestinationLiquidationValue, "Higher reliable destination per item.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("BEST SALE DESTINATIONS");
        if (summary.SaleDestinationBreakdown.Count == 0)
        {
            HermesUi.DrawEmptyState("No reliable sale destination was available.");
        }
        else
        {
            var visibleDestinations = HermesUi.LimitRows(
                summary.SaleDestinationBreakdown,
                out var hiddenDestinations);
            foreach (var destination in visibleDestinations)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(destination.Destination, GUILayout.Width(180f));
                GUILayout.Label($"{destination.ItemCount:N0} item(s)", GUILayout.Width(120f));
                GUILayout.FlexibleSpace();
                GUILayout.Label($"₽{destination.RoubleEquivalent:N0}");
                GUILayout.EndHorizontal();
            }
            HermesUi.DrawHiddenRowsNotice(hiddenDestinations);
        }

        GUILayout.Space(8f);
        GUILayout.Label("MOST VALUABLE ITEMS");
        var visibleValuableItems = HermesUi.LimitRows(
            summary.MostValuableItems,
            out var hiddenValuable);
        foreach (var item in visibleValuableItems)
        {
            DrawCompactItemRow(item);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenValuable);
    }

    private void DrawRecommendationList(
        HermesStashSummaryResponse summary,
        string heading,
        IEnumerable<HermesStashValuationItem> items)
    {
        var rows = ApplyRecommendationFilters(items).ToList();
        GUILayout.Label($"{heading} — {rows.Count:N0} visible");
        GUILayout.Label(
            "Reservation priority is active quests, next hideout upgrade, future quests, then later hideout stages. "
            + "Only the enabled F12 reservation sources are applied.");
        GUILayout.Space(5f);

        if (Plugin.Settings.ShowUnsupportedStashItems.Value
            && !string.IsNullOrWhiteSpace(summary.Message))
        {
            GUILayout.Label(summary.Message);
            GUILayout.Space(5f);
        }

        if (rows.Count == 0)
        {
            HermesUi.DrawEmptyState("No stash items match the current view and filters.");
            return;
        }

        var visibleRows = HermesUi.LimitRows(rows, out var hiddenRows);
        foreach (var item in visibleRows)
        {
            DrawItemRow(item);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenRows);
    }

    private void DrawCleanup(HermesStashSummaryResponse summary)
    {
        var rows = ApplyRecommendationFilters(summary.CleanupCandidates).ToList();
        GUILayout.Label($"SPACE RECOVERY — {rows.Count:N0} visible");
        GUILayout.Label(
            "Only complete exact instances that satisfy the configured minimum sale value and value-per-cell thresholds are included. "
            + "Partial stacks, filled containers, installed assemblies, and reserved quantities remain excluded.");
        GUILayout.Space(5f);

        GUILayout.BeginHorizontal();
        DrawMetric("REMOVABLE INSTANCES", summary.CleanupCandidateInstanceCount.ToString("N0"), "Complete exact instances passing server thresholds.");
        DrawMetric("RECOVERABLE CELLS", summary.RecoverableCells.ToString("N0"), summary.OccupiedCells > 0
            ? $"{(summary.RecoverableCells * 100d / summary.OccupiedCells):N1}% of occupied cells."
            : "No occupied-cell baseline.");
        DrawValueCard("BEST SALE VALUE", summary.CleanupBestSaleValue, "Combined best reliable destination value.");
        GUILayout.EndHorizontal();

        if (rows.Count == 0)
        {
            HermesUi.DrawEmptyState("No cleanup candidates match the configured thresholds and current filters.");
            return;
        }

        var visibleCleanupRows = HermesUi.LimitRows(rows, out var hiddenCleanup);
        foreach (var item in visibleCleanupRows)
        {
            DrawItemRow(item, true);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenCleanup);
    }

    private void DrawDuplicates(HermesStashSummaryResponse summary)
    {
        var rows = summary.DuplicateGroups
            .Where(group => MatchesText(group.Name, group.ShortName, group.Note, group.BestSaleDestination))
            .OrderByDescending(group => group.PotentialExcessSaleValue)
            .ThenByDescending(group => group.PotentialExcessQuantity)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        GUILayout.Label($"DUPLICATE REVIEW — {rows.Count:N0} visible");
        GUILayout.Label(
            $"Exact-template duplicate advice keeps explicit reservations first. Without one, the configured baseline keeps "
            + $"{Math.Clamp(Plugin.Settings.DuplicateBaselineReserve.Value, 0, 1000):N0} unit(s).");
        GUILayout.Space(5f);

        if (rows.Count == 0)
        {
            HermesUi.DrawEmptyState("No duplicate groups match the current filter.");
            return;
        }

        var visibleDuplicateRows = HermesUi.LimitRows(rows, out var hiddenDuplicates);
        foreach (var group in visibleDuplicateRows)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(group.Name);
            GUILayout.FlexibleSpace();
            GUILayout.Label(group.PotentialExcessSaleValue > 0
                ? $"Potential value ₽{group.PotentialExcessSaleValue:N0}"
                : "Value unavailable");
            GUILayout.EndHorizontal();
            GUILayout.Label(
                $"Instances {group.InstanceCount:N0} • Owned {FormatNumber(group.OwnedQuantity)} • "
                + $"Explicit reserve {FormatNumber(group.ExplicitlyReservedQuantity)} • "
                + $"Suggested reserve {FormatNumber(group.SuggestedReserveQuantity)} • "
                + $"Potential excess {FormatNumber(group.PotentialExcessQuantity)}");
            GUILayout.Label($"Cells {group.OccupiedCells:N0} • Destination {group.BestSaleDestination ?? "Review manually"}");
            GUILayout.Label(group.Note);
            GUILayout.EndVertical();
        }
        HermesUi.DrawHiddenRowsNotice(hiddenDuplicates);
    }

    private void DrawDamaged(HermesStashSummaryResponse summary)
    {
        var rows = summary.DamagedOrDepletedItems
            .Where(item => MatchesText(item.Name, item.ShortName, item.Status, item.ConditionKind, item.BestSaleDestination))
            .OrderBy(item => item.ConditionPercent)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        GUILayout.Label($"DAMAGED AND DEPLETED ITEMS — {rows.Count:N0} visible");
        GUILayout.Label(
            $"Current thresholds: weapon {Math.Clamp(Plugin.Settings.StashWeaponDurabilityThreshold.Value, 1, 100)}%, "
            + $"armor/durability {Math.Clamp(Plugin.Settings.StashArmorDurabilityThreshold.Value, 1, 100)}%, "
            + $"resources {Math.Clamp(Plugin.Settings.StashLowResourceThreshold.Value, 0, 100)}%, "
            + $"keys ≤ {Math.Clamp(Plugin.Settings.StashKeyUsesWarningThreshold.Value, 0, 100)} uses.");
        GUILayout.Space(5f);

        if (rows.Count == 0)
        {
            HermesUi.DrawEmptyState("No damaged or depleted items match the current thresholds and filter.");
            return;
        }

        var visibleDamagedRows = HermesUi.LimitRows(rows, out var hiddenDamaged);
        foreach (var item in visibleDamagedRows)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{item.Status.ToUpperInvariant()} — {item.Name}");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Ask HERMES", GUILayout.Width(105f)))
            {
                Plugin.Instance?.OpenForStashItem(item.InstanceKey, item.Name);
            }
            GUILayout.Label($"{item.ConditionPercent:N0}%", GUILayout.Width(55f));
            GUILayout.EndHorizontal();
            GUILayout.Label(item.InstanceLabel);
            GUILayout.Label(
                $"{item.ConditionKind}: {FormatNumber(item.ConditionCurrent)}/{FormatNumber(item.ConditionMaximum)} • "
                + $"Report threshold {item.ThresholdPercent:N0}%");
            GUILayout.Label(item.BestSaleValue.HasValue
                ? $"Best estimated destination {item.BestSaleDestination} • ₽{item.BestSaleValue.Value:N0}"
                : "Best estimated destination unavailable");
            GUILayout.Label(item.Recommendation);
            GUILayout.EndVertical();
        }
        HermesUi.DrawHiddenRowsNotice(hiddenDamaged);
    }

    private IEnumerable<HermesStashValuationItem> ApplyRecommendationFilters(
        IEnumerable<HermesStashValuationItem> source)
    {
        var filtered = source.Where(item =>
            MatchesText(
                item.Name,
                item.ShortName,
                item.InstanceLabel,
                item.Category,
                item.Recommendation,
                item.BestSaleDestination,
                string.Join(" ", item.Reasons))
            && (_categoryFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                || item.Category.Equals(_categoryFilter, StringComparison.OrdinalIgnoreCase))
            && (_destinationFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                || (item.BestSaleDestination ?? "Unavailable").Equals(
                    _destinationFilter,
                    StringComparison.OrdinalIgnoreCase))
            && (_foundInRaidFilter == FoundInRaidFilter.All
                || (_foundInRaidFilter == FoundInRaidFilter.FoundInRaid && item.FoundInRaid)
                || (_foundInRaidFilter == FoundInRaidFilter.NotFoundInRaid && !item.FoundInRaid)));

        return _sort switch
        {
            StashSort.Name => filtered
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.InstanceLabel, StringComparer.OrdinalIgnoreCase),
            StashSort.SellValue => filtered
                .OrderByDescending(item => item.PotentialBestSaleValue)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            StashSort.SellableQuantity => filtered
                .OrderByDescending(item => item.PotentiallySellQuantity)
                .ThenByDescending(item => item.PotentialBestSaleValue),
            StashSort.ValuePerCell => filtered
                .OrderByDescending(GetValuePerCell)
                .ThenByDescending(item => item.PotentialBestSaleValue),
            StashSort.OccupiedCells => filtered
                .OrderByDescending(item => item.OccupiedCells)
                .ThenByDescending(item => item.PotentialBestSaleValue),
            StashSort.Condition => filtered
                .OrderBy(item => item.ConditionPercent)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            StashSort.Destination => filtered
                .OrderBy(item => item.BestSaleDestination ?? "Unavailable", StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.PotentialBestSaleValue),
            StashSort.ReservedQuantity => filtered
                .OrderByDescending(GetReservedQuantity)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered
                .OrderBy(item => RecommendationRank(item.Recommendation))
                .ThenByDescending(item => item.PotentialBestSaleValue)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void DrawItemRow(HermesStashValuationItem item, bool emphasizeSpace = false)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        var badges = new List<string> { item.Recommendation.ToUpperInvariant(), item.Category.ToUpperInvariant() };
        if (item.FoundInRaid)
        {
            badges.Add("FIR");
        }
        if (item.PotentiallySellQuantity > 0d && item.PotentiallySellQuantity + 0.001d < item.Quantity)
        {
            badges.Add("PARTIAL STACK");
        }
        if (item.ContainedItemCount > 0)
        {
            badges.Add("FILLED");
        }
        if (item.InstalledItemCount > 0)
        {
            badges.Add("ASSEMBLY");
        }
        if (item.IsProtectedCurrency)
        {
            badges.Add("PROTECTED");
        }

        GUILayout.Label($"{string.Join(" • ", badges)} — {item.Name}");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Ask HERMES", GUILayout.Width(105f)))
        {
            Plugin.Instance?.OpenForStashItem(item.InstanceKey, item.Name);
        }
        GUILayout.Label(item.BestSaleValue.HasValue
            ? $"Best {item.BestSaleDestination} • ₽{item.BestSaleValue.Value:N0}"
            : "No reliable destination", GUILayout.Width(230f));
        GUILayout.EndHorizontal();

        GUILayout.Label(item.InstanceLabel);
        GUILayout.Label(
            $"Owned {FormatNumber(item.Quantity)} • Keep {FormatNumber(item.RecommendedKeepQuantity)} • "
            + $"Sellable {FormatNumber(item.PotentiallySellQuantity)} • Condition {item.ConditionPercent:N0}%");

        if (item.PotentialBestSaleValue > 0)
        {
            GUILayout.Label(
                $"Sellable value ₽{item.PotentialBestSaleValue:N0} • "
                + $"Value per occupied cell ₽{GetValuePerCell(item):N0} • Cells {item.OccupiedCells:N0}");
        }
        else if (emphasizeSpace)
        {
            GUILayout.Label($"Cells {item.OccupiedCells:N0} • value unavailable");
        }

        var saleOptions = new List<string>();
        if (item.BestTraderValue.HasValue)
        {
            saleOptions.Add($"{item.BestTraderName} ₽{item.BestTraderValue.Value:N0}");
        }
        if (item.FleaEstimateAvailable && item.EstimatedFleaNetValue.HasValue)
        {
            saleOptions.Add(
                $"Flea net ₽{item.EstimatedFleaNetValue.Value:N0} "
                + $"({item.FleaComparableOfferCount:N0} offers, {(item.FleaEstimateReliable ? "reliable" : "informational")})");
        }
        if (saleOptions.Count > 0)
        {
            GUILayout.Label("Sale alternatives: " + string.Join(" • ", saleOptions));
        }

        var reserves = new List<string>();
        if (item.ActiveQuestReserve > 0d)
        {
            reserves.Add($"active quest {FormatNumber(item.ActiveQuestReserve)}");
        }
        if (item.NextHideoutReserve > 0d)
        {
            reserves.Add($"next hideout {FormatNumber(item.NextHideoutReserve)}");
        }
        if (item.FutureQuestReserve > 0d)
        {
            reserves.Add($"future quest {FormatNumber(item.FutureQuestReserve)}");
        }
        if (item.FutureHideoutReserve > 0d)
        {
            reserves.Add($"future hideout {FormatNumber(item.FutureHideoutReserve)}");
        }
        if (reserves.Count > 0)
        {
            GUILayout.Label("Reservation breakdown: " + string.Join(" • ", reserves));
        }

        GUILayout.Label(
            $"Trader sellable value ₽{item.PotentialTraderSaleValue:N0} • "
            + $"Flea sellable net ₽{item.PotentialFleaNetValue:N0} • "
            + $"Handbook condition-adjusted ₽{item.ConditionAdjustedHandbookValue:N0}");

        if (Plugin.Settings.ShowStashReservationReasons.Value)
        {
            foreach (var reason in item.Reasons)
            {
                GUILayout.Label("• " + reason);
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawCompactItemRow(HermesStashValuationItem item)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label($"{item.Name} [{item.Category}]", GUILayout.ExpandWidth(true));
        GUILayout.Label(item.FoundInRaid ? "FIR" : string.Empty, GUILayout.Width(35f));
        GUILayout.Label(item.BestSaleValue.HasValue
            ? $"{item.BestSaleDestination} • ₽{item.BestSaleValue.Value:N0}"
            : $"Reference ₽{item.ConditionAdjustedHandbookValue:N0}", GUILayout.Width(230f));
        if (GUILayout.Button("Ask HERMES", GUILayout.Width(105f)))
        {
            Plugin.Instance?.OpenForStashItem(item.InstanceKey, item.Name);
        }
        GUILayout.EndHorizontal();
    }

    private bool MatchesText(params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(_textFilter))
        {
            return true;
        }

        var query = _textFilter.Trim();
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static long GetValuePerCell(HermesStashValuationItem item)
    {
        return item.OccupiedCells > 0
            ? item.PotentialBestSaleValue / item.OccupiedCells
            : 0L;
    }

    private static double GetReservedQuantity(HermesStashValuationItem item)
    {
        return item.ActiveQuestReserve
               + item.FutureQuestReserve
               + item.NextHideoutReserve
               + item.FutureHideoutReserve;
    }

    private static int RecommendationRank(string recommendation)
    {
        return recommendation switch
        {
            "Sell surplus" => 0,
            "Safe to sell" => 1,
            "Keep" => 2,
            "Review" => 3,
            _ => 4
        };
    }

    private static string NextValue(IReadOnlyList<string> values, string current)
    {
        if (values.Count == 0)
        {
            return "All";
        }

        var index = values
            .Select((value, position) => (value, position))
            .FirstOrDefault(pair => pair.value.Equals(current, StringComparison.OrdinalIgnoreCase))
            .position;
        return values[(index + 1) % values.Count];
    }

    private static StashView ParseView(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "safe to sell" or "safetosell" => StashView.SafeToSell,
            "cleanup" => StashView.Cleanup,
            "keep" => StashView.Keep,
            "review" => StashView.Review,
            "duplicates" => StashView.Duplicates,
            "damaged" => StashView.Damaged,
            _ => StashView.Overview
        };
    }

    private static StashSort ParseSort(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "name" => StashSort.Name,
            "sell value" or "value" => StashSort.SellValue,
            "sellable quantity" or "quantity" => StashSort.SellableQuantity,
            "value per cell" => StashSort.ValuePerCell,
            "occupied cells" or "cells" => StashSort.OccupiedCells,
            "condition" => StashSort.Condition,
            "destination" => StashSort.Destination,
            "reserved quantity" or "reserved" => StashSort.ReservedQuantity,
            _ => StashSort.Recommendation
        };
    }

    private static string FriendlySort(StashSort sort)
    {
        return sort switch
        {
            StashSort.SellValue => "Sell value",
            StashSort.SellableQuantity => "Sellable qty",
            StashSort.ValuePerCell => "Value/cell",
            StashSort.OccupiedCells => "Cells",
            StashSort.ReservedQuantity => "Reserved qty",
            _ => sort.ToString()
        };
    }

    private static string FriendlyFoundInRaidFilter(FoundInRaidFilter filter)
    {
        return filter switch
        {
            FoundInRaidFilter.FoundInRaid => "Only",
            FoundInRaidFilter.NotFoundInRaid => "Exclude",
            _ => "All"
        };
    }

    private static string BuildClipboardSummary(HermesStashSummaryResponse summary)
    {
        return $"HERMES Stash Intelligence\n"
               + $"Independent items: {summary.IndependentItemCount:N0}\n"
               + $"Occupied cells: {summary.OccupiedCells:N0}\n"
               + $"Safe to sell: {summary.SafeToSellInstanceCount:N0}\n"
               + $"Sell surplus: {summary.SellSurplusInstanceCount:N0}\n"
               + $"Keep: {summary.KeepInstanceCount:N0}\n"
               + $"Review: {summary.ReviewInstanceCount:N0}\n"
               + $"Sellable quantity: {FormatNumber(summary.PotentiallySellQuantity)}\n"
               + $"Potential best sale value: ₽{summary.PotentialBestSaleValue:N0}\n"
               + $"Cleanup candidates: {summary.CleanupCandidateInstanceCount:N0}\n"
               + $"Recoverable cells: {summary.RecoverableCells:N0}\n"
               + $"Cleanup value: ₽{summary.CleanupBestSaleValue:N0}\n"
               + $"Duplicate groups: {summary.DuplicateGroupCount:N0}\n"
               + $"Condition warnings: {summary.DamagedOrDepletedItemCount:N0}";
    }

    private static void DrawMetric(string label, string value, string note)
    {
        HermesUi.DrawMetric(label, value, note, 180f);
    }

    private static void DrawValueCard(string label, long value, string note)
    {
        HermesUi.DrawMetric(label, $"₽{value:N0}", note, 250f);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }
}
