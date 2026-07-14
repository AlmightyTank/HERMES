using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesStashAnalysisService(
    HermesStashService stashService,
    HermesReservationService reservationService,
    HermesTraderService traderService,
    HermesMarketService marketService,
    ProfileHelper profileHelper,
    JsonUtil jsonUtil)
{
    public const int StashCacheTtlSeconds = 5;

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public HermesStashSummaryResponse GetSummary(MongoId sessionId)
    {
        var key = sessionId.ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresUnixTime > now)
            {
                return cached.Response;
            }

            _cache.Remove(key);
        }

        var response = BuildSummary(sessionId, now);
        if (response.Found)
        {
            lock (_sync)
            {
                _cache[key] = new CacheEntry(response, now + StashCacheTtlSeconds);
            }
        }

        return response;
    }

    public void Clear(string? reason = null)
    {
        lock (_sync)
        {
            _cache.Clear();
        }
    }

    private HermesStashSummaryResponse BuildSummary(MongoId sessionId, long generatedUnixTime)
    {
        var snapshot = stashService.BuildAnalysisSnapshot(sessionId);
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (snapshot is null || profile is null)
        {
            return NotFound("HERMES could not read the active PMC stash.");
        }

        var stashInventory = snapshot.Entries
            .GroupBy(entry => entry.RootTemplateId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(entry => entry.Instance.Quantity),
                StringComparer.OrdinalIgnoreCase);
        var stashFoundInRaidInventory = snapshot.Entries
            .Where(entry => entry.Instance.FoundInRaid)
            .GroupBy(entry => entry.RootTemplateId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(entry => entry.Instance.Quantity),
                StringComparer.OrdinalIgnoreCase);
        var reservations = reservationService.Build(
            sessionId,
            stashInventory,
            stashFoundInRaidInventory);
        if (reservations is null)
        {
            return NotFound("HERMES could not read the active quest and hideout reservation state.");
        }

        var profileJson = jsonUtil.Serialize(profile) ?? "{}";
        long fullHandbookValue = 0;
        long conditionAdjustedHandbookValue = 0;
        long traderLiquidationValue = 0;
        long fleaNetValue = 0;
        long bestDestinationLiquidationValue = 0;
        var traderValuedCount = 0;
        var fleaValuedCount = 0;
        var noTraderBuyerCount = 0;
        var noFleaEstimateCount = 0;
        var traderTotals = new Dictionary<string, (int Count, long Value)>(StringComparer.OrdinalIgnoreCase);
        var analyzedEntries = new List<AnalyzedEntry>();

        foreach (var entry in snapshot.Entries)
        {
            fullHandbookValue += entry.FullHandbookReferenceValue;
            conditionAdjustedHandbookValue += entry.Instance.ConditionAdjustedReferenceValue;

            var bestTrader = traderService.GetBestSellOfferForComponents(
                entry.RootTemplateId,
                entry.Components,
                profileJson);
            if (bestTrader is null)
            {
                noTraderBuyerCount++;
            }
            else
            {
                traderValuedCount++;
                traderLiquidationValue += bestTrader.RoubleEquivalent;
                var existing = traderTotals.GetValueOrDefault(bestTrader.TraderName);
                traderTotals[bestTrader.TraderName] = (
                    existing.Count + 1,
                    existing.Value + bestTrader.RoubleEquivalent);
            }

            var flea = marketService.GetStashEstimate(entry, sessionId);
            if (flea.Available && flea.EstimatedNetSale.HasValue)
            {
                fleaValuedCount++;
                fleaNetValue += flea.EstimatedNetSale.Value;
            }
            else
            {
                noFleaEstimateCount++;
            }

            var bestSale = ChooseBestSale(bestTrader, flea);
            if (bestSale.Value.HasValue)
            {
                bestDestinationLiquidationValue += bestSale.Value.Value;
            }

            analyzedEntries.Add(new AnalyzedEntry(entry, bestTrader, flea));
        }

        var recommendationRows = new List<HermesStashValuationItem>();
        foreach (var group in analyzedEntries.GroupBy(
                     value => value.Entry.RootTemplateId.ToString(),
                     StringComparer.OrdinalIgnoreCase))
        {
            reservations.ByTemplate.TryGetValue(group.Key, out var reservation);
            recommendationRows.AddRange(BuildRecommendationsForTemplate(
                group.ToList(),
                reservation));
        }

        var safeToSellCount = recommendationRows.Count(row => row.Recommendation == "Safe to sell");
        var sellSurplusCount = recommendationRows.Count(row => row.Recommendation == "Sell surplus");
        var keepCount = recommendationRows.Count(row => row.Recommendation == "Keep");
        var reviewCount = recommendationRows.Count(row => row.Recommendation == "Review");
        var recommendedKeepQuantity = recommendationRows.Sum(row => row.RecommendedKeepQuantity);
        var potentiallySellQuantity = recommendationRows
            .Where(row => row.Recommendation is "Safe to sell" or "Sell surplus")
            .Sum(row => row.PotentiallySellQuantity);
        var potentialTraderSaleValue = recommendationRows
            .Where(row => row.Recommendation is "Safe to sell" or "Sell surplus")
            .Sum(row => row.PotentialTraderSaleValue);
        var potentialFleaNetValue = recommendationRows
            .Where(row => row.Recommendation is "Safe to sell" or "Sell surplus")
            .Sum(row => row.PotentialFleaNetValue);
        var potentialBestSaleValue = recommendationRows
            .Where(row => row.Recommendation is "Safe to sell" or "Sell surplus")
            .Sum(row => row.PotentialBestSaleValue);

        var traderBreakdown = traderTotals
            .Select(pair => new HermesStashTraderBreakdown(pair.Key, pair.Value.Count, pair.Value.Value))
            .OrderByDescending(row => row.RoubleEquivalent)
            .ThenBy(row => row.TraderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var destinationBreakdown = recommendationRows
            .Where(row => !string.IsNullOrWhiteSpace(row.BestSaleDestination) && row.BestSaleValue is > 0)
            .GroupBy(row => row.BestSaleDestination!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HermesStashSaleDestinationBreakdown(
                group.Key,
                group.Count(),
                group.Sum(row => row.BestSaleValue ?? 0L)))
            .OrderByDescending(row => row.RoubleEquivalent)
            .ThenBy(row => row.Destination, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var valuableItems = recommendationRows
            .OrderByDescending(row => row.BestSaleValue ?? row.BestTraderValue ?? row.ConditionAdjustedHandbookValue)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
        var orderedRecommendations = recommendationRows
            .OrderBy(row => RecommendationRank(row.Recommendation))
            .ThenByDescending(row => row.PotentialBestSaleValue)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.InstanceLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var duplicates = BuildDuplicateGroups(recommendationRows);
        var damaged = BuildConditionReport(recommendationRows);

        return new HermesStashSummaryResponse(
            true,
            snapshot.UnsupportedIndependentItemCount > 0
                ? $"{snapshot.UnsupportedIndependentItemCount:N0} independent item(s) are excluded from valuation and recommendations because HERMES does not list quest-only or handbook-less items."
                : null,
            snapshot.TotalItemInstances,
            snapshot.IndependentItemCount,
            snapshot.Entries.Count,
            snapshot.UnsupportedIndependentItemCount,
            snapshot.OccupiedCells,
            Math.Max(0L, fullHandbookValue),
            Math.Max(0L, conditionAdjustedHandbookValue),
            Math.Max(0L, traderLiquidationValue),
            Math.Max(0L, fleaNetValue),
            Math.Max(0L, bestDestinationLiquidationValue),
            traderValuedCount,
            fleaValuedCount,
            noTraderBuyerCount,
            noFleaEstimateCount,
            safeToSellCount,
            sellSurplusCount,
            keepCount,
            reviewCount,
            duplicates.Count,
            damaged.Count,
            Math.Max(0d, recommendedKeepQuantity),
            Math.Max(0d, potentiallySellQuantity),
            Math.Max(0L, potentialTraderSaleValue),
            Math.Max(0L, potentialFleaNetValue),
            Math.Max(0L, potentialBestSaleValue),
            generatedUnixTime,
            StashCacheTtlSeconds,
            traderBreakdown,
            destinationBreakdown,
            valuableItems,
            orderedRecommendations,
            duplicates,
            damaged);
    }

    private static IReadOnlyList<HermesStashValuationItem> BuildRecommendationsForTemplate(
        IReadOnlyList<AnalyzedEntry> entries,
        HermesTemplateReservation? reservation)
    {
        var states = entries
            .Select(entry => new AllocationState(entry))
            .ToList();

        if (reservation is not null)
        {
            AllocateReservation(
                states,
                reservation.ActiveQuestQuantity,
                reservation.ActiveQuestFoundInRaidQuantity,
                ReservationBucket.ActiveQuest);
            AllocateReservation(
                states,
                reservation.NextHideoutQuantity,
                reservation.NextHideoutFoundInRaidQuantity,
                ReservationBucket.NextHideout);
            AllocateReservation(
                states,
                reservation.FutureQuestQuantity,
                reservation.FutureQuestFoundInRaidQuantity,
                ReservationBucket.FutureQuest);
            AllocateReservation(
                states,
                reservation.FutureHideoutQuantity,
                reservation.FutureHideoutFoundInRaidQuantity,
                ReservationBucket.FutureHideout);
        }

        var output = new List<HermesStashValuationItem>();
        foreach (var state in states)
        {
            var entry = state.Analyzed.Entry;
            var quantity = Math.Max(0d, entry.Instance.Quantity);
            var keepQuantity = entry.IsProtectedCurrency
                ? quantity
                : Math.Min(quantity, state.ReservedQuantity);
            var sellQuantity = Math.Max(0d, quantity - keepQuantity);
            var bestSale = ChooseBestSale(state.Analyzed.BestTrader, state.Analyzed.Flea);
            var reasons = BuildReasons(entry, state, reservation, keepQuantity, sellQuantity, bestSale);
            var recommendation = Classify(entry, bestSale.Destination, keepQuantity, sellQuantity);
            var sellFraction = quantity > 0d ? Math.Clamp(sellQuantity / quantity, 0d, 1d) : 0d;
            var potentialTraderValue = state.Analyzed.BestTrader is not null
                ? ScaleValue(state.Analyzed.BestTrader.RoubleEquivalent, sellFraction)
                : 0L;
            var potentialFleaValue = state.Analyzed.Flea.EstimatedNetSale.HasValue
                ? ScaleValue(state.Analyzed.Flea.EstimatedNetSale.Value, sellFraction)
                : 0L;
            var potentialBestValue = bestSale.Value.HasValue
                ? ScaleValue(bestSale.Value.Value, sellFraction)
                : 0L;

            output.Add(new HermesStashValuationItem(
                entry.ItemKey,
                entry.Name,
                entry.ShortName,
                entry.Instance.InstanceKey,
                entry.Instance.Label,
                entry.Instance.Quantity,
                entry.Instance.ConditionPercent,
                entry.Instance.ConditionDescription,
                entry.Instance.ConditionKind,
                entry.Instance.ConditionCurrent,
                entry.Instance.ConditionMaximum,
                entry.OccupiedCells,
                entry.Instance.ChildItemCount,
                entry.ContainedItemCount,
                entry.FullHandbookReferenceValue,
                entry.Instance.ConditionAdjustedReferenceValue,
                state.Analyzed.BestTrader?.TraderName,
                state.Analyzed.BestTrader?.RoubleEquivalent,
                state.Analyzed.Flea.Available,
                state.Analyzed.Flea.ReliableForRecommendation,
                state.Analyzed.Flea.ComparableOfferCount,
                state.Analyzed.Flea.SuggestedListPrice,
                state.Analyzed.Flea.EstimatedListingFee,
                state.Analyzed.Flea.EstimatedNetSale,
                state.Analyzed.Flea.Source,
                bestSale.Destination,
                bestSale.Value,
                recommendation,
                keepQuantity,
                sellQuantity,
                state.ActiveQuestQuantity,
                state.FutureQuestQuantity,
                state.NextHideoutQuantity,
                state.FutureHideoutQuantity,
                potentialTraderValue,
                potentialFleaValue,
                potentialBestValue,
                reasons));
        }

        return output;
    }

    private static IReadOnlyList<HermesStashDuplicateGroup> BuildDuplicateGroups(
        IReadOnlyList<HermesStashValuationItem> rows)
    {
        var output = new List<HermesStashDuplicateGroup>();
        foreach (var group in rows.GroupBy(row => row.ItemKey, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            if (list.Any(row => row.Reasons.Any(reason => reason.Contains("Currency is protected", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var owned = list.Sum(row => row.Quantity);
            if (list.Count <= 1)
            {
                continue;
            }

            var explicitReserve = list.Sum(row => row.RecommendedKeepQuantity);
            var baselineReserve = explicitReserve > 0d
                ? explicitReserve
                : Math.Min(owned, list.Min(row => Math.Max(0d, row.Quantity)));
            var excess = Math.Max(0d, owned - baselineReserve);
            if (excess <= 0d)
            {
                continue;
            }

            var totalBestValue = list.Sum(row => row.BestSaleValue ?? 0L);
            var excessValue = owned > 0d
                ? ScaleValue(totalBestValue, Math.Clamp(excess / owned, 0d, 1d))
                : 0L;
            var destination = list
                .Where(row => !string.IsNullOrWhiteSpace(row.BestSaleDestination))
                .GroupBy(row => row.BestSaleDestination!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(destinationGroup => destinationGroup.Sum(row => row.BestSaleValue ?? 0L))
                .ThenBy(destinationGroup => destinationGroup.Key, StringComparer.OrdinalIgnoreCase)
                .Select(destinationGroup => destinationGroup.Key)
                .FirstOrDefault();
            var note = explicitReserve > 0d
                ? "Suggested reserve follows active/future quest and hideout reservations."
                : "No explicit quest or hideout reserve was found; this duplicate view keeps one baseline instance for review.";

            output.Add(new HermesStashDuplicateGroup(
                list[0].ItemKey,
                list[0].Name,
                list[0].ShortName,
                list.Count,
                owned,
                explicitReserve,
                baselineReserve,
                excess,
                list.Sum(row => row.OccupiedCells),
                destination,
                excessValue,
                note));
        }

        return output
            .OrderByDescending(group => group.PotentialExcessSaleValue)
            .ThenByDescending(group => group.PotentialExcessQuantity)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(150)
            .ToList();
    }

    private static IReadOnlyList<HermesStashConditionItem> BuildConditionReport(
        IReadOnlyList<HermesStashValuationItem> rows)
    {
        var output = new List<HermesStashConditionItem>();
        foreach (var row in rows)
        {
            var threshold = GetConditionThreshold(row);
            if (!threshold.ShouldReport)
            {
                continue;
            }

            var status = row.ConditionKind switch
            {
                "Weapon durability" => "Low weapon durability",
                "Armor durability" => "Low armor durability",
                "Key uses" => row.ConditionCurrent <= 0d ? "Depleted key" : "One use remaining",
                "Medical resource" => "Nearly empty medical item",
                "Repair resource" => "Nearly empty repair kit",
                "Consumable resource" => "Nearly depleted consumable",
                "Resource" => "Nearly depleted resource",
                _ => "Low condition"
            };
            var recommendation = row.Recommendation == "Keep"
                ? "Keep — this copy is currently reserved; replace or replenish it before relying on it."
                : !string.IsNullOrWhiteSpace(row.BestSaleDestination)
                    ? $"Review — current best estimated sale destination is {row.BestSaleDestination}."
                    : "Review manually — no reliable sale destination was available.";

            output.Add(new HermesStashConditionItem(
                row.ItemKey,
                row.Name,
                row.ShortName,
                row.InstanceKey,
                row.InstanceLabel,
                row.ConditionKind,
                row.ConditionPercent,
                row.ConditionCurrent,
                row.ConditionMaximum,
                threshold.ThresholdPercent,
                status,
                row.BestSaleDestination,
                row.BestSaleValue,
                recommendation));
        }

        return output
            .OrderBy(item => item.ConditionPercent)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(150)
            .ToList();
    }

    private static ConditionThreshold GetConditionThreshold(HermesStashValuationItem row)
    {
        return row.ConditionKind switch
        {
            "Weapon durability" => new ConditionThreshold(70, row.ConditionPercent < 70),
            "Armor durability" => new ConditionThreshold(50, row.ConditionPercent < 50),
            "Durability" => new ConditionThreshold(50, row.ConditionPercent < 50),
            "Medical resource" => new ConditionThreshold(20, row.ConditionPercent < 20),
            "Consumable resource" => new ConditionThreshold(20, row.ConditionPercent < 20),
            "Resource" => new ConditionThreshold(20, row.ConditionPercent < 20),
            "Repair resource" => new ConditionThreshold(20, row.ConditionPercent < 20),
            "Key uses" => new ConditionThreshold(
                row.ConditionMaximum > 0d
                    ? Math.Max(1, Convert.ToInt32(Math.Ceiling(100d / row.ConditionMaximum)))
                    : 1,
                row.ConditionCurrent <= 1d),
            _ => new ConditionThreshold(0, false)
        };
    }

    private static BestSaleChoice ChooseBestSale(
        HermesSellOffer? bestTrader,
        HermesStashFleaValuation flea)
    {
        var traderValue = bestTrader?.RoubleEquivalent;
        var reliableFleaValue = flea.Available && flea.ReliableForRecommendation
            ? flea.EstimatedNetSale
            : null;

        if (reliableFleaValue.HasValue
            && (!traderValue.HasValue || reliableFleaValue.Value > traderValue.Value))
        {
            return new BestSaleChoice("Flea", reliableFleaValue.Value);
        }

        if (traderValue.HasValue)
        {
            return new BestSaleChoice(bestTrader!.TraderName, traderValue.Value);
        }

        return new BestSaleChoice(null, null);
    }

    private static void AllocateReservation(
        IReadOnlyList<AllocationState> states,
        double requestedQuantity,
        double foundInRaidRequestedQuantity,
        ReservationBucket bucket)
    {
        var available = states.Sum(state => state.AvailableQuantity);
        var remaining = Math.Min(Math.Max(0d, requestedQuantity), Math.Max(0d, available));
        if (remaining <= 0d)
        {
            return;
        }

        var foundInRaidTarget = Math.Min(
            remaining,
            Math.Max(0d, foundInRaidRequestedQuantity));
        if (foundInRaidTarget > 0d)
        {
            var foundInRaidAllocated = AllocateFromCandidates(
                states.Where(state => state.Analyzed.Entry.Instance.FoundInRaid),
                foundInRaidTarget,
                bucket);
            remaining = Math.Max(0d, remaining - foundInRaidAllocated);
        }

        if (remaining > 0d)
        {
            AllocateFromCandidates(states, remaining, bucket);
        }
    }

    private static double AllocateFromCandidates(
        IEnumerable<AllocationState> candidates,
        double requestedQuantity,
        ReservationBucket bucket)
    {
        var remaining = Math.Max(0d, requestedQuantity);
        var allocated = 0d;
        foreach (var state in candidates
                     .Where(state => state.AvailableQuantity > 0d)
                     .OrderByDescending(state => state.Analyzed.Entry.Instance.FoundInRaid)
                     .ThenBy(state => state.UnitLiquidationValue)
                     .ThenBy(state => state.Analyzed.Entry.Instance.ConditionPercent)
                     .ThenBy(state => state.Analyzed.Entry.Instance.Label, StringComparer.OrdinalIgnoreCase))
        {
            if (remaining <= 0d)
            {
                break;
            }

            var quantity = Math.Min(state.AvailableQuantity, remaining);
            if (quantity <= 0d)
            {
                continue;
            }

            state.Add(bucket, quantity);
            allocated += quantity;
            remaining -= quantity;
        }

        return allocated;
    }

    private static IReadOnlyList<string> BuildReasons(
        HermesStashAnalysisEntry entry,
        AllocationState state,
        HermesTemplateReservation? reservation,
        double keepQuantity,
        double sellQuantity,
        BestSaleChoice bestSale)
    {
        var reasons = new List<string>();
        if (reservation is not null && keepQuantity > 0d)
        {
            foreach (var reason in reservation.Reasons)
            {
                if (reason.StartsWith("Active quest:", StringComparison.OrdinalIgnoreCase)
                    && state.ActiveQuestQuantity <= 0d)
                {
                    continue;
                }

                if (reason.StartsWith("Future quest:", StringComparison.OrdinalIgnoreCase)
                    && state.FutureQuestQuantity <= 0d)
                {
                    continue;
                }

                if (reason.StartsWith("Next hideout upgrade:", StringComparison.OrdinalIgnoreCase)
                    && state.NextHideoutQuantity <= 0d)
                {
                    continue;
                }

                if (reason.StartsWith("Future hideout upgrade:", StringComparison.OrdinalIgnoreCase)
                    && state.FutureHideoutQuantity <= 0d)
                {
                    continue;
                }

                reasons.Add(reason);
            }
        }

        if (entry.IsProtectedCurrency)
        {
            reasons.Add("Currency is protected and excluded from sale recommendations.");
        }

        if (sellQuantity > 0d && entry.ContainedItemCount > 0)
        {
            reasons.Add($"Contains {entry.ContainedItemCount:N0} stored item(s); empty it before considering a sale.");
        }

        if (sellQuantity > 0d && entry.Instance.ChildItemCount > 0)
        {
            reasons.Add($"Assembly contains {entry.Instance.ChildItemCount:N0} installed item(s); review the build before selling.");
        }

        if (sellQuantity > 0d && !state.Analyzed.Flea.ReliableForRecommendation
            && state.Analyzed.Flea.Available)
        {
            reasons.Add($"Flea estimate is informational only because just {state.Analyzed.Flea.ComparableOfferCount:N0} comparable offer(s) were available.");
        }

        if (sellQuantity > 0d && string.IsNullOrWhiteSpace(bestSale.Destination))
        {
            reasons.Add("No supported trader buyer or reliable flea estimate was found for this exact item instance.");
        }

        if (reasons.Count == 0 && sellQuantity > 0d)
        {
            reasons.Add("No unmet active/future quest or hideout reservation was found for this quantity.");
        }

        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    private static string Classify(
        HermesStashAnalysisEntry entry,
        string? bestSaleDestination,
        double keepQuantity,
        double sellQuantity)
    {
        if (entry.IsProtectedCurrency || sellQuantity <= 0d)
        {
            return "Keep";
        }

        if (entry.ContainedItemCount > 0
            || entry.Instance.ChildItemCount > 0
            || string.IsNullOrWhiteSpace(bestSaleDestination))
        {
            return "Review";
        }

        return keepQuantity > 0d ? "Sell surplus" : "Safe to sell";
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

    private static long ScaleValue(long value, double fraction)
    {
        return Math.Max(0L, Convert.ToInt64(Math.Floor(Math.Max(0L, value) * Math.Clamp(fraction, 0d, 1d))));
    }

    private static HermesStashSummaryResponse NotFound(string message)
    {
        return new HermesStashSummaryResponse(
            false,
            message,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0d,
            0d,
            0,
            0,
            0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StashCacheTtlSeconds,
            [],
            [],
            [],
            [],
            [],
            []);
    }

    private enum ReservationBucket
    {
        ActiveQuest,
        FutureQuest,
        NextHideout,
        FutureHideout
    }

    private sealed record AnalyzedEntry(
        HermesStashAnalysisEntry Entry,
        HermesSellOffer? BestTrader,
        HermesStashFleaValuation Flea);

    private sealed record BestSaleChoice(string? Destination, long? Value);

    private sealed record ConditionThreshold(int ThresholdPercent, bool ShouldReport);

    private sealed class AllocationState(AnalyzedEntry analyzed)
    {
        public AnalyzedEntry Analyzed { get; } = analyzed;
        public double ActiveQuestQuantity { get; private set; }
        public double FutureQuestQuantity { get; private set; }
        public double NextHideoutQuantity { get; private set; }
        public double FutureHideoutQuantity { get; private set; }
        public double ReservedQuantity => ActiveQuestQuantity
                                          + FutureQuestQuantity
                                          + NextHideoutQuantity
                                          + FutureHideoutQuantity;
        public double AvailableQuantity => Math.Max(
            0d,
            Analyzed.Entry.Instance.Quantity - ReservedQuantity);
        public double UnitLiquidationValue
        {
            get
            {
                var quantity = Math.Max(0.01d, Analyzed.Entry.Instance.Quantity);
                var bestSale = ChooseBestSale(Analyzed.BestTrader, Analyzed.Flea);
                var value = bestSale.Value
                            ?? Analyzed.BestTrader?.RoubleEquivalent
                            ?? Analyzed.Flea.EstimatedNetSale
                            ?? Analyzed.Entry.Instance.ConditionAdjustedReferenceValue;
                return Math.Max(0d, value / quantity);
            }
        }

        public void Add(ReservationBucket bucket, double quantity)
        {
            var allocated = Math.Min(Math.Max(0d, quantity), AvailableQuantity);
            switch (bucket)
            {
                case ReservationBucket.ActiveQuest:
                    ActiveQuestQuantity += allocated;
                    break;
                case ReservationBucket.FutureQuest:
                    FutureQuestQuantity += allocated;
                    break;
                case ReservationBucket.NextHideout:
                    NextHideoutQuantity += allocated;
                    break;
                case ReservationBucket.FutureHideout:
                    FutureHideoutQuantity += allocated;
                    break;
            }
        }
    }

    private sealed record CacheEntry(
        HermesStashSummaryResponse Response,
        long ExpiresUnixTime);
}
