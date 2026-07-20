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

[Injectable(InjectionType.Singleton)]
public sealed partial class HermesHideoutService(
    DatabaseService databaseService,
    HermesPreparedProfileSnapshotService preparedProfiles,
    HermesProfileScopeService profileScopeService,
    HermesStaticDataSnapshotService staticData,
    HermesCatalogService catalogService,
    HermesTraderService traderService,
    HermesMarketService marketService,
    HermesMarketPriceService marketPriceService,
    HermesQuestKeyKnowledgeService questKeyKnowledgeService,
    SeasonalEventService seasonalEventService)
{
    private readonly object _sync = new();
    private readonly object _hideoutSummaryCacheSync = new();
    private readonly object _craftsSummaryCacheSync = new();
    private readonly Dictionary<string, MaterializedSummaryEntry<HermesHideoutSummaryResponse>>
        _hideoutSummaryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MaterializedSummaryEntry<HermesCraftsResponse>>
        _craftsSummaryCache = new(StringComparer.Ordinal);
    private long _hideoutSummaryGeneration;
    private long _craftsSummaryGeneration;
    private long _hideoutContentRevision;
    private long _craftsContentRevision;
    private Dictionary<string, AreaDefinition>? _areasByKey;
    private Dictionary<int, AreaDefinition>? _areasByType;
    private Dictionary<string, CraftDefinition>? _craftsByKey;
    private Dictionary<string, CraftDefinition>? _craftsById;
    private Dictionary<string, string>? _traderNames;
    private Dictionary<string, string>? _questIdsByNormalizedName;
    private Dictionary<string, List<QuestItemUseDefinition>>? _questUsesByTemplate;
    private double _generatorFuelFlowRate;

    public HermesHideoutSummaryResponse GetSummary(MongoId sessionId)
    {
        var cacheKey = BuildMaterializedSummaryKey(sessionId, "hideout");
        lock (_hideoutSummaryCacheSync)
        {
            if (cacheKey is not null
                && _hideoutSummaryCache.TryGetValue(cacheKey, out var cached))
            {
                return cached.Response;
            }

            var response = BuildSummaryCore(sessionId);
            var confirmedKey = BuildMaterializedSummaryKey(sessionId, "hideout");
            if (response.Found
                && cacheKey is not null
                && string.Equals(cacheKey, confirmedKey, StringComparison.Ordinal))
            {
                response = response with
                {
                    ContentRevision = Interlocked.Increment(ref _hideoutContentRevision)
                };
                _hideoutSummaryCache[cacheKey] =
                    new MaterializedSummaryEntry<HermesHideoutSummaryResponse>(cacheKey, response);
            }

            return response;
        }
    }

    public HermesHideoutAreaDetailResponse GetAreaDetail(string? areaKey, MongoId sessionId)
    {
        EnsureStaticIndex();
        if (string.IsNullOrWhiteSpace(areaKey)
            || !_areasByKey!.TryGetValue(areaKey.Trim(), out var area))
        {
            return new HermesHideoutAreaDetailResponse(
                false,
                "The selected hideout area is no longer available. Refresh HERMES.",
                null,
                0,
                [],
                0);
        }

        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesHideoutAreaDetailResponse(
                false,
                "HERMES could not read the active PMC hideout profile.",
                null,
                0,
                [],
                0);
        }

        if (!IsAreaAvailable(area, snapshot))
        {
            return new HermesHideoutAreaDetailResponse(
                false,
                "The selected hideout area is not currently available.",
                null,
                0,
                [],
                0);
        }

        var evaluation = BuildAreaEvaluation(area, snapshot, true, sessionId);
        return new HermesHideoutAreaDetailResponse(
            true,
            null,
            evaluation.Summary,
            evaluation.ConstructionSeconds,
            evaluation.Requirements,
            evaluation.Requirements.Sum(requirement => requirement.EstimatedMissingCost ?? 0L));
    }

    public HermesCraftsResponse GetCrafts(MongoId sessionId)
    {
        var cacheKey = BuildMaterializedSummaryKey(sessionId, "crafts");
        lock (_craftsSummaryCacheSync)
        {
            if (cacheKey is not null
                && _craftsSummaryCache.TryGetValue(cacheKey, out var cached))
            {
                return cached.Response;
            }

            var response = BuildCraftsCore(sessionId);
            var confirmedKey = BuildMaterializedSummaryKey(sessionId, "crafts");
            if (response.Found
                && cacheKey is not null
                && string.Equals(cacheKey, confirmedKey, StringComparison.Ordinal))
            {
                response = response with
                {
                    ContentRevision = Interlocked.Increment(ref _craftsContentRevision)
                };
                _craftsSummaryCache[cacheKey] =
                    new MaterializedSummaryEntry<HermesCraftsResponse>(cacheKey, response);
            }

            return response;
        }
    }

    public HermesCraftDetailResponse GetCraftDetail(string? craftKey, MongoId sessionId)
    {
        EnsureStaticIndex();
        if (string.IsNullOrWhiteSpace(craftKey)
            || !_craftsByKey!.TryGetValue(craftKey.Trim(), out var craft))
        {
            return new HermesCraftDetailResponse(
                false,
                "The selected craft is no longer available. Refresh HERMES.",
                null,
                [],
                null,
                false,
                "Inventory-aware values");
        }

        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesCraftDetailResponse(
                false,
                "HERMES could not read the active PMC hideout profile.",
                null,
                [],
                null,
                false,
                "Inventory-aware values");
        }

        if (!IsCraftStationAvailable(craft, snapshot))
        {
            return new HermesCraftDetailResponse(
                false,
                "The selected craft's hideout station is not currently available.",
                null,
                [],
                null,
                false,
                "Inventory-aware values");
        }

        var valuationCache = new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase);
        var traderSaleCache = new Dictionary<string, TraderSaleEstimate>(StringComparer.OrdinalIgnoreCase);
        var fleaSaleCache = new FleaSaleCache();
        var ingredients = BuildCraftIngredients(craft, snapshot, sessionId, valuationCache);
        var summary = BuildCraftEvaluation(
            craft,
            snapshot,
            sessionId,
            valuationCache,
            prebuiltIngredients: ingredients,
            traderSaleCache: traderSaleCache,
            fleaSaleCache: fleaSaleCache);
        var requiredQuestName = craft.QuestId is not null && MongoId.IsValidMongoId(craft.QuestId)
            ? catalogService.GetQuestName(new MongoId(craft.QuestId))
            : null;
        var questComplete = string.IsNullOrWhiteSpace(craft.QuestId)
                            || snapshot.CompletedQuests.Contains(craft.QuestId);

        return new HermesCraftDetailResponse(
            true,
            null,
            summary,
            ingredients,
            requiredQuestName,
            questComplete,
            "Owned ingredients have no additional cash cost but retain opportunity value. Missing ingredients use the cheapest available trader cash offer, trader barter estimate, or local flea offer; handbook value is used only as a fallback.");
    }

    public HermesItemHideoutUsageResponse GetItemUsage(string? itemKey, MongoId sessionId)
    {
        EnsureStaticIndex();
        var item = catalogService.ResolveItem(itemKey);
        if (item is null)
        {
            return new HermesItemHideoutUsageResponse(
                false,
                "The selected HERMES item is no longer available. Search for it again.",
                itemKey ?? string.Empty,
                string.Empty,
                0d,
                0d,
                [],
                [],
                [],
                [],
                []);
        }

        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesItemHideoutUsageResponse(
                false,
                "HERMES could not read the active PMC quest, hideout, and inventory state.",
                item.ItemKey,
                item.Name,
                0d,
                0d,
                [],
                [],
                [],
                [],
                []);
        }

        var templateText = item.TemplateId.ToString();
        var ownedTotal = snapshot.Inventory.GetValueOrDefault(templateText);
        var ownedFir = snapshot.FoundInRaidInventory.GetValueOrDefault(templateText);
        var questUses = BuildQuestItemUses(templateText, snapshot);
        var questKeyUses = BuildQuestKeyUses(templateText, item.Name, snapshot);
        var upgradeUses = BuildPlayerAwareUpgradeUses(item, templateText, snapshot, sessionId);
        var producedBy = new List<HermesCraftUse>();
        var usedBy = new List<HermesCraftUse>();
        var craftValuationCache = new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase);
        var craftTraderSaleCache = new Dictionary<string, TraderSaleEstimate>(StringComparer.OrdinalIgnoreCase);
        var craftFleaSaleCache = new FleaSaleCache();

        foreach (var craft in _craftsByKey!.Values)
        {
            if (!IsCraftStationAvailable(craft, snapshot))
            {
                continue;
            }

            var producesItem = string.Equals(craft.OutputTemplateId, templateText, StringComparison.OrdinalIgnoreCase);
            var matchingRequirements = craft.Requirements
                .Where(requirement => string.Equals(requirement.TemplateId, templateText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!producesItem && matchingRequirements.Count == 0)
            {
                continue;
            }

            var evaluation = BuildCraftEvaluation(
                craft,
                snapshot,
                sessionId,
                craftValuationCache,
                includeLivePricing: false,
                traderSaleCache: craftTraderSaleCache,
                fleaSaleCache: craftFleaSaleCache);
            var currentStationLevel = snapshot.Areas.GetValueOrDefault(craft.AreaType)?.Level ?? 0;
            var unlocked = !craft.Locked
                           || snapshot.UnlockedRecipes.Contains(craft.Id)
                           || string.IsNullOrWhiteSpace(craft.QuestId)
                           || snapshot.CompletedQuests.Contains(craft.QuestId);
            var production = snapshot.Productions.FirstOrDefault(value =>
                string.Equals(value.RecipeId, craft.Id, StringComparison.OrdinalIgnoreCase));

            if (producesItem)
            {
                producedBy.Add(ToPlayerAwareCraftUse(
                    craft,
                    craft.OutputQuantity,
                    ownedTotal,
                    0d,
                    currentStationLevel,
                    unlocked,
                    evaluation,
                    production));
            }

            foreach (var requirement in matchingRequirements)
            {
                var available = requirement.FoundInRaidRequired ? ownedFir : ownedTotal;
                var missing = Math.Max(0d, requirement.Count - available);
                usedBy.Add(ToPlayerAwareCraftUse(
                    craft,
                    requirement.Count,
                    available,
                    missing,
                    currentStationLevel,
                    unlocked,
                    evaluation,
                    production));
            }
        }

        return new HermesItemHideoutUsageResponse(
            true,
            null,
            item.ItemKey,
            item.Name,
            ownedTotal,
            ownedFir,
            questUses,
            questKeyUses,
            upgradeUses,
            producedBy
                .OrderByDescending(use => use.CanStartNow)
                .ThenBy(use => use.StationName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(use => use.OutputName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            usedBy
                .OrderByDescending(use => use.CanStartNow)
                .ThenBy(use => use.StationName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(use => use.OutputName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }


    /// <summary>
    /// Drops only the materialized Hideout/Crafts response objects. Static recipe indexes remain
    /// intact unless the change tracker separately detects a database-table change.
    /// </summary>
    public void InvalidateMaterializedSummaries(
        bool hideout = true,
        bool crafts = true,
        string? reason = null)
    {
        if (hideout)
        {
            Interlocked.Increment(ref _hideoutSummaryGeneration);
            lock (_hideoutSummaryCacheSync)
            {
                _hideoutSummaryCache.Clear();
            }
        }

        if (crafts)
        {
            Interlocked.Increment(ref _craftsSummaryGeneration);
            lock (_craftsSummaryCacheSync)
            {
                _craftsSummaryCache.Clear();
            }
        }
    }

    private string? BuildMaterializedSummaryKey(MongoId sessionId, string domain)
    {
        // Materialized workspace responses are invalidated by HermesChangeTrackingService when
        // relevant profile, hideout, quest, trader, or market source revisions change. Opening a
        // workspace therefore needs only a profile-scope key and generation number; it must not
        // serialize and hash large profile sections just to prove that a prepared response exists.
        var scope = profileScopeService.ResolveIdentity(sessionId);
        if (scope is null)
        {
            return null;
        }

        var generation = domain.Equals("crafts", StringComparison.OrdinalIgnoreCase)
            ? Interlocked.Read(ref _craftsSummaryGeneration)
            : Interlocked.Read(ref _hideoutSummaryGeneration);
        return $"{scope.ScopeKey}|{domain}|{generation}";
    }
}
