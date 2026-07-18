using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Services;

namespace Hermes.Server.Services;

internal sealed record HermesCraftFleaValuation(
    bool FleaUnlocked,
    bool CanSellOnFlea,
    long UnitNetValue);

internal sealed record HermesStashFleaValuation(
    bool Available,
    bool ReliableForRecommendation,
    int ComparableOfferCount,
    long? SuggestedListPrice,
    long? EstimatedListingFee,
    long? EstimatedNetSale,
    string Source,
    string? UnavailableReason);

[Injectable(InjectionType.Singleton)]
public sealed class HermesMarketService(
    RagfairOfferService ragfairOfferService,
    RagfairTaxService ragfairTaxService,
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    HandbookHelper handbookHelper,
    ItemHelper itemHelper,
    HermesCatalogService catalogService,
    HermesTraderService traderService,
    HermesMarketPriceService marketPriceService,
    HermesCacheService cacheService)
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

        if (cacheService.TryGetMarketSummary(item.ItemKey, sessionId, out var cachedSummary))
        {
            return cachedSummary;
        }

        var cacheGeneration = cacheService.Generation;
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

        var traderSummary = traderService.GetSummary(item.ItemKey, null, sessionId);
        var bestTraderSell = traderSummary.BestSellOffer;
        var cheapestTraderBuy = FindCheapestAvailableCashTraderOffer(traderSummary.PurchaseOffers);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = new List<FleaCandidate>();
        var validCashOffers = 0;
        var convertedBarterOffers = 0;
        var barterOffersUsingHandbookFallback = 0;
        var ignoredBarter = 0;
        var ignoredTrader = 0;
        var ignoredExpiredOrInvalid = 0;
        var requirementPriceCache = new Dictionary<string, HermesMarketUnitValuation>(StringComparer.OrdinalIgnoreCase);

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
            if (requirements.Count == 0)
            {
                ignoredExpiredOrInvalid++;
                continue;
            }

            if (!TryGetOfferUnitValue(
                    offer,
                    requirements,
                    item.TemplateId,
                    now,
                    requirementPriceCache,
                    out var offerValue))
            {
                ignoredBarter++;
                continue;
            }

            if (offerValue.IsBarter)
            {
                convertedBarterOffers++;
                if (offerValue.UsedHandbookFallback)
                {
                    barterOffersUsingHandbookFallback++;
                }
            }
            else
            {
                validCashOffers++;
            }

            var unitPrice = offerValue.UnitValue;

            double quality;
            try
            {
                quality = Math.Clamp(itemHelper.GetItemQualityModifier(rootItem), 0.01d, 1d);
            }
            catch
            {
                quality = 1d;
            }

            var assembly = AnalyzeOfferAssembly(
                rootItem,
                offer.Items,
                unitPrice,
                requirementPriceCache);
            var offerUsedHandbookFallback =
                offerValue.UsedHandbookFallback || assembly.UsedHandbookFallback;
            var priceSource = assembly.UsedHandbookFallback && !offerValue.UsedHandbookFallback
                ? offerValue.PriceSource + " with handbook fallback for installed components"
                : offerValue.PriceSource;

            candidates.Add(new FleaCandidate(
                rootItem,
                assembly.ListedUnitPrice,
                assembly.ComponentAdjustedUnitPrice,
                assembly.InstalledComponentValue,
                assembly.WeaponAttachmentCount,
                assembly.ArmorInsertCount,
                Math.Max(1, offer.Quantity),
                quality,
                Math.Max(0L, offer.EndTime.Value - now),
                offerValue.IsBarter,
                priceSource,
                offerValue.RequirementCount,
                offerUsedHandbookFallback));
        }

        // The shared market-price policy is a true source fallback chain.
        // Active cash offers are compared first. Converted barter offers become
        // the active comparison pool only when no cash offer could be valued.
        var sourceCandidates = candidates.Any(candidate => !candidate.IsBarter)
            ? candidates.Where(candidate => !candidate.IsBarter).ToList()
            : candidates.Where(candidate => candidate.IsBarter).ToList();
        var highCondition = sourceCandidates
            .Where(candidate => candidate.Quality >= MinimumComparableCondition)
            .OrderBy(candidate => candidate.ComponentAdjustedUnitPrice)
            .ToList();

        var usedLowConditionFallback = highCondition.Count == 0 && sourceCandidates.Count > 0;
        var comparisonPool = usedLowConditionFallback
            ? sourceCandidates.OrderBy(candidate => candidate.ComponentAdjustedUnitPrice).ToList()
            : highCondition;
        var ignoredLowCondition = usedLowConditionFallback
            ? 0
            : sourceCandidates.Count - highCondition.Count;

        var withoutOutliers = RemoveHighOutliers(comparisonPool, out var ignoredOutliers);
        var prices = withoutOutliers
            .Select(candidate => candidate.ComponentAdjustedUnitPrice)
            .OrderBy(price => price)
            .ToList();
        var bestComparableOffer = withoutOutliers
            .OrderBy(candidate => candidate.ComponentAdjustedUnitPrice)
            .ThenBy(candidate => candidate.ListedUnitPrice)
            .FirstOrDefault();
        var offersWithInstalledComponents = candidates.Count(candidate =>
            candidate.WeaponAttachmentCount > 0 || candidate.ArmorInsertCount > 0);
        var marketPriceFromActiveOffers = bestComparableOffer is not null;
        HermesMarketUnitValuation? fallbackMarketValue = marketPriceFromActiveOffers
            ? null
            : marketPriceService.GetBestUnitValue(item.TemplateId, requirementPriceCache);
        var marketPriceSource = bestComparableOffer?.PriceSource
                                ?? fallbackMarketValue?.Source
                                ?? "Unavailable";
        var marketPriceUsedHandbookFallback = bestComparableOffer?.UsedHandbookFallback
                                              ?? fallbackMarketValue?.UsedHandbookFallback
                                              ?? false;
        var lowestOfferIsBarter = bestComparableOffer?.IsBarter ?? false;

        long? lowestPrice = prices.Count > 0
            ? prices[0]
            : fallbackMarketValue is { UnitValue: > 0 }
                ? fallbackMarketValue.UnitValue
                : null;
        long? lowestListedPrice = bestComparableOffer?.ListedUnitPrice;
        long? medianPrice = prices.Count > 0
            ? CalculateMedian(prices)
            : lowestPrice;
        long? averagePrice = prices.Count > 0
            ? Convert.ToInt64(Math.Round(prices.Average(price => (double)price)))
            : lowestPrice;
        long? highestReasonablePrice = prices.Count > 0 ? prices[^1] : lowestPrice;
        long? suggestedListPrice = marketPriceFromActiveOffers && lowestPrice.HasValue
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
            lowestListedPrice,
            lowestOfferIsBarter,
            marketPriceSource,
            marketPriceFromActiveOffers,
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
            .OrderBy(candidate => candidate.ComponentAdjustedUnitPrice)
            .ThenBy(candidate => candidate.ListedUnitPrice)
            .Take(8)
            .Select(candidate => new HermesFleaOfferSample(
                candidate.IsBarter,
                candidate.PriceSource,
                candidate.BarterRequirementCount,
                candidate.UsedHandbookFallback,
                candidate.ComponentAdjustedUnitPrice,
                candidate.ListedUnitPrice,
                candidate.InstalledComponentValue,
                candidate.WeaponAttachmentCount,
                candidate.ArmorInsertCount,
                candidate.Quantity,
                Convert.ToInt32(Math.Round(candidate.Quality * 100d)),
                GetConditionLabel(candidate.Quality),
                candidate.SecondsRemaining))
            .ToList();

        var response = new HermesMarketSummaryResponse(
            true,
            null,
            item.ItemKey,
            item.Name,
            fleaUnlocked,
            playerLevel,
            requiredPlayerLevel,
            canSellOnFlea,
            sellUnavailableReason,
            validCashOffers,
            convertedBarterOffers,
            barterOffersUsingHandbookFallback,
            prices.Count,
            ignoredBarter,
            ignoredTrader,
            ignoredExpiredOrInvalid,
            ignoredLowCondition,
            ignoredOutliers,
            usedLowConditionFallback,
            offersWithInstalledComponents,
            marketPriceSource,
            marketPriceFromActiveOffers,
            marketPriceUsedHandbookFallback,
            lowestListedPrice,
            lowestOfferIsBarter,
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

        cacheService.SetMarketSummary(item.ItemKey, sessionId, response, cacheGeneration);
        return response;
    }

    internal HermesCraftFleaValuation GetCraftEstimate(
        MongoId itemTemplateId,
        MongoId sessionId)
    {
        var pmcProfile = profileHelper.GetPmcProfile(sessionId);
        if (pmcProfile is null)
        {
            return new HermesCraftFleaValuation(false, false, 0L);
        }

        var requiredPlayerLevel = databaseService.GetGlobals().Configuration.RagFair.MinUserLevel;
        var fleaUnlocked = (pmcProfile.Info?.Level ?? 1) >= requiredPlayerLevel;
        if (!fleaUnlocked)
        {
            return new HermesCraftFleaValuation(false, false, 0L);
        }

        if (!databaseService.GetItems().TryGetValue(itemTemplateId, out var itemTemplate)
            || itemTemplate.Properties?.CanSellOnRagfair != true)
        {
            return new HermesCraftFleaValuation(true, false, 0L);
        }

        IEnumerable<RagfairOffer> offers;
        try
        {
            offers = ragfairOfferService.GetOffersOfType(itemTemplateId) ?? [];
        }
        catch
        {
            offers = [];
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requirementPriceCache = new Dictionary<string, HermesMarketUnitValuation>(
            StringComparer.OrdinalIgnoreCase);
        var lowestGross = long.MaxValue;
        var lowestNet = 0L;

        foreach (var offer in offers)
        {
            if (IsTraderOfferSafe(offer)
                || offer.Locked == true
                || offer.Quantity <= 0
                || offer.EndTime is null
                || offer.EndTime <= now
                || offer.Items is null
                || offer.Items.Count != 1)
            {
                continue;
            }

            var rootItem = offer.Items[0];
            if (rootItem.Template != itemTemplateId)
            {
                continue;
            }

            var requirements = offer.Requirements?.ToList() ?? [];
            if (requirements.Count == 0
                || !TryGetOfferUnitValue(
                    offer,
                    requirements,
                    itemTemplateId,
                    now,
                    requirementPriceCache,
                    out var offerValue)
                || offerValue.UnitValue <= 0
                || offerValue.UnitValue >= lowestGross)
            {
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

            if (quality < MinimumComparableCondition)
            {
                continue;
            }

            var suggested = Math.Max(1L, offerValue.UnitValue - SuggestedUndercutAmount);
            try
            {
                var fee = ragfairTaxService.CalculateTax(rootItem, pmcProfile, suggested, 1, false);
                lowestGross = offerValue.UnitValue;
                lowestNet = Math.Max(0L, suggested - Convert.ToInt64(Math.Round(fee)));
            }
            catch
            {
                // A fee-backed net value is required before Flea can beat a trader.
            }
        }

        return new HermesCraftFleaValuation(true, true, lowestNet);
    }

    internal HermesStashFleaValuation GetStashEstimate(
        HermesStashAnalysisEntry entry,
        MongoId sessionId)
    {
        var pmcProfile = profileHelper.GetPmcProfile(sessionId);
        if (pmcProfile is null)
        {
            return UnavailableStashEstimate("HERMES could not read the active PMC profile.");
        }

        var requiredPlayerLevel = databaseService.GetGlobals().Configuration.RagFair.MinUserLevel;
        if ((pmcProfile.Info?.Level ?? 1) < requiredPlayerLevel)
        {
            return UnavailableStashEstimate($"Flea market unlocks at level {requiredPlayerLevel}.");
        }

        if (!databaseService.GetItems().TryGetValue(entry.RootTemplateId, out var rootTemplate)
            || rootTemplate.Properties?.CanSellOnRagfair != true)
        {
            return UnavailableStashEstimate("This item cannot be listed on the flea market.");
        }

        var summary = GetSummary(entry.ItemKey, sessionId);
        if (!summary.Found)
        {
            return UnavailableStashEstimate(summary.Message ?? "No current flea analysis was available.");
        }

        if (!summary.FleaUnlocked || !summary.CanSellOnFlea)
        {
            return UnavailableStashEstimate(
                summary.SellUnavailableReason ?? "This item cannot currently be listed on the flea market.",
                summary.ComparableOfferCount);
        }

        if (!summary.SuggestedListPrice.HasValue
            || !summary.EstimatedListingFee.HasValue
            || summary.ComparableOfferCount <= 0)
        {
            if (summary.LowestPrice is > 0)
            {
                return new HermesStashFleaValuation(
                    true,
                    false,
                    Math.Max(0, summary.ComparableOfferCount),
                    summary.LowestPrice,
                    null,
                    null,
                    summary.MarketPriceSource,
                    $"No active comparable offer was available for a fee-backed estimate. {summary.MarketPriceSource} is shown as an informational market reference only.");
            }

            return UnavailableStashEstimate(
                "HERMES could not resolve an active flea offer, converted barter, SPT dynamic price, or handbook fallback for this item.",
                summary.ComparableOfferCount);
        }

        var root = entry.Components.FirstOrDefault(component => component.Kind == HermesSaleComponentKind.Root);
        if (root is null)
        {
            return UnavailableStashEstimate("The stash item had no supported root valuation component.", summary.ComparableOfferCount);
        }

        var rootReference = catalogService.GetReferencePrice(root.TemplateId);
        if (rootReference is null or <= 0)
        {
            return UnavailableStashEstimate("The stash item had no positive root handbook reference price.", summary.ComparableOfferCount);
        }

        var rootMultiplier = Math.Max(0d, root.ConditionAdjustedReferenceValue / (double)rootReference.Value);
        var estimatedListingValue = summary.SuggestedListPrice.Value * rootMultiplier;
        var usedHandbookFallback = false;
        var pricedInstalledComponents = 0;
        var componentCache = new Dictionary<string, HermesMarketUnitValuation>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in entry.Components.Where(component => component.Kind != HermesSaleComponentKind.Root))
        {
            var referencePrice = catalogService.GetReferencePrice(component.TemplateId);
            if (referencePrice is null or <= 0)
            {
                continue;
            }

            var marketValue = marketPriceService.GetBestUnitValue(component.TemplateId, componentCache);
            if (marketValue.UnitValue <= 0L)
            {
                continue;
            }

            var componentMultiplier = Math.Max(0d, component.ConditionAdjustedReferenceValue / (double)referencePrice.Value);
            estimatedListingValue += marketValue.UnitValue * componentMultiplier;
            usedHandbookFallback |= marketValue.UsedHandbookFallback;
            pricedInstalledComponents++;
        }

        var suggestedListPrice = Math.Max(1L, Convert.ToInt64(Math.Round(estimatedListingValue)));
        var baseFeeRatio = summary.SuggestedListPrice.Value > 0L
            ? Math.Clamp(summary.EstimatedListingFee.Value / (double)summary.SuggestedListPrice.Value, 0d, 0.95d)
            : 0d;
        var estimatedFee = Math.Max(0L, Convert.ToInt64(Math.Round(suggestedListPrice * baseFeeRatio)));
        var estimatedNet = Math.Max(0L, suggestedListPrice - estimatedFee);
        var reliable = summary.ComparableOfferCount >= MinimumOffersForRecommendation;
        var source = pricedInstalledComponents > 0
            ? usedHandbookFallback
                ? summary.MarketPriceSource + " with installed-component handbook fallback"
                : summary.MarketPriceSource + " with installed-component market values"
            : summary.MarketPriceSource;

        return new HermesStashFleaValuation(
            true,
            reliable,
            summary.ComparableOfferCount,
            suggestedListPrice,
            estimatedFee,
            estimatedNet,
            source,
            reliable ? null : "Fewer than three comparable flea offers were available; the estimate is informational only.");
    }

    private static HermesStashFleaValuation UnavailableStashEstimate(
        string reason,
        int comparableOfferCount = 0)
    {
        return new HermesStashFleaValuation(
            false,
            false,
            Math.Max(0, comparableOfferCount),
            null,
            null,
            null,
            "Unavailable",
            reason);
    }

    private bool TryGetOfferUnitValue(
        RagfairOffer offer,
        IReadOnlyList<OfferRequirement> requirements,
        MongoId offeredTemplateId,
        long now,
        IDictionary<string, HermesMarketUnitValuation> requirementPriceCache,
        out OfferValue valuation)
    {
        var isCash = requirements.Count == 1 && IsSupportedCashCurrency(requirements[0].TemplateId);
        if (isCash)
        {
            var cashValue = GetRoubleUnitPrice(offer, requirements[0]);
            valuation = new OfferValue(
                cashValue,
                false,
                "Active local flea offer",
                1,
                false);
            return cashValue > 0;
        }

        double totalRequirementValue = 0d;
        var usedHandbookFallback = false;
        var usedDynamicPrice = false;
        foreach (var requirement in requirements)
        {
            var count = requirement.Count ?? 0d;
            if (count <= 0d)
            {
                valuation = default!;
                return false;
            }

            if (IsSupportedCashCurrency(requirement.TemplateId))
            {
                var currencyValue = handbookHelper.InRoubles(count, requirement.TemplateId);
                if (currencyValue <= 0d)
                {
                    valuation = default!;
                    return false;
                }

                totalRequirementValue += currencyValue;
                continue;
            }

            var requirementValue = marketPriceService.GetBestUnitValue(
                requirement.TemplateId,
                requirementPriceCache);
            if (requirementValue.UnitValue <= 0L)
            {
                valuation = default!;
                return false;
            }

            totalRequirementValue += requirementValue.UnitValue * count;
            usedHandbookFallback |= requirementValue.UsedHandbookFallback;
            usedDynamicPrice |= requirementValue.Source.Contains(
                "dynamic flea",
                StringComparison.OrdinalIgnoreCase);
        }

        if (offer.SellInOnePiece == true && offer.Quantity > 1)
        {
            totalRequirementValue /= offer.Quantity;
        }

        var roundedValue = Math.Max(0L, Convert.ToInt64(Math.Round(totalRequirementValue)));
        var source = usedHandbookFallback
            ? "Converted flea barter offer with handbook fallback"
            : usedDynamicPrice
                ? "Converted flea barter offer using SPT dynamic flea-market price"
                : "Converted flea barter offer";

        valuation = new OfferValue(
            roundedValue,
            true,
            source,
            requirements.Count,
            usedHandbookFallback);
        return roundedValue > 0L;
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

    private OfferAssemblyValuation AnalyzeOfferAssembly(
        Item rootItem,
        IReadOnlyCollection<Item> offerItems,
        long listedUnitPrice,
        IDictionary<string, HermesMarketUnitValuation> componentPriceCache)
    {
        var childrenByParent = offerItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentId))
            .GroupBy(item => item.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<Item>();
        queue.Enqueue(rootItem);

        double installedValue = 0d;
        var weaponAttachmentCount = 0;
        var armorInsertCount = 0;
        var usedHandbookFallback = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rootItem.Id.ToString()
        };

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parent.Id.ToString(), out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child.Id.ToString()) || IsStoredOrConsumableContent(child))
                {
                    continue;
                }

                queue.Enqueue(child);

                var isArmorInsert = IsArmorInsert(child);
                if (isArmorInsert)
                {
                    armorInsertCount++;
                }
                else
                {
                    weaponAttachmentCount++;
                }

                var componentValue = marketPriceService.GetBestUnitValue(
                    child.Template,
                    componentPriceCache);
                if (componentValue.UnitValue <= 0L)
                {
                    continue;
                }

                var quantity = Math.Max(1d, child.Upd?.StackObjectsCount ?? 1d);
                double quality;
                try
                {
                    quality = Math.Clamp(itemHelper.GetItemQualityModifier(child), 0.01d, 1d);
                }
                catch
                {
                    quality = 1d;
                }

                installedValue += componentValue.UnitValue * quantity * quality;
                usedHandbookFallback |= componentValue.UsedHandbookFallback;
            }
        }

        var roundedInstalledValue = Math.Max(0L, Convert.ToInt64(Math.Floor(installedValue)));
        var adjustedUnitPrice = Math.Max(1L, listedUnitPrice - roundedInstalledValue);

        return new OfferAssemblyValuation(
            listedUnitPrice,
            adjustedUnitPrice,
            roundedInstalledValue,
            weaponAttachmentCount,
            armorInsertCount,
            usedHandbookFallback);
    }

    private bool IsArmorInsert(Item item)
    {
        var slotId = item.SlotId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (slotId.Contains("plate", StringComparison.Ordinal)
            || slotId.Contains("soft_armor", StringComparison.Ordinal)
            || itemHelper.IsSoftInsertId(slotId))
        {
            return true;
        }

        return databaseService.GetItems().TryGetValue(item.Template, out var template)
               && (template.Properties?.ArmorClass ?? 0) > 0;
    }

    private static bool IsStoredOrConsumableContent(Item item)
    {
        if (item.Location is not null)
        {
            return true;
        }

        var slotId = item.SlotId?.Trim().ToLowerInvariant() ?? string.Empty;
        return slotId.Contains("cartridge", StringComparison.Ordinal)
               || slotId.Contains("cartridges", StringComparison.Ordinal)
               || slotId.Contains("chamber", StringComparison.Ordinal)
               || slotId.Contains("patron", StringComparison.Ordinal)
               || slotId.Contains("ammo", StringComparison.Ordinal);
    }

    private static List<FleaCandidate> RemoveHighOutliers(
        IReadOnlyList<FleaCandidate> candidates,
        out int ignoredOutliers)
    {
        ignoredOutliers = 0;
        if (candidates.Count < 3)
        {
            return candidates.OrderBy(candidate => candidate.ComponentAdjustedUnitPrice).ToList();
        }

        var sortedPrices = candidates.Select(candidate => candidate.ComponentAdjustedUnitPrice).OrderBy(price => price).ToList();
        var median = CalculateMedian(sortedPrices);
        var maximumReasonable = Math.Max(median, Convert.ToInt64(Math.Round(median * OutlierMultiplier)));
        var output = candidates
            .Where(candidate => candidate.ComponentAdjustedUnitPrice <= maximumReasonable)
            .OrderBy(candidate => candidate.ComponentAdjustedUnitPrice)
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
        long? componentAdjustedFleaPrice,
        long? listedFleaPrice,
        bool lowestOfferIsBarter,
        string marketPriceSource,
        bool marketPriceFromActiveOffers,
        long? traderPrice,
        string? traderName,
        int comparableOfferCount)
    {
        if (!fleaUnlocked)
        {
            return "Flea purchasing is unavailable until the flea market is unlocked.";
        }

        if (!componentAdjustedFleaPrice.HasValue)
        {
            return traderPrice.HasValue
                ? $"Buy from {traderName}; no flea or fallback market value could be resolved."
                : "No current purchase source could be valued.";
        }

        if (!marketPriceFromActiveOffers)
        {
            var marketText = $"Market reference: ₽{componentAdjustedFleaPrice.Value:N0} ({marketPriceSource}).";
            if (!traderPrice.HasValue)
            {
                return marketText + " No currently available cash trader offer was found.";
            }

            var fallbackDifference = Math.Abs(traderPrice.Value - componentAdjustedFleaPrice.Value);
            return traderPrice.Value <= componentAdjustedFleaPrice.Value
                ? $"{marketText} Buy from {traderName}; the trader is about ₽{fallbackDifference:N0} cheaper or equal."
                : $"{marketText} It is about ₽{fallbackDifference:N0} below {traderName}, but it is a fallback reference rather than an active listing.";
        }

        var offerKind = lowestOfferIsBarter ? "converted barter offer" : "cash offer";
        var fleaPriceText = listedFleaPrice.HasValue
            && listedFleaPrice.Value != componentAdjustedFleaPrice.Value
                ? $"The best comparable flea {offerKind} has requirements valued at ₽{listedFleaPrice.Value:N0} total and a component-adjusted base-item value of ₽{componentAdjustedFleaPrice.Value:N0}."
                : $"The lowest comparable flea {offerKind} is valued at ₽{componentAdjustedFleaPrice.Value:N0}.";

        if (!traderPrice.HasValue)
        {
            return comparableOfferCount >= MinimumOffersForRecommendation
                ? $"{fleaPriceText} No currently available cash trader offer was found."
                : $"{fleaPriceText} There are too few comparable offers for a strong recommendation.";
        }

        var difference = Math.Abs(traderPrice.Value - componentAdjustedFleaPrice.Value);
        if (componentAdjustedFleaPrice.Value < traderPrice.Value)
        {
            return comparableOfferCount >= MinimumOffersForRecommendation
                ? $"{fleaPriceText} Its base-item equivalent is about ₽{difference:N0} cheaper than {traderName}."
                : $"{fleaPriceText} Its base-item equivalent is about ₽{difference:N0} cheaper than {traderName}, but there are too few comparable offers for a strong recommendation.";
        }

        if (traderPrice.Value < componentAdjustedFleaPrice.Value)
        {
            return $"{fleaPriceText} Buy from {traderName}; the trader is about ₽{difference:N0} cheaper than the component-adjusted flea value.";
        }

        return $"{fleaPriceText} The component-adjusted flea value and {traderName} are approximately equal.";
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
            0,
            0,
            false,
            0,
            string.Empty,
            false,
            false,
            null,
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
        long ListedUnitPrice,
        long ComponentAdjustedUnitPrice,
        long InstalledComponentValue,
        int WeaponAttachmentCount,
        int ArmorInsertCount,
        int Quantity,
        double Quality,
        long SecondsRemaining,
        bool IsBarter,
        string PriceSource,
        int BarterRequirementCount,
        bool UsedHandbookFallback);

    private sealed record OfferValue(
        long UnitValue,
        bool IsBarter,
        string PriceSource,
        int RequirementCount,
        bool UsedHandbookFallback);

    private sealed record OfferAssemblyValuation(
        long ListedUnitPrice,
        long ComponentAdjustedUnitPrice,
        long InstalledComponentValue,
        int WeaponAttachmentCount,
        int ArmorInsertCount,
        bool UsedHandbookFallback);

    private sealed record CashTraderOffer(string TraderName, long Price);
}
