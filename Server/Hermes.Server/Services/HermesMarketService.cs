using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Services;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesMarketService(
    RagfairOfferService ragfairOfferService,
    RagfairTaxService ragfairTaxService,
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    HandbookHelper handbookHelper,
    ItemHelper itemHelper,
    HermesCatalogService catalogService,
    HermesTraderService traderService)
{
    private const int MinimumOffersForRecommendation = 3;
    private const double MinimumComparableCondition = 0.80d;
    private const double OutlierMultiplier = 3.0d;
    private const long SuggestedUndercutAmount = 1L;

    private static readonly MongoId RoublesTpl = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId DollarsTpl = new("5696686a4bdc2da3298b456a");
    private static readonly MongoId EurosTpl = new("569668774bdc2da2298b4568");

    public HermesMarketSummaryResponse GetSummary(string? itemKey, MongoId sessionId)
    {
        var item = catalogService.ResolveItem(itemKey);
        if (item is null)
        {
            return NotFound(itemKey, "The selected HERMES item is no longer available. Search for it again.");
        }

        var pmcProfile = profileHelper.GetPmcProfile(sessionId);
        if (pmcProfile is null)
        {
            return NotFound(item.ItemKey, "HERMES could not read the active PMC profile.");
        }

        var playerLevel = pmcProfile.Info?.Level ?? 1;
        var requiredPlayerLevel = databaseService.GetGlobals().Configuration.RagFair.MinUserLevel;
        var fleaUnlocked = playerLevel >= requiredPlayerLevel;

        databaseService.GetItems().TryGetValue(item.TemplateId, out var itemTemplate);
        var canSellOnFlea = itemTemplate?.Properties?.CanSellOnRagfair ?? false;
        var sellUnavailableReason = GetSellUnavailableReason(
            fleaUnlocked,
            playerLevel,
            requiredPlayerLevel,
            canSellOnFlea);

        var traderSummary = traderService.GetSummary(item.ItemKey, sessionId);
        var bestTraderSell = traderSummary.BestSellOffer;
        var cheapestTraderBuy = FindCheapestAvailableCashTraderOffer(traderSummary.PurchaseOffers);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = new List<FleaCandidate>();
        var ignoredBarter = 0;
        var ignoredTrader = 0;
        var ignoredExpiredOrInvalid = 0;

        IEnumerable<RagfairOffer> offers;
        try
        {
            offers = ragfairOfferService.GetOffersOfType(item.TemplateId) ?? [];
        }
        catch
        {
            offers = [];
        }

        foreach (var offer in offers)
        {
            if (IsTraderOfferSafe(offer))
            {
                ignoredTrader++;
                continue;
            }

            if (offer.Locked == true
                || offer.Quantity <= 0
                || offer.EndTime is null
                || offer.EndTime <= now
                || offer.Items is null
                || offer.Items.Count == 0)
            {
                ignoredExpiredOrInvalid++;
                continue;
            }

            var rootItem = offer.Items.FirstOrDefault(offerItem => offerItem.Id == offer.Root)
                           ?? offer.Items[0];
            if (rootItem.Template != item.TemplateId)
            {
                ignoredExpiredOrInvalid++;
                continue;
            }

            var requirements = offer.Requirements?.ToList() ?? [];
            if (requirements.Count != 1 || !IsSupportedCashCurrency(requirements[0].TemplateId))
            {
                ignoredBarter++;
                continue;
            }

            var unitPrice = GetRoubleUnitPrice(offer, requirements[0]);
            if (unitPrice <= 0)
            {
                ignoredExpiredOrInvalid++;
                continue;
            }

            double quality;
            try
            {
                quality = Math.Clamp(itemHelper.GetItemQualityModifier(rootItem), 0.01d, 1d);
            }
            catch
            {
                quality = 1d;
            }

            candidates.Add(new FleaCandidate(
                rootItem,
                unitPrice,
                Math.Max(1, offer.Quantity),
                quality,
                Math.Max(0L, offer.EndTime.Value - now)));
        }

        var highCondition = candidates
            .Where(candidate => candidate.Quality >= MinimumComparableCondition)
            .OrderBy(candidate => candidate.UnitPrice)
            .ToList();

        var usedLowConditionFallback = highCondition.Count == 0 && candidates.Count > 0;
        var comparisonPool = usedLowConditionFallback
            ? candidates.OrderBy(candidate => candidate.UnitPrice).ToList()
            : highCondition;
        var ignoredLowCondition = usedLowConditionFallback ? 0 : candidates.Count - highCondition.Count;

        var withoutOutliers = RemoveHighOutliers(comparisonPool, out var ignoredOutliers);
        var prices = withoutOutliers.Select(candidate => candidate.UnitPrice).OrderBy(price => price).ToList();

        long? lowestPrice = prices.Count > 0 ? prices[0] : null;
        long? medianPrice = prices.Count > 0 ? CalculateMedian(prices) : null;
        long? averagePrice = prices.Count > 0
            ? Convert.ToInt64(Math.Round(prices.Average(price => (double)price)))
            : null;
        long? highestReasonablePrice = prices.Count > 0 ? prices[^1] : null;
        long? suggestedListPrice = lowestPrice.HasValue
            ? Math.Max(1L, lowestPrice.Value - SuggestedUndercutAmount)
            : null;

        long? estimatedFee = null;
        long? estimatedNet = null;
        if (suggestedListPrice.HasValue && withoutOutliers.Count > 0 && canSellOnFlea && fleaUnlocked)
        {
            try
            {
                var fee = ragfairTaxService.CalculateTax(
                    withoutOutliers[0].RootItem,
                    pmcProfile,
                    suggestedListPrice.Value,
                    1,
                    false);
                estimatedFee = Math.Max(0L, Convert.ToInt64(Math.Round(fee)));
                estimatedNet = Math.Max(0L, suggestedListPrice.Value - estimatedFee.Value);
            }
            catch
            {
                // Keep the market statistics even if a fee estimate cannot be produced.
            }
        }

        var buyRecommendation = BuildBuyRecommendation(
            fleaUnlocked,
            lowestPrice,
            cheapestTraderBuy?.Price,
            cheapestTraderBuy?.TraderName,
            prices.Count);
        var sellRecommendation = BuildSellRecommendation(
            fleaUnlocked,
            canSellOnFlea,
            estimatedNet,
            bestTraderSell?.RoubleEquivalent,
            bestTraderSell?.TraderName,
            prices.Count);

        var samples = withoutOutliers
            .Take(8)
            .Select(candidate => new HermesFleaOfferSample(
                candidate.UnitPrice,
                candidate.Quantity,
                Convert.ToInt32(Math.Round(candidate.Quality * 100d)),
                GetConditionLabel(candidate.Quality),
                candidate.SecondsRemaining))
            .ToList();

        return new HermesMarketSummaryResponse(
            true,
            null,
            item.ItemKey,
            item.Name,
            fleaUnlocked,
            playerLevel,
            requiredPlayerLevel,
            canSellOnFlea,
            sellUnavailableReason,
            candidates.Count,
            prices.Count,
            ignoredBarter,
            ignoredTrader,
            ignoredExpiredOrInvalid,
            ignoredLowCondition,
            ignoredOutliers,
            usedLowConditionFallback,
            lowestPrice,
            medianPrice,
            averagePrice,
            highestReasonablePrice,
            suggestedListPrice,
            estimatedFee,
            estimatedNet,
            cheapestTraderBuy?.Price,
            cheapestTraderBuy?.TraderName,
            bestTraderSell?.RoubleEquivalent,
            bestTraderSell?.TraderName,
            buyRecommendation,
            sellRecommendation,
            samples);
    }

    private long GetRoubleUnitPrice(RagfairOffer offer, OfferRequirement requirement)
    {
        if (offer.RequirementsCost is > 0)
        {
            return Math.Max(1L, Convert.ToInt64(Math.Round(offer.RequirementsCost.Value)));
        }

        var requirementCount = requirement.Count ?? 0d;
        if (requirementCount <= 0)
        {
            return 0L;
        }

        var roubles = handbookHelper.InRoubles(requirementCount, requirement.TemplateId);
        if (offer.SellInOnePiece == true && offer.Quantity > 1)
        {
            roubles /= offer.Quantity;
        }

        return Math.Max(0L, Convert.ToInt64(Math.Round(roubles)));
    }

    private static List<FleaCandidate> RemoveHighOutliers(
        IReadOnlyList<FleaCandidate> candidates,
        out int ignoredOutliers)
    {
        ignoredOutliers = 0;
        if (candidates.Count < 3)
        {
            return candidates.OrderBy(candidate => candidate.UnitPrice).ToList();
        }

        var sortedPrices = candidates.Select(candidate => candidate.UnitPrice).OrderBy(price => price).ToList();
        var median = CalculateMedian(sortedPrices);
        var maximumReasonable = Math.Max(median, Convert.ToInt64(Math.Round(median * OutlierMultiplier)));
        var output = candidates
            .Where(candidate => candidate.UnitPrice <= maximumReasonable)
            .OrderBy(candidate => candidate.UnitPrice)
            .ToList();
        ignoredOutliers = candidates.Count - output.Count;
        return output;
    }

    private static long CalculateMedian(IReadOnlyList<long> sortedPrices)
    {
        if (sortedPrices.Count == 0)
        {
            return 0L;
        }

        var middle = sortedPrices.Count / 2;
        if (sortedPrices.Count % 2 == 1)
        {
            return sortedPrices[middle];
        }

        return Convert.ToInt64(Math.Round((sortedPrices[middle - 1] + sortedPrices[middle]) / 2d));
    }

    private static CashTraderOffer? FindCheapestAvailableCashTraderOffer(
        IEnumerable<HermesPurchaseOffer> offers)
    {
        return offers
            .Where(offer => offer.IsAvailable)
            .SelectMany(offer => offer.PaymentOptions
                .Where(payment => payment.IsCash && payment.EstimatedRoubleValue > 0)
                .Select(payment => new CashTraderOffer(offer.TraderName, payment.EstimatedRoubleValue)))
            .OrderBy(offer => offer.Price)
            .FirstOrDefault();
    }

    private static string BuildBuyRecommendation(
        bool fleaUnlocked,
        long? fleaPrice,
        long? traderPrice,
        string? traderName,
        int comparableOfferCount)
    {
        if (!fleaUnlocked)
        {
            return "Flea purchasing is unavailable until the flea market is unlocked.";
        }

        if (!fleaPrice.HasValue)
        {
            return traderPrice.HasValue
                ? $"Buy from {traderName}; no valid local flea cash offer was found."
                : "No current cash purchase source was found.";
        }

        if (!traderPrice.HasValue)
        {
            return comparableOfferCount >= MinimumOffersForRecommendation
                ? "Buy from the local flea market; no currently available cash trader offer was found."
                : "The flea is the only cash source found, but there are too few comparable offers for a strong recommendation.";
        }

        var difference = Math.Abs(traderPrice.Value - fleaPrice.Value);
        if (fleaPrice.Value < traderPrice.Value)
        {
            return comparableOfferCount >= MinimumOffersForRecommendation
                ? $"Buy from the local flea market and save about ₽{difference:N0}."
                : $"The flea is about ₽{difference:N0} cheaper, but there are too few comparable offers for a strong recommendation.";
        }

        if (traderPrice.Value < fleaPrice.Value)
        {
            return $"Buy from {traderName} and save about ₽{difference:N0}.";
        }

        return $"The local flea and {traderName} are approximately the same price.";
    }

    private static string BuildSellRecommendation(
        bool fleaUnlocked,
        bool canSellOnFlea,
        long? estimatedNet,
        long? traderPrice,
        string? traderName,
        int comparableOfferCount)
    {
        if (!fleaUnlocked || !canSellOnFlea)
        {
            return traderPrice.HasValue
                ? $"Sell to {traderName}; this item cannot currently be listed on the flea market."
                : "No supported sale destination is currently available.";
        }

        if (!estimatedNet.HasValue)
        {
            return traderPrice.HasValue
                ? $"Sell to {traderName}; HERMES could not produce a reliable flea net estimate."
                : "A reliable sale recommendation is unavailable.";
        }

        if (comparableOfferCount < MinimumOffersForRecommendation)
        {
            return traderPrice.HasValue
                ? $"Too few comparable flea offers for a strong recommendation; the best trader estimate is {traderName} at ₽{traderPrice.Value:N0}."
                : "Too few comparable flea offers for a strong recommendation.";
        }

        if (!traderPrice.HasValue)
        {
            return $"List on the local flea market for an estimated net of ₽{estimatedNet.Value:N0}.";
        }

        var difference = Math.Abs(estimatedNet.Value - traderPrice.Value);
        if (estimatedNet.Value > traderPrice.Value)
        {
            return $"List on the local flea market for about ₽{difference:N0} more after the estimated fee.";
        }

        if (traderPrice.Value > estimatedNet.Value)
        {
            return $"Sell to {traderName} for about ₽{difference:N0} more than the estimated flea net.";
        }

        return $"The flea net and {traderName} sale estimate are approximately equal.";
    }

    private static string? GetSellUnavailableReason(
        bool fleaUnlocked,
        int playerLevel,
        int requiredPlayerLevel,
        bool canSellOnFlea)
    {
        if (!fleaUnlocked)
        {
            return $"Flea market unlocks at level {requiredPlayerLevel}; current level is {playerLevel}.";
        }

        if (!canSellOnFlea)
        {
            return "This item cannot be listed on the flea market.";
        }

        return null;
    }

    private static bool IsTraderOfferSafe(RagfairOffer offer)
    {
        try
        {
            return offer.IsTraderOffer();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedCashCurrency(MongoId templateId)
    {
        return templateId == RoublesTpl || templateId == DollarsTpl || templateId == EurosTpl;
    }

    private static string GetConditionLabel(double quality)
    {
        if (quality >= 0.99d)
        {
            return "Full";
        }

        if (quality >= MinimumComparableCondition)
        {
            return "Good";
        }

        return "Used";
    }

    private static HermesMarketSummaryResponse NotFound(string? itemKey, string message)
    {
        return new HermesMarketSummaryResponse(
            false,
            message,
            itemKey ?? string.Empty,
            string.Empty,
            false,
            0,
            0,
            false,
            message,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty,
            []);
    }

    private sealed record FleaCandidate(
        Item RootItem,
        long UnitPrice,
        int Quantity,
        double Quality,
        long SecondsRemaining);

    private sealed record CashTraderOffer(string TraderName, long Price);
}
