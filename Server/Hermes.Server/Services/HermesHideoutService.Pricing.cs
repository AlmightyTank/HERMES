using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

public sealed partial class HermesHideoutService
{
    private AcquisitionEstimate EstimateAcquisition(
        HermesCatalogItem item,
        MongoId sessionId,
        long? referencePrice)
    {
        var currentSources = new List<AcquisitionEstimate>();

        try
        {
            var traders = traderService.GetSummary(item.ItemKey, null, sessionId);
            foreach (var offer in traders.PurchaseOffers.Where(offer => offer.IsAvailable))
            {
                foreach (var payment in offer.PaymentOptions.Where(payment => payment.EstimatedRoubleValue > 0))
                {
                    currentSources.Add(new AcquisitionEstimate(
                        payment.IsCash ? offer.TraderName : $"{offer.TraderName} barter",
                        payment.EstimatedRoubleValue));
                }
            }

            var market = marketService.GetSummary(item.ItemKey, sessionId);
            if (market.FleaUnlocked
                && market.MarketPriceFromActiveOffers
                && market.LowestPrice is > 0)
            {
                currentSources.Add(new AcquisitionEstimate(
                    market.MarketPriceSource,
                    market.LowestPrice.Value));
            }
        }
        catch
        {
            // The shared market resolver below still provides dynamic/handbook fallback.
        }

        var current = currentSources
            .Where(candidate => candidate.UnitPrice > 0)
            .OrderBy(candidate => candidate.UnitPrice)
            .FirstOrDefault();
        if (current is not null)
        {
            return current;
        }

        var fallback = marketPriceService.GetBestUnitValue(item.TemplateId);
        if (fallback.UnitValue > 0L)
        {
            return new AcquisitionEstimate(fallback.Source, fallback.UnitValue);
        }

        return referencePrice is > 0
            ? new AcquisitionEstimate("Handbook fallback", referencePrice.Value)
            : new AcquisitionEstimate(null, null);
    }

    private TraderSaleEstimate GetBestTraderSale(
        HermesCatalogItem item,
        MongoId sessionId,
        IDictionary<string, TraderSaleEstimate> cache)
    {
        if (cache.TryGetValue(item.ItemKey, out var cached))
        {
            return cached;
        }

        TraderSaleEstimate estimate;
        try
        {
            var bestSell = traderService.GetBestBaseItemSellOffer(item.TemplateId, sessionId);
            estimate = bestSell is not null && bestSell.RoubleEquivalent > 0
                ? new TraderSaleEstimate(bestSell.TraderName, bestSell.RoubleEquivalent)
                : new TraderSaleEstimate(null, 0L);
        }
        catch
        {
            estimate = new TraderSaleEstimate(null, 0L);
        }

        cache[item.ItemKey] = estimate;
        return estimate;
    }

    private FleaSaleEstimate GetFleaSale(
        HermesCatalogItem item,
        MongoId sessionId,
        FleaSaleCache cache)
    {
        if (cache.ByItem.TryGetValue(item.ItemKey, out var cached))
        {
            return cached;
        }

        if (cache.FleaUnlocked == false)
        {
            var locked = new FleaSaleEstimate(false, false, 0L);
            cache.ByItem[item.ItemKey] = locked;
            return locked;
        }

        FleaSaleEstimate estimate;
        try
        {
            var market = marketService.GetCraftEstimate(item.TemplateId, sessionId);
            cache.FleaUnlocked = market.FleaUnlocked;

            estimate = market.FleaUnlocked
                && market.CanSellOnFlea
                && market.UnitNetValue > 0
                    ? new FleaSaleEstimate(true, true, market.UnitNetValue)
                    : new FleaSaleEstimate(market.FleaUnlocked, false, 0L);
        }
        catch
        {
            estimate = new FleaSaleEstimate(cache.FleaUnlocked == true, false, 0L);
        }

        cache.ByItem[item.ItemKey] = estimate;
        return estimate;
    }

