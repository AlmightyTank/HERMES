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
    private HermesCraftsResponse BuildCraftsCore(MongoId sessionId)
    {
        EnsureStaticIndex();
        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesCraftsResponse(false, "HERMES could not read the active PMC hideout profile.", 0, []);
        }

        var valuationCache = new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase);
        var traderSaleCache = new Dictionary<string, TraderSaleEstimate>(StringComparer.OrdinalIgnoreCase);
        var fleaSaleCache = new FleaSaleCache();
        var crafts = new List<HermesCraftSummary>();

        // Evaluate recipes independently. Custom production tables occasionally contain
        // incomplete event recipes or references to stations removed by another server mod.
        // One malformed recipe must not abort every valid /hermes/crafts/summary result.
        foreach (var craft in _craftsByKey!.Values)
        {
            if (!IsCraftStationAvailable(craft, snapshot))
            {
                continue;
            }

            try
            {
                crafts.Add(BuildCraftEvaluation(
                    craft,
                    snapshot,
                    sessionId,
                    valuationCache,
                    includeLivePricing: false,
                    traderSaleCache: traderSaleCache,
                    fleaSaleCache: fleaSaleCache));
            }
            catch
            {
                // Hide only the malformed recipe and continue returning all valid crafts.
            }
        }

        var representedProductionKeys = crafts
            .Where(craft => !string.IsNullOrWhiteSpace(craft.ProductionKey))
            .Select(craft => craft.ProductionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var production in snapshot.Productions)
        {
            if (representedProductionKeys.Contains(production.ProductionKey)
                || (!production.IsComplete && !production.InProgress))
            {
                continue;
            }

            crafts.Add(BuildProductionOnlyCraftSummary(production, snapshot));
        }

        crafts = crafts
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.IsAvailable)
            .ThenByDescending(craft => craft.EstimatedBestSaleProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HermesCraftsResponse(true, null, crafts.Count, crafts);
    }

    private HermesCraftSummary BuildCraftEvaluation(
        CraftDefinition craft,
        ProfileSnapshot snapshot,
        MongoId sessionId,
        IDictionary<string, ItemValuation> valuationCache,
        IReadOnlyList<HermesCraftIngredient>? prebuiltIngredients = null,
        bool includeLivePricing = true,
        IDictionary<string, TraderSaleEstimate>? traderSaleCache = null,
        FleaSaleCache? fleaSaleCache = null)
    {
        var stationLevel = snapshot.Areas.GetValueOrDefault(craft.AreaType)?.Level ?? 0;
        var stationReady = stationLevel >= craft.RequiredStationLevel;
        var ingredients = prebuiltIngredients
                          ?? BuildCraftIngredients(craft, snapshot, sessionId, valuationCache, includeLivePricing);
        var ingredientsReady = ingredients.All(ingredient => ingredient.IsMet);
        var questReady = string.IsNullOrWhiteSpace(craft.QuestId)
                         || snapshot.CompletedQuests.Contains(craft.QuestId);
        var unlocked = !craft.Locked
                       || snapshot.UnlockedRecipes.Contains(craft.Id)
                       || (!string.IsNullOrWhiteSpace(craft.QuestId)
                           && snapshot.CompletedQuests.Contains(craft.QuestId));
        var activeProduction = snapshot.Productions.FirstOrDefault(production =>
            string.Equals(production.RecipeId, craft.Id, StringComparison.OrdinalIgnoreCase));
        var stationBusy = snapshot.Productions.Any(production =>
            production.InProgress
            && _craftsById!.TryGetValue(production.RecipeId, out var activeCraft)
            && activeCraft.AreaType == craft.AreaType
            && !string.Equals(activeCraft.Id, craft.Id, StringComparison.OrdinalIgnoreCase));

        // IsAvailable remains the full recipe-readiness state used by status badges and
        // Ready Now. StationLevelMet is sent separately because the native AVAILABLE
        // checkbox is profile-progression filtering: it follows the player's current
        // station level even when ingredients are still missing.
        var isAvailable = stationReady && questReady && unlocked && ingredientsReady;
        var canStart = isAvailable && !stationBusy && activeProduction is null;
        string status;
        if (activeProduction?.IsComplete == true)
        {
            status = "Ready to collect";
        }
        else if (activeProduction is not null)
        {
            status = "In production";
        }
        else if (!stationReady)
        {
            status = $"Requires {craft.StationName} Level {craft.RequiredStationLevel}";
        }
        else if (!questReady)
        {
            var questName = craft.QuestId is not null && MongoId.IsValidMongoId(craft.QuestId)
                ? catalogService.GetQuestName(new MongoId(craft.QuestId))
                : null;
            status = string.IsNullOrWhiteSpace(questName)
                ? "Locked by quest"
                : $"Locked by quest: \"{questName}\"";
        }
        else if (!unlocked)
        {
            status = "Locked by progression";
        }
        else if (stationBusy)
        {
            status = "Station already producing";
        }
        else if (!ingredientsReady)
        {
            status = "Missing ingredients";
        }
        else
        {
            status = "Ready to start";
        }

        var acquisitionPlanComplete = ingredients.All(ingredient =>
            ingredient.Missing <= 0d || ingredient.AcquisitionAvailable);
        var additionalCashCost = ingredients.Sum(ingredient => ingredient.EstimatedPurchaseCost);
        var ownedIngredientValue = ingredients.Sum(ingredient => ingredient.EstimatedOwnedEconomicValue);

        var unavailableEconomicEstimate = ingredients.Sum(ingredient =>
        {
            if (ingredient.Missing <= 0d
                || ingredient.AcquisitionAvailable
                || ingredient.IsReusableTool
                || ingredient.EstimatedPurchaseCost > 0L)
            {
                return 0L;
            }

            var fallbackUnit = ingredient.OwnedEconomicUnitValue
                               ?? ingredient.UnitHandbookValue
                               ?? 0L;
            return RoundCost(fallbackUnit, ingredient.Missing);
        });

        var economicInputValue = ownedIngredientValue + additionalCashCost + unavailableEconomicEstimate;

        var outputValue = 0L;
        string? bestTraderName = null;
        var traderSaleValue = 0L;
        var fleaUnlocked = false;
        var canSellOnFlea = false;
        var fleaNetSaleValue = 0L;
        if (MongoId.IsValidMongoId(craft.OutputTemplateId))
        {
            var outputItem = catalogService.ResolveTemplate(new MongoId(craft.OutputTemplateId));
            if (outputItem is not null)
            {
                var valuation = GetItemValuation(outputItem, sessionId, valuationCache, includeLivePricing);
                var unitOutputValue = valuation.EconomicUnitValue
                                      ?? valuation.HandbookUnitValue
                                      ?? 0L;
                outputValue = unitOutputValue * Math.Max(1, craft.OutputQuantity);

                traderSaleCache ??= new Dictionary<string, TraderSaleEstimate>(StringComparer.OrdinalIgnoreCase);
                var traderSale = GetBestTraderSale(outputItem, sessionId, traderSaleCache);
                bestTraderName = traderSale.TraderName;
                traderSaleValue = traderSale.UnitValue * Math.Max(1, craft.OutputQuantity);

                fleaSaleCache ??= new FleaSaleCache();
                var fleaSale = GetFleaSale(outputItem, sessionId, fleaSaleCache);
                fleaUnlocked = fleaSale.FleaUnlocked;
                canSellOnFlea = fleaSale.CanSellOnFlea;
                fleaNetSaleValue = fleaSale.UnitNetValue * Math.Max(1, craft.OutputQuantity);
            }
        }

        var cashProfit = outputValue - additionalCashCost;
        var economicProfit = outputValue - economicInputValue;
        var economicProfitPerHour = craft.DurationSeconds > 0
            ? Convert.ToInt64(Math.Round(economicProfit / (craft.DurationSeconds / 3600d)))
            : economicProfit;
        var traderProfit = traderSaleValue - economicInputValue;
        var traderProfitPerHour = craft.DurationSeconds > 0
            ? Convert.ToInt64(Math.Round(traderProfit / (craft.DurationSeconds / 3600d)))
            : traderProfit;
        var fleaProfit = fleaNetSaleValue - economicInputValue;
        var fleaProfitPerHour = craft.DurationSeconds > 0
            ? Convert.ToInt64(Math.Round(fleaProfit / (craft.DurationSeconds / 3600d)))
            : fleaProfit;

        var useFlea = fleaUnlocked
                      && canSellOnFlea
                      && fleaNetSaleValue > traderSaleValue;
        var bestSaleSource = useFlea
            ? "Flea Market"
            : !string.IsNullOrWhiteSpace(bestTraderName)
                ? bestTraderName!
                : "No available buyer";
        var bestSaleValue = useFlea ? fleaNetSaleValue : traderSaleValue;
        var bestSaleProfit = bestSaleValue - economicInputValue;
        var bestSaleProfitPerHour = craft.DurationSeconds > 0
            ? Convert.ToInt64(Math.Round(bestSaleProfit / (craft.DurationSeconds / 3600d)))
            : bestSaleProfit;

        return new HermesCraftSummary(
            CraftKey: craft.Key,
            ProductionKey: activeProduction?.ProductionKey ?? string.Empty,
            StationName: craft.StationName,
            CurrentStationLevel: stationLevel,
            RequiredStationLevel: craft.RequiredStationLevel,
            StationLevelMet: stationReady,
            OutputName: craft.OutputName,
            OutputTemplateId: craft.OutputTemplateId,
            OutputQuantity: craft.OutputQuantity,
            DurationSeconds: craft.DurationSeconds,
            IsAvailable: isAvailable,
            CanStartNow: canStart,
            Status: status,
            AcquisitionPlanComplete: acquisitionPlanComplete,
            EstimatedAdditionalCashCost: additionalCashCost,
            EstimatedOwnedIngredientValue: ownedIngredientValue,
            EstimatedEconomicInputValue: economicInputValue,
            EstimatedOutputValue: outputValue,
            EstimatedCashProfit: cashProfit,
            EstimatedEconomicProfit: economicProfit,
            EstimatedEconomicProfitPerHour: economicProfitPerHour,
            BestTraderName: bestTraderName,
            EstimatedTraderSaleValue: traderSaleValue,
            EstimatedTraderProfit: traderProfit,
            EstimatedTraderProfitPerHour: traderProfitPerHour,
            FleaUnlocked: fleaUnlocked,
            CanSellOnFlea: canSellOnFlea,
            EstimatedFleaNetSaleValue: fleaNetSaleValue,
            EstimatedFleaProfit: fleaProfit,
            EstimatedFleaProfitPerHour: fleaProfitPerHour,
            BestSaleSource: bestSaleSource,
            EstimatedBestSaleValue: bestSaleValue,
            EstimatedBestSaleProfit: bestSaleProfit,
            EstimatedBestSaleProfitPerHour: bestSaleProfitPerHour,
            IsActive: activeProduction is not null,
            IsComplete: activeProduction?.IsComplete == true);
    }

    private HermesCraftSummary BuildProductionOnlyCraftSummary(
        ProfileProductionState production,
        ProfileSnapshot snapshot)
    {
        _craftsById!.TryGetValue(production.RecipeId, out var craft);
        var stationLevel = craft is null
            ? 0
            : snapshot.Areas.GetValueOrDefault(craft.AreaType)?.Level ?? 0;
        var stationName = craft?.StationName
                          ?? production.StationName
                          ?? "Hideout station";
        var outputTemplateId = craft?.OutputTemplateId
                               ?? production.OutputTemplateId;
        var outputName = craft?.OutputName
                         ?? production.OutputName
                         ?? ResolveProductionOutputName(outputTemplateId)
                         ?? "Hideout production";
        var outputQuantity = craft?.OutputQuantity
                             ?? Math.Max(1, production.OutputQuantity);
        var remaining = production.IsComplete
            ? 0
            : Math.Max(0, Convert.ToInt32(Math.Ceiling(production.ProductionTime - production.Progress)));

        return new HermesCraftSummary(
            CraftKey: craft?.Key ?? $"production:{production.ProductionKey}",
            ProductionKey: production.ProductionKey,
            StationName: stationName,
            CurrentStationLevel: stationLevel,
            RequiredStationLevel: craft?.RequiredStationLevel ?? 0,
            StationLevelMet: craft is null || stationLevel >= craft.RequiredStationLevel,
            OutputName: outputName,
            OutputTemplateId: outputTemplateId,
            OutputQuantity: outputQuantity,
            DurationSeconds: craft?.DurationSeconds ?? Convert.ToInt32(Math.Max(0d, production.ProductionTime)),
            IsAvailable: false,
            CanStartNow: false,
            Status: production.IsComplete
                ? "Ready to collect"
                : remaining > 0
                    ? $"In production - {remaining:N0}s remaining"
                    : "In production",
            AcquisitionPlanComplete: false,
            EstimatedAdditionalCashCost: 0,
            EstimatedOwnedIngredientValue: 0,
            EstimatedEconomicInputValue: 0,
            EstimatedOutputValue: 0,
            EstimatedCashProfit: 0,
            EstimatedEconomicProfit: 0,
            EstimatedEconomicProfitPerHour: 0,
            BestTraderName: null,
            EstimatedTraderSaleValue: 0,
            EstimatedTraderProfit: 0,
            EstimatedTraderProfitPerHour: 0,
            FleaUnlocked: false,
            CanSellOnFlea: false,
            EstimatedFleaNetSaleValue: 0,
            EstimatedFleaProfit: 0,
            EstimatedFleaProfitPerHour: 0,
            BestSaleSource: "Production",
            EstimatedBestSaleValue: 0,
            EstimatedBestSaleProfit: 0,
            EstimatedBestSaleProfitPerHour: 0,
            IsActive: true,
            IsComplete: production.IsComplete);
    }

    private string? ResolveProductionOutputName(string? outputTemplateId)
        => !string.IsNullOrWhiteSpace(outputTemplateId) && MongoId.IsValidMongoId(outputTemplateId)
            ? catalogService.ResolveTemplate(new MongoId(outputTemplateId))?.Name
            : null;

    private IReadOnlyList<HermesCraftIngredient> BuildCraftIngredients(
        CraftDefinition craft,
        ProfileSnapshot snapshot,
        MongoId sessionId,
        IDictionary<string, ItemValuation> valuationCache,
        bool includeLivePricing = true)
    {
        var output = new List<HermesCraftIngredient>();

        foreach (var requirement in craft.Requirements.Where(IsItemRequirement))
        {
            if (string.IsNullOrWhiteSpace(requirement.TemplateId)
                || !MongoId.IsValidMongoId(requirement.TemplateId))
            {
                continue;
            }

            var templateId = new MongoId(requirement.TemplateId);
            var catalogItem = catalogService.ResolveTemplate(templateId);
            if (catalogItem is null)
            {
                continue;
            }

            var owned = requirement.FoundInRaidRequired
                ? snapshot.FoundInRaidInventory.GetValueOrDefault(requirement.TemplateId)
                : snapshot.Inventory.GetValueOrDefault(requirement.TemplateId);
            var required = Math.Max(0d, requirement.Count);
            var ownedUsed = Math.Min(required, Math.Max(0d, owned));
            var missing = Math.Max(0d, required - ownedUsed);
            var reusableTool = requirement.Type.Contains("Tool", StringComparison.OrdinalIgnoreCase);
            var valuation = GetItemValuation(catalogItem, sessionId, valuationCache, includeLivePricing);

            var ownedEconomicUnit = valuation.EconomicUnitValue
                                    ?? valuation.HandbookUnitValue;
            var ownedEconomicValue = reusableTool
                ? 0L
                : RoundCost(ownedEconomicUnit, ownedUsed);

            var acquisitionPlan = new List<HermesCraftAcquisitionLine>();
            var unavailableQuantity = 0d;
            string? note;

            if (missing <= 0d)
            {
                note = reusableTool
                    ? "Reusable tool is already owned and is not charged as a consumed input."
                    : "Owned quantity adds no new cash cost; its sell opportunity value is included in economic profit.";
            }
            else if (requirement.FoundInRaidRequired)
            {
                unavailableQuantity = missing;
                note = "This missing quantity must be found in raid and cannot be satisfied by a trader or flea purchase.";
            }
            else
            {
                var remaining = missing;
                foreach (var quote in valuation.AcquisitionQuotes
                             .Where(quote => quote.UnitPrice > 0)
                             .OrderBy(quote => quote.UnitPrice))
                {
                    if (remaining <= 0d)
                    {
                        break;
                    }

                    var available = quote.AvailableQuantity.HasValue
                        ? Math.Max(0d, quote.AvailableQuantity.Value)
                        : remaining;
                    var quantity = Math.Min(remaining, available);
                    if (quantity <= 0d)
                    {
                        continue;
                    }

                    acquisitionPlan.Add(new HermesCraftAcquisitionLine(
                        quote.Source,
                        quantity,
                        quote.UnitPrice,
                        RoundCost(quote.UnitPrice, quantity),
                        false));
                    remaining -= quantity;
                }

                if (remaining > 0d)
                {
                    unavailableQuantity = remaining;
                    var fallbackUnit = valuation.FallbackMarketUnitValue
                                       ?? valuation.HandbookUnitValue;
                    if (fallbackUnit is > 0)
                    {
                        var fallbackSource = string.IsNullOrWhiteSpace(valuation.FallbackMarketSource)
                            ? "Handbook fallback"
                            : valuation.FallbackMarketSource;
                        acquisitionPlan.Add(new HermesCraftAcquisitionLine(
                            $"{fallbackSource} — no current purchase source",
                            remaining,
                            fallbackUnit.Value,
                            RoundCost(fallbackUnit, remaining),
                            true));
                    }
                }

                note = unavailableQuantity <= 0d
                    ? "Missing quantity is allocated across the cheapest currently available trader, barter, and active local flea sources."
                    : "Part of the missing quantity has no current purchase source; HERMES shows the shared flea-price fallback chain as an estimate only.";
            }

            var purchaseCost = acquisitionPlan.Sum(line => line.TotalCost);
            var acquisitionAvailable = missing <= 0d || unavailableQuantity <= 0d;

            output.Add(new HermesCraftIngredient(
                catalogItem.Name,
                requirement.TemplateId,
                FormatDisplayName(requirement.Type),
                required,
                owned,
                ownedUsed,
                missing,
                missing <= 0d,
                requirement.FoundInRaidRequired,
                reusableTool,
                valuation.HandbookUnitValue,
                ownedEconomicUnit,
                ownedEconomicValue,
                acquisitionPlan,
                unavailableQuantity,
                purchaseCost,
                acquisitionAvailable,
                note));
        }

        return output;
    }

    private bool IsCraftStationAvailable(CraftDefinition craft, ProfileSnapshot snapshot)
    {
        // Production tables can be extended by server mods and seasonal data. Treat an
        // incomplete recipe/station/profile entry as unavailable instead of allowing one
        // malformed record to abort the entire crafts summary response.
        if (craft is null
            || snapshot is null
            || _areasByType is null
            || snapshot.Areas is null)
        {
            return false;
        }

        try
        {
            if (!_areasByType.TryGetValue(craft.AreaType, out var area)
                || area is null
                || !IsAreaAvailable(area, snapshot)
                || !snapshot.Areas.TryGetValue(craft.AreaType, out var profileArea)
                || profileArea is null)
            {
                return false;
            }

            // SPT keeps level-zero placeholders for hideout areas that the active PMC has not
            // constructed yet. Presence in Hideout.Areas therefore does not mean the station is
            // installed. Only expose recipes after this exact profile has built level 1 or higher.
            // Higher-level recipes can still be shown for planning once the station exists; their
            // required level remains evaluated separately by BuildCraftEvaluation().
            return profileArea.Level > 0;
        }
        catch
        {
            // Hide malformed or partially initialized mod/event recipes. The detail endpoint
            // reports the station as unavailable instead of returning HTTP 500.
            return false;
        }
    }

    private static HermesCraftUse ToPlayerAwareCraftUse(
        CraftDefinition craft,
        double itemCount,
        double owned,
        double missing,
        int currentStationLevel,
        bool unlocked,
        HermesCraftSummary evaluation,
        ProfileProductionState? production)
    {
        return new HermesCraftUse(
            CraftKey: craft.Key,
            StationName: craft.StationName,
            CurrentStationLevel: currentStationLevel,
            RequiredStationLevel: craft.RequiredStationLevel,
            OutputName: craft.OutputName,
            OutputQuantity: craft.OutputQuantity,
            DurationSeconds: craft.DurationSeconds,
            ItemCount: Math.Max(0d, itemCount),
            Owned: owned,
            Missing: missing,
            IsUnlocked: unlocked,
            CanStartNow: evaluation.CanStartNow,
            IsActive: production is not null && !production.IsComplete,
            IsComplete: production?.IsComplete == true,
            Status: evaluation.Status);
    }
}
