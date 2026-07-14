using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Services;

namespace Hermes.Server.Services;

/// <summary>
/// Produces one authoritative current-market unit value for an item template.
/// Every flea-backed HERMES calculation uses the same fallback order:
/// 1. active local cash flea offer;
/// 2. converted active flea barter offer;
/// 3. SPT dynamic flea-market price;
/// 4. handbook value.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesMarketPriceService(
    RagfairOfferService ragfairOfferService,
    HandbookHelper handbookHelper,
    RagfairPriceService ragfairPriceService,
    ItemHelper itemHelper,
    HermesCatalogService catalogService,
    HermesCacheService cacheService)
{
    private const int MaximumBarterDepth = 4;

    private static readonly MongoId RoublesTpl = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId DollarsTpl = new("5696686a4bdc2da3298b456a");
    private static readonly MongoId EurosTpl = new("569668774bdc2da2298b4568");

    public HermesMarketUnitValuation GetBestUnitValue(
        MongoId templateId,
        IDictionary<string, HermesMarketUnitValuation>? cache = null)
    {
        cache ??= new Dictionary<string, HermesMarketUnitValuation>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cacheGeneration = cacheService.Generation;
        return GetBestUnitValueInternal(
            templateId,
            now,
            cache,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cacheGeneration,
            0);
    }

    private HermesMarketUnitValuation GetBestUnitValueInternal(
        MongoId templateId,
        long now,
        IDictionary<string, HermesMarketUnitValuation> cache,
        ISet<string> recursionPath,
        long cacheGeneration,
        int depth)
    {
        var key = templateId.ToString();
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (cacheService.TryGetMarketUnitValue(templateId, out var sharedCached))
        {
            cache[key] = sharedCached;
            return sharedCached;
        }

        if (depth > MaximumBarterDepth || !recursionPath.Add(key))
        {
            return GetDynamicOrHandbookFallback(templateId);
        }

        var activeOffers = GetActiveLocalOffers(templateId, now);
        var cashCandidates = new List<HermesMarketUnitValuation>();
        var barterOffers = new List<(RagfairOffer Offer, Item Root, IReadOnlyList<OfferRequirement> Requirements)>();

        foreach (var offer in activeOffers)
        {
            var rootItem = offer.Items!.FirstOrDefault(item => item.Id == offer.Root) ?? offer.Items[0];
            var requirements = offer.Requirements?.ToList() ?? [];
            if (requirements.Count == 0)
            {
                continue;
            }

            if (requirements.Count == 1 && IsSupportedCashCurrency(requirements[0].TemplateId))
            {
                if (!TryConvertOffer(
                        offer,
                        requirements,
                        now,
                        cache,
                        recursionPath,
                        cacheGeneration,
                        depth,
                        out var cashValue))
                {
                    continue;
                }

                var adjustment = AdjustForInstalledComponents(
                    rootItem,
                    offer.Items,
                    cashValue.UnitValue,
                    now,
                    cache,
                    recursionPath,
                    cacheGeneration,
                    depth);
                if (adjustment.AdjustedUnitValue <= 0L)
                {
                    continue;
                }

                cashCandidates.Add(cashValue with
                {
                    UnitValue = adjustment.AdjustedUnitValue,
                    UsedHandbookFallback = cashValue.UsedHandbookFallback || adjustment.UsedHandbookFallback
                });
            }
            else
            {
                barterOffers.Add((offer, rootItem, requirements));
            }
        }

        HermesMarketUnitValuation result;
        if (cashCandidates.Count > 0)
        {
            result = cashCandidates
                .OrderBy(candidate => candidate.UnitValue)
                .ThenBy(candidate => candidate.UsedHandbookFallback)
                .First();
        }
        else
        {
            var barterCandidates = new List<HermesMarketUnitValuation>();
            foreach (var barter in barterOffers)
            {
                if (!TryConvertOffer(
                        barter.Offer,
                        barter.Requirements,
                        now,
                        cache,
                        recursionPath,
                        cacheGeneration,
                        depth,
                        out var barterValue))
                {
                    continue;
                }

                var adjustment = AdjustForInstalledComponents(
                    barter.Root,
                    barter.Offer.Items!,
                    barterValue.UnitValue,
                    now,
                    cache,
                    recursionPath,
                    cacheGeneration,
                    depth);
                if (adjustment.AdjustedUnitValue <= 0L)
                {
                    continue;
                }

                barterCandidates.Add(barterValue with
                {
                    UnitValue = adjustment.AdjustedUnitValue,
                    UsedHandbookFallback = barterValue.UsedHandbookFallback || adjustment.UsedHandbookFallback
                });
            }

            result = barterCandidates.Count > 0
                ? barterCandidates
                    .OrderBy(candidate => candidate.UnitValue)
                    .ThenBy(candidate => candidate.UsedHandbookFallback)
                    .First()
                : GetDynamicOrHandbookFallback(templateId);
        }

        recursionPath.Remove(key);
        cache[key] = result;
        cacheService.SetMarketUnitValue(templateId, result, cacheGeneration);
        return result;
    }

    private IReadOnlyList<RagfairOffer> GetActiveLocalOffers(MongoId templateId, long now)
    {
        IEnumerable<RagfairOffer> offers;
        try
        {
            offers = ragfairOfferService.GetOffersOfType(templateId) ?? [];
        }
        catch
        {
            return [];
        }

        return offers
            .Where(offer => !IsTraderOfferSafe(offer)
                            && offer.Locked != true
                            && offer.Quantity > 0
                            && offer.EndTime.HasValue
                            && offer.EndTime.Value > now
                            && offer.Items is { Count: > 0 })
            .Where(offer =>
            {
                var root = offer.Items!.FirstOrDefault(item => item.Id == offer.Root) ?? offer.Items[0];
                return root.Template == templateId;
            })
            .ToList();
    }

    private bool TryConvertOffer(
        RagfairOffer offer,
        IReadOnlyList<OfferRequirement> requirements,
        long now,
        IDictionary<string, HermesMarketUnitValuation> cache,
        ISet<string> recursionPath,
        long cacheGeneration,
        int depth,
        out HermesMarketUnitValuation valuation)
    {
        var isCash = requirements.Count == 1 && IsSupportedCashCurrency(requirements[0].TemplateId);
        if (isCash)
        {
            var unitValue = GetRoubleUnitPrice(offer, requirements[0]);
            valuation = new HermesMarketUnitValuation(
                unitValue,
                "Active local flea offer",
                false,
                false);
            return unitValue > 0L;
        }

        double totalValue = 0d;
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

                totalValue += currencyValue;
                continue;
            }

            var requirementValue = GetBestUnitValueInternal(
                requirement.TemplateId,
                now,
                cache,
                recursionPath,
                cacheGeneration,
                depth + 1);
            if (requirementValue.UnitValue <= 0L)
            {
                valuation = default!;
                return false;
            }

            totalValue += requirementValue.UnitValue * count;
            usedHandbookFallback |= requirementValue.UsedHandbookFallback;
            usedDynamicPrice |= requirementValue.Source.Contains(
                "dynamic flea",
                StringComparison.OrdinalIgnoreCase);
        }

        if (offer.SellInOnePiece == true && offer.Quantity > 1)
        {
            totalValue /= offer.Quantity;
        }

        var rounded = Math.Max(0L, Convert.ToInt64(Math.Round(totalValue)));
        var source = usedHandbookFallback
            ? "Converted flea barter offer with handbook fallback"
            : usedDynamicPrice
                ? "Converted flea barter offer using SPT dynamic flea-market price"
                : "Converted flea barter offer";
        valuation = new HermesMarketUnitValuation(
            rounded,
            source,
            usedHandbookFallback,
            true);
        return rounded > 0L;
    }

    private long GetRoubleUnitPrice(RagfairOffer offer, OfferRequirement requirement)
    {
        if (offer.RequirementsCost is > 0)
        {
            return Math.Max(1L, Convert.ToInt64(Math.Round(offer.RequirementsCost.Value)));
        }

        var count = requirement.Count ?? 0d;
        if (count <= 0d)
        {
            return 0L;
        }

        var roubles = handbookHelper.InRoubles(count, requirement.TemplateId);
        if (offer.SellInOnePiece == true && offer.Quantity > 1)
        {
            roubles /= offer.Quantity;
        }

        return Math.Max(0L, Convert.ToInt64(Math.Round(roubles)));
    }

    private ComponentAdjustment AdjustForInstalledComponents(
        Item rootItem,
        IReadOnlyCollection<Item> offerItems,
        long listedUnitValue,
        long now,
        IDictionary<string, HermesMarketUnitValuation> cache,
        ISet<string> recursionPath,
        long cacheGeneration,
        int depth)
    {
        var childrenByParent = offerItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentId))
            .GroupBy(item => item.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<Item>();
        queue.Enqueue(rootItem);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rootItem.Id.ToString()
        };
        double installedValue = 0d;
        var usedHandbookFallback = false;

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

                var componentValue = GetBestUnitValueInternal(
                    child.Template,
                    now,
                    cache,
                    recursionPath,
                    cacheGeneration,
                    depth + 1);
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
        return new ComponentAdjustment(
            Math.Max(1L, listedUnitValue - roundedInstalledValue),
            usedHandbookFallback);
    }

    private HermesMarketUnitValuation GetDynamicOrHandbookFallback(MongoId templateId)
    {
        double? dynamicPrice;
        try
        {
            dynamicPrice = ragfairPriceService.GetDynamicPriceForItem(templateId);
        }
        catch
        {
            dynamicPrice = null;
        }

        if (dynamicPrice is > 0d)
        {
            return new HermesMarketUnitValuation(
                Math.Max(1L, Convert.ToInt64(Math.Round(dynamicPrice.Value))),
                "SPT dynamic flea-market price",
                false,
                false);
        }

        var handbookValue = catalogService.GetReferencePrice(templateId)
                            ?? Math.Max(0L, Convert.ToInt64(Math.Round(
                                handbookHelper.GetTemplatePrice(templateId))));
        return new HermesMarketUnitValuation(
            handbookValue,
            "Handbook fallback",
            handbookValue > 0L,
            false);
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

    private sealed record ComponentAdjustment(
        long AdjustedUnitValue,
        bool UsedHandbookFallback);
}

public sealed record HermesMarketUnitValuation(
    long UnitValue,
    string Source,
    bool UsedHandbookFallback,
    bool IsConvertedBarter);