    private ItemValuation GetItemValuation(
        HermesCatalogItem item,
        MongoId sessionId,
        IDictionary<string, ItemValuation> cache,
        bool includeLivePricing = true)
    {
        if (cache.TryGetValue(item.ItemKey, out var cached))
        {
            return cached;
        }

        var handbook = catalogService.GetReferencePrice(item.TemplateId);

        // Craft summary requests can cover hundreds of recipes, so ingredient and general
        // economic values stay on the handbook path here. Best trader output sale values are
        // resolved separately once per unique output through traderSaleCache.
        if (!includeLivePricing)
        {
            var lightweight = new ItemValuation(
                handbook,
                [],
                handbook is > 0 ? "Handbook reference" : null,
                handbook,
                handbook is > 0 ? "handbook reference" : null,
                handbook);
            cache[item.ItemKey] = lightweight;
            return lightweight;
        }

        var acquisitionQuotes = new List<AcquisitionQuote>();
        var economicCandidates = new List<ValueEstimate>();
        string? fallbackMarketSource = null;
        long? fallbackMarketUnitValue = null;

        HermesTraderSummaryResponse? traderSummary = null;
        try
        {
            traderSummary = traderService.GetSummary(item.ItemKey, null, sessionId);
        }
        catch
        {
            // Flea and handbook values remain available.
        }

        if (traderSummary is not null)
        {
            foreach (var offer in traderSummary.PurchaseOffers.Where(offer => offer.IsAvailable))
            {
                var payment = offer.PaymentOptions
                    .Where(option => option.EstimatedRoubleValue > 0)
                    .OrderBy(option => option.EstimatedRoubleValue)
                    .FirstOrDefault();
                if (payment is null)
                {
                    continue;
                }

                var packSize = Math.Max(1, offer.PackSize);
                var unitPrice = Math.Max(
                    1L,
                    Convert.ToInt64(Math.Round(payment.EstimatedRoubleValue / (double)packSize)));

                double? availableQuantity = null;
                if (!offer.UnlimitedStock)
                {
                    availableQuantity = Math.Max(0, offer.StockRemaining ?? 0) * packSize;
                }

                if (offer.PurchaseLimitRemaining.HasValue)
                {
                    var limitedQuantity = Math.Max(0, offer.PurchaseLimitRemaining.Value) * packSize;
                    availableQuantity = availableQuantity.HasValue
                        ? Math.Min(availableQuantity.Value, limitedQuantity)
                        : limitedQuantity;
                }

                if (availableQuantity is <= 0d)
                {
                    continue;
                }

                var source = payment.IsCash
                    ? $"{offer.TraderName} cash"
                    : $"{offer.TraderName} barter — {payment.DisplayPrice}";
                acquisitionQuotes.Add(new AcquisitionQuote(source, unitPrice, availableQuantity));
            }

            var bestSell = traderSummary.BestSellOffer;
            if (bestSell is not null && bestSell.RoubleEquivalent > 0)
            {
                economicCandidates.Add(new ValueEstimate(
                    $"{bestSell.TraderName} sale",
                    bestSell.RoubleEquivalent));
            }
        }

        try
        {
            var market = marketService.GetSummary(item.ItemKey, sessionId);
            if (market.FleaUnlocked && market.MarketPriceFromActiveOffers)
            {
                foreach (var offer in market.LowestOffers.Where(offer => offer.UnitPrice > 0 && offer.Quantity > 0))
                {
                    acquisitionQuotes.Add(new AcquisitionQuote(
                        offer.PriceSource,
                        offer.UnitPrice,
                        offer.Quantity));
                }
            }

            var resolvedMarket = marketPriceService.GetBestUnitValue(item.TemplateId);
            if (resolvedMarket.UnitValue > 0L)
            {
                fallbackMarketSource = resolvedMarket.Source;
                fallbackMarketUnitValue = resolvedMarket.UnitValue;
            }

            if (market.FleaUnlocked
                && market.CanSellOnFlea
                && market.EstimatedNetSale is > 0)
            {
                economicCandidates.Add(new ValueEstimate(
                    "local flea net",
                    market.EstimatedNetSale.Value));
            }
            else if (resolvedMarket.UnitValue > 0L)
            {
                economicCandidates.Add(new ValueEstimate(
                    resolvedMarket.Source,
                    resolvedMarket.UnitValue));
            }
        }
        catch
        {
            var resolvedMarket = marketPriceService.GetBestUnitValue(item.TemplateId);
            if (resolvedMarket.UnitValue > 0L)
            {
                fallbackMarketSource = resolvedMarket.Source;
                fallbackMarketUnitValue = resolvedMarket.UnitValue;
                economicCandidates.Add(new ValueEstimate(
                    resolvedMarket.Source,
                    resolvedMarket.UnitValue));
            }
        }

        if (economicCandidates.Count == 0 && handbook is > 0)
        {
            fallbackMarketSource ??= "Handbook fallback";
            fallbackMarketUnitValue ??= handbook.Value;
            economicCandidates.Add(new ValueEstimate(
                "Handbook fallback",
                handbook.Value));
        }

        var economic = economicCandidates
            .Where(candidate => candidate.UnitPrice > 0)
            .OrderByDescending(candidate => candidate.UnitPrice)
            .FirstOrDefault();

        var value = new ItemValuation(
            handbook,
            acquisitionQuotes
                .OrderBy(quote => quote.UnitPrice)
                .ThenBy(quote => quote.Source, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            fallbackMarketSource,
            fallbackMarketUnitValue,
            economic?.Source,
            economic?.UnitPrice);

        cache[item.ItemKey] = value;
        return value;
    }

    private static long RoundCost(long? unitPrice, double quantity)
    {
        if (unitPrice is not > 0 || quantity <= 0d)
        {
            return 0L;
        }

        return Convert.ToInt64(Math.Round(unitPrice.Value * quantity));
    }
}
