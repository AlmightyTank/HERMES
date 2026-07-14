using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Services;

namespace Hermes.Server.Services;

/// <summary>
/// Produces a current local-market unit value for an item template.
/// Cash flea offers are used directly. Barter flea offers are recursively
/// converted from their requirements. Handbook value is only a fallback.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesMarketPriceService(
    RagfairOfferService ragfairOfferService,
    DatabaseService databaseService,
    HandbookHelper handbookHelper,
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
            return GetHandbookFallback(templateId, "Handbook fallback (barter cycle or depth limit)");
        }

        var candidates = new List<HermesMarketUnitValuation>();
        IEnumerable<RagfairOffer> offers;
        try
        {
            offers = ragfairOfferService.GetOffersOfType(templateId) ?? [];
        }
        catch
        {
            offers = [];
        }

        foreach (var offer in offers)
        {
            if (IsTraderOfferSafe(offer)
                || offer.Locked == true
                || offer.Quantity <= 0
                || offer.EndTime is null
                || offer.EndTime <= now
                || offer.Items is null
                || offer.Items.Count == 0)
            {
                continue;
            }

            var rootItem = offer.Items.FirstOrDefault(item => item.Id == offer.Root) ?? offer.Items[0];
            if (rootItem.Template != templateId)
            {
                continue;
            }

            var requirements = offer.Requirements?.ToList() ?? [];
            if (requirements.Count == 0)
            {
                continue;
            }

            if (!TryConvertOffer(
                    offer,
                    requirements,
                    now,
                    cache,
                    recursionPath,
                    cacheGeneration,
                    depth,
                    out var converted))
            {
                continue;
            }

            var adjusted = AdjustForInstalledComponents(rootItem, offer.Items, converted.UnitValue);
            if (adjusted <= 0L)
            {
                continue;
            }

            candidates.Add(converted with { UnitValue = adjusted });
        }

        recursionPath.Remove(key);

        HermesMarketUnitValuation result;
        if (candidates.Count > 0)
        {
            result = candidates
                .OrderBy(candidate => candidate.UnitValue)
                .ThenBy(candidate => candidate.UsedHandbookFallback)
                .First();
        }
        else
        {
            result = GetHandbookFallback(templateId, "Handbook fallback (no current local flea offer)");
        }

        cache[key] = result;
        cacheService.SetMarketUnitValue(templateId, result, cacheGeneration);
        return result;
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
                "Current local flea cash offer",
                false,
                false);
            return unitValue > 0L;
        }

        double totalValue = 0d;
        var usedHandbookFallback = false;
        var usedConvertedBarter = false;

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
            usedConvertedBarter = true;
        }

        if (offer.SellInOnePiece == true && offer.Quantity > 1)
        {
            totalValue /= offer.Quantity;
        }

        var rounded = Math.Max(0L, Convert.ToInt64(Math.Round(totalValue)));
        var source = usedHandbookFallback
            ? "Converted local flea barter with handbook fallback"
            : "Converted local flea barter";

        valuation = new HermesMarketUnitValuation(
            rounded,
            source,
            usedHandbookFallback,
            usedConvertedBarter);
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

    private long AdjustForInstalledComponents(
        Item rootItem,
        IReadOnlyCollection<Item> offerItems,
        long listedUnitValue)
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

                var referencePrice = catalogService.GetReferencePrice(child.Template) ?? 0L;
                if (referencePrice <= 0L)
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

                installedValue += referencePrice * quantity * quality;
            }
        }

        var roundedInstalledValue = Math.Max(0L, Convert.ToInt64(Math.Floor(installedValue)));
        return Math.Max(1L, listedUnitValue - roundedInstalledValue);
    }

    private HermesMarketUnitValuation GetHandbookFallback(MongoId templateId, string source)
    {
        var handbookValue = catalogService.GetReferencePrice(templateId)
                            ?? Math.Max(0L, Convert.ToInt64(Math.Round(
                                handbookHelper.GetTemplatePrice(templateId))));
        return new HermesMarketUnitValuation(
            handbookValue,
            source,
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
}

public sealed record HermesMarketUnitValuation(
    long UnitValue,
    string Source,
    bool UsedHandbookFallback,
    bool IsConvertedBarter);
