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

internal sealed record HermesTemplateReservation(
    double ActiveQuestQuantity,
    double ActiveQuestFoundInRaidQuantity,
    double FutureQuestQuantity,
    double FutureQuestFoundInRaidQuantity,
    double NextHideoutQuantity,
    double NextHideoutFoundInRaidQuantity,
    double FutureHideoutQuantity,
    double FutureHideoutFoundInRaidQuantity,
    IReadOnlyList<string> Reasons);

internal sealed record HermesStashReservationSnapshot(
    IReadOnlyDictionary<string, HermesTemplateReservation> ByTemplate);

[Injectable(InjectionType.Singleton)]
public sealed class HermesHideoutService(
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    JsonUtil jsonUtil,
    HermesCatalogService catalogService,
    HermesTraderService traderService,
    HermesMarketService marketService)
{
    private readonly object _sync = new();
    private Dictionary<string, AreaDefinition>? _areasByKey;
    private Dictionary<int, AreaDefinition>? _areasByType;
    private Dictionary<string, CraftDefinition>? _craftsByKey;
    private Dictionary<string, CraftDefinition>? _craftsById;
    private Dictionary<string, string>? _traderNames;
    private Dictionary<string, List<QuestItemUseDefinition>>? _questUsesByTemplate;
    private double _generatorFuelFlowRate;

    public HermesHideoutSummaryResponse GetSummary(MongoId sessionId)
    {
        EnsureStaticIndex();
        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesHideoutSummaryResponse(
                false,
                "HERMES could not read the active PMC hideout profile.",
                0,
                0,
                0,
                [],
                [],
                EmptyResources());
        }

        var areaSummaries = _areasByType!
            .Values
            .Where(area => area.Enabled)
            .Select(area => BuildAreaEvaluation(area, snapshot, false, sessionId).Summary)
            .OrderBy(area => AreaStatusRank(area.Status))
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var productions = BuildActiveProductions(snapshot);
        var resources = BuildResourceSummary(snapshot, productions);

        return new HermesHideoutSummaryResponse(
            true,
            null,
            areaSummaries.Count(area => area.Status.Equals("Ready to upgrade", StringComparison.OrdinalIgnoreCase)),
            areaSummaries.Count(area => area.Status.Equals("Missing materials", StringComparison.OrdinalIgnoreCase)),
            areaSummaries.Count(area => area.Status.Equals("Blocked by progression", StringComparison.OrdinalIgnoreCase)),
            areaSummaries,
            productions,
            resources);
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
        EnsureStaticIndex();
        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesCraftsResponse(false, "HERMES could not read the active PMC hideout profile.", 0, []);
        }

        var valuationCache = new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase);
        var crafts = _craftsByKey!
            .Values
            .Select(craft => BuildCraftEvaluation(craft, snapshot, sessionId, valuationCache, includeLivePricing: false))
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.EstimatedEconomicProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HermesCraftsResponse(true, null, crafts.Count, crafts);
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

        var valuationCache = new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase);
        var ingredients = BuildCraftIngredients(craft, snapshot, sessionId, valuationCache);
        var summary = BuildCraftEvaluation(craft, snapshot, sessionId, valuationCache, ingredients);
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
                []);
        }

        var templateText = item.TemplateId.ToString();
        var ownedTotal = snapshot.Inventory.GetValueOrDefault(templateText);
        var ownedFir = snapshot.FoundInRaidInventory.GetValueOrDefault(templateText);
        var questUses = BuildQuestItemUses(templateText, snapshot);
        var upgradeUses = BuildPlayerAwareUpgradeUses(item, templateText, snapshot, sessionId);
        var producedBy = new List<HermesCraftUse>();
        var usedBy = new List<HermesCraftUse>();

        foreach (var craft in _craftsByKey!.Values)
        {
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
                new Dictionary<string, ItemValuation>(StringComparer.OrdinalIgnoreCase),
                includeLivePricing: false);
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

    internal HermesStashReservationSnapshot? BuildStashReservations(
        MongoId sessionId,
        IReadOnlyDictionary<string, double> stashInventory,
        IReadOnlyDictionary<string, double> stashFoundInRaidInventory)
    {
        EnsureStaticIndex();
        var snapshot = BuildProfileSnapshot(sessionId);
        if (snapshot is null)
        {
            return null;
        }

        var builders = new Dictionary<string, StashReservationBuilder>(StringComparer.OrdinalIgnoreCase);
        var questDefinitionGroups = _questUsesByTemplate!
            .Values
            .SelectMany(value => value)
            .GroupBy(
                definition => $"{definition.QuestId}|{definition.ConditionId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .GroupBy(
                definition => $"{definition.QuestId}|{definition.FoundInRaidRequired}|{definition.RequiredCount:R}|"
                              + string.Join(",", definition.TargetTemplateIds
                                  .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var definitionGroup in questDefinitionGroups)
        {
            var definition = definitionGroup.First();
            snapshot.QuestStates.TryGetValue(definition.QuestId, out var questState);
            var statusCode = questState?.StatusCode ?? 0;
            var conditionCompleted = statusCode == 4
                                     || definitionGroup.All(value =>
                                         questState?.CompletedConditions.Contains(value.ConditionId) ?? false);
            if (conditionCompleted || statusCode is 5 or 6 or 7 or 8)
            {
                continue;
            }

            var isActive = statusCode is 2 or 3;
            var inventorySource = definition.FoundInRaidRequired
                ? stashFoundInRaidInventory
                : stashInventory;
            var ownedMatching = definition.TargetTemplateIds
                .Sum(target => inventorySource.GetValueOrDefault(target));
            var quantityToReserve = Math.Min(
                Math.Max(0d, definition.RequiredCount),
                Math.Max(0d, ownedMatching));
            if (quantityToReserve <= 0d)
            {
                continue;
            }

            var conditionTypes = string.Join(
                " / ",
                definitionGroup
                    .Select(value => FriendlyQuestConditionType(value.ConditionType))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            var reason = isActive
                ? $"Active quest: {definition.QuestName} — {conditionTypes} {definition.RequiredCount:N0}"
                : $"Future quest: {definition.QuestName} — {conditionTypes} {definition.RequiredCount:N0}";
            var remaining = quantityToReserve;
            foreach (var target in definition.TargetTemplateIds
                         .OrderByDescending(target => inventorySource.GetValueOrDefault(target))
                         .ThenBy(target => target, StringComparer.OrdinalIgnoreCase))
            {
                if (remaining <= 0d)
                {
                    break;
                }

                var allocated = Math.Min(
                    Math.Max(0d, inventorySource.GetValueOrDefault(target)),
                    remaining);
                if (allocated <= 0d)
                {
                    continue;
                }

                var builder = GetStashReservationBuilder(builders, target);
                if (isActive)
                {
                    builder.ActiveQuestQuantity += allocated;
                    if (definition.FoundInRaidRequired)
                    {
                        builder.ActiveQuestFoundInRaidQuantity += allocated;
                    }
                }
                else
                {
                    builder.FutureQuestQuantity += allocated;
                    if (definition.FoundInRaidRequired)
                    {
                        builder.FutureQuestFoundInRaidQuantity += allocated;
                    }
                }

                builder.Reasons.Add(reason + (definition.FoundInRaidRequired ? " (FIR required)" : string.Empty));
                remaining -= allocated;
            }
        }

        foreach (var area in _areasByType!.Values.Where(area => area.Enabled))
        {
            var profileArea = snapshot.Areas.GetValueOrDefault(area.Type);
            var currentLevel = profileArea?.Level ?? 0;
            foreach (var stage in area.Stages.Values.OrderBy(stage => stage.Level))
            {
                if (stage.Level <= currentLevel)
                {
                    continue;
                }

                var isNextStage = stage.Level == currentLevel + 1;
                if (isNextStage && profileArea?.Constructing == true)
                {
                    // Materials for a construction already in progress have been committed.
                    continue;
                }

                foreach (var requirement in stage.Requirements.Where(IsItemRequirement))
                {
                    if (string.IsNullOrWhiteSpace(requirement.TemplateId)
                        || requirement.Count <= 0d)
                    {
                        continue;
                    }

                    var inventorySource = requirement.FoundInRaidRequired
                        ? stashFoundInRaidInventory
                        : stashInventory;
                    var owned = Math.Max(0d, inventorySource.GetValueOrDefault(requirement.TemplateId));
                    var quantityToReserve = Math.Min(requirement.Count, owned);
                    if (quantityToReserve <= 0d)
                    {
                        continue;
                    }

                    var builder = GetStashReservationBuilder(builders, requirement.TemplateId);
                    if (isNextStage)
                    {
                        builder.NextHideoutQuantity += quantityToReserve;
                        if (requirement.FoundInRaidRequired)
                        {
                            builder.NextHideoutFoundInRaidQuantity += quantityToReserve;
                        }
                    }
                    else
                    {
                        builder.FutureHideoutQuantity += quantityToReserve;
                        if (requirement.FoundInRaidRequired)
                        {
                            builder.FutureHideoutFoundInRaidQuantity += quantityToReserve;
                        }
                    }

                    var hideoutReasonPrefix = isNextStage
                        ? "Next hideout upgrade"
                        : "Future hideout upgrade";
                    builder.Reasons.Add(
                        $"{hideoutReasonPrefix}: {area.Name} level {stage.Level} — {requirement.Count:N0} required"
                        + (requirement.FoundInRaidRequired ? " (FIR required)" : string.Empty));
                }
            }
        }

        var reservations = builders.ToDictionary(
            pair => pair.Key,
            pair => new HermesTemplateReservation(
                pair.Value.ActiveQuestQuantity,
                pair.Value.ActiveQuestFoundInRaidQuantity,
                pair.Value.FutureQuestQuantity,
                pair.Value.FutureQuestFoundInRaidQuantity,
                pair.Value.NextHideoutQuantity,
                pair.Value.NextHideoutFoundInRaidQuantity,
                pair.Value.FutureHideoutQuantity,
                pair.Value.FutureHideoutFoundInRaidQuantity,
                pair.Value.Reasons
                    .OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList()),
            StringComparer.OrdinalIgnoreCase);

        return new HermesStashReservationSnapshot(reservations);
    }

    private static StashReservationBuilder GetStashReservationBuilder(
        IDictionary<string, StashReservationBuilder> builders,
        string templateId)
    {
        if (!builders.TryGetValue(templateId, out var builder))
        {
            builder = new StashReservationBuilder();
            builders[templateId] = builder;
        }

        return builder;
    }

    private IReadOnlyList<HermesQuestItemUse> BuildQuestItemUses(
        string templateId,
        ProfileSnapshot snapshot)
    {
        if (_questUsesByTemplate is null
            || !_questUsesByTemplate.TryGetValue(templateId, out var definitions))
        {
            return [];
        }

        var output = new List<HermesQuestItemUse>();
        foreach (var definition in definitions)
        {
            snapshot.QuestStates.TryGetValue(definition.QuestId, out var questState);
            var statusCode = questState?.StatusCode ?? 0;
            var status = QuestStatusDisplay(statusCode);
            var questCompleted = statusCode == 4;
            var conditionCompleted = questCompleted
                                     || (questState?.CompletedConditions.Contains(definition.ConditionId) ?? false);
            var inventorySource = definition.FoundInRaidRequired
                ? snapshot.FoundInRaidInventory
                : snapshot.Inventory;
            var ownedMatching = definition.TargetTemplateIds.Sum(target => inventorySource.GetValueOrDefault(target));
            var ownedSelected = inventorySource.GetValueOrDefault(templateId);
            var missing = conditionCompleted ? 0d : Math.Max(0d, definition.RequiredCount - ownedMatching);
            var active = statusCode is 2 or 3;
            var progressText = conditionCompleted
                ? "Condition completed"
                : active
                    ? $"Owned now: {ownedMatching:N0}/{definition.RequiredCount:N0} matching item(s)"
                    : statusCode == 1
                        ? $"Quest available to start; owned now: {ownedMatching:N0}/{definition.RequiredCount:N0}"
                        : $"Future requirement; owned now: {ownedMatching:N0}/{definition.RequiredCount:N0}";

            output.Add(new HermesQuestItemUse(
                definition.QuestName,
                definition.TraderName,
                status,
                FriendlyQuestConditionType(definition.ConditionType),
                definition.RequiredCount,
                ownedMatching,
                ownedSelected,
                missing,
                definition.FoundInRaidRequired,
                conditionCompleted,
                questCompleted,
                active,
                progressText));
        }

        return output
            .OrderByDescending(use => use.IsActive)
            .ThenBy(use => QuestStatusRank(use.QuestStatus))
            .ThenBy(use => use.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<HermesUpgradeUse> BuildPlayerAwareUpgradeUses(
        HermesCatalogItem item,
        string templateId,
        ProfileSnapshot snapshot,
        MongoId sessionId)
    {
        var output = new List<HermesUpgradeUse>();
        AcquisitionEstimate? cachedAcquisition = null;
        foreach (var area in _areasByType!.Values)
        {
            var currentLevel = snapshot.Areas.GetValueOrDefault(area.Type)?.Level ?? 0;
            var profileArea = snapshot.Areas.GetValueOrDefault(area.Type);
            foreach (var stage in area.Stages.Values.OrderBy(stage => stage.Level))
            {
                foreach (var requirement in stage.Requirements.Where(value =>
                             string.Equals(value.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)))
                {
                    var owned = requirement.FoundInRaidRequired
                        ? snapshot.FoundInRaidInventory.GetValueOrDefault(templateId)
                        : snapshot.Inventory.GetValueOrDefault(templateId);
                    var missing = Math.Max(0d, requirement.Count - owned);
                    var isNext = stage.Level == currentLevel + 1;
                    string status;
                    if (stage.Level <= currentLevel)
                    {
                        status = "Completed upgrade";
                    }
                    else if (profileArea?.Constructing == true && isNext)
                    {
                        status = "Upgrade in progress";
                    }
                    else if (isNext)
                    {
                        status = BuildAreaEvaluation(area, snapshot, false, sessionId).Summary.Status;
                    }
                    else
                    {
                        status = "Future upgrade";
                    }

                    string? acquisitionSource = null;
                    long? estimatedMissingCost = null;
                    if (missing > 0d && stage.Level > currentLevel)
                    {
                        cachedAcquisition ??= EstimateAcquisition(
                            item,
                            sessionId,
                            catalogService.GetReferencePrice(item.TemplateId));
                        acquisitionSource = cachedAcquisition.Source;
                        estimatedMissingCost = cachedAcquisition.UnitPrice.HasValue
                            ? Convert.ToInt64(Math.Round(cachedAcquisition.UnitPrice.Value * missing))
                            : null;
                    }

                    output.Add(new HermesUpgradeUse(
                        area.Name,
                        currentLevel,
                        stage.Level,
                        status,
                        requirement.Count,
                        owned,
                        missing,
                        missing <= 0d || stage.Level <= currentLevel,
                        isNext,
                        requirement.FoundInRaidRequired,
                        acquisitionSource,
                        estimatedMissingCost));
                }
            }
        }

        return output
            .OrderByDescending(use => use.IsNextUpgrade)
            .ThenBy(use => use.TargetLevel <= use.CurrentLevel ? 2 : 0)
            .ThenBy(use => use.AreaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(use => use.TargetLevel)
            .ToList();
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
            craft.Key,
            craft.StationName,
            currentStationLevel,
            craft.RequiredStationLevel,
            craft.OutputName,
            craft.OutputQuantity,
            craft.DurationSeconds,
            Math.Max(0d, itemCount),
            owned,
            missing,
            unlocked,
            evaluation.CanStartNow,
            production is not null && !production.IsComplete,
            production?.IsComplete == true,
            evaluation.Status);
    }

    private AreaEvaluation BuildAreaEvaluation(
        AreaDefinition area,
        ProfileSnapshot snapshot,
        bool includeAcquisition,
        MongoId sessionId)
    {
        snapshot.Areas.TryGetValue(area.Type, out var profileArea);
        profileArea ??= new ProfileAreaState(area.Type, 0, false, false, null, 0, 0d);

        var currentLevel = Math.Max(0, profileArea.Level);
        var maximumLevel = area.Stages.Count == 0 ? currentLevel : area.Stages.Keys.Max();
        var targetStage = area.Stages.Values
            .Where(stage => stage.Level > currentLevel)
            .OrderBy(stage => stage.Level)
            .FirstOrDefault();

        var requirements = targetStage is null
            ? new List<HermesHideoutRequirement>()
            : targetStage.Requirements
                .Select(requirement => EvaluateRequirement(requirement, snapshot, includeAcquisition, sessionId))
                .Where(requirement => requirement is not null)
                .Cast<HermesHideoutRequirement>()
                .ToList();

        string status;
        if (!area.Enabled)
        {
            status = "Unavailable";
        }
        else if (profileArea.Constructing)
        {
            status = "Upgrade in progress";
        }
        else if (targetStage is null || currentLevel >= maximumLevel)
        {
            status = "Maximum level reached";
        }
        else if (requirements.All(requirement => requirement.IsMet))
        {
            status = "Ready to upgrade";
        }
        else if (requirements.Where(requirement => !requirement.IsMet)
                     .All(requirement => requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase)))
        {
            status = "Missing materials";
        }
        else
        {
            status = "Blocked by progression";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? secondsUntilComplete = profileArea.Constructing && profileArea.CompleteTime.HasValue
            ? Math.Max(0L, profileArea.CompleteTime.Value - now)
            : null;

        var summary = new HermesHideoutAreaSummary(
            area.Key,
            area.Name,
            currentLevel,
            maximumLevel,
            targetStage?.Level,
            status,
            profileArea.Active,
            profileArea.Constructing,
            secondsUntilComplete,
            requirements.Count(requirement =>
                requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase) && !requirement.IsMet),
            requirements.Sum(requirement =>
                requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(Math.Round((requirement.UnitPrice ?? 0L) * requirement.Missing))
                    : 0L));

        return new AreaEvaluation(
            summary,
            Convert.ToInt32(Math.Round(targetStage?.ConstructionSeconds ?? 0d)),
            requirements);
    }

    private HermesHideoutRequirement? EvaluateRequirement(
        RequirementDefinition requirement,
        ProfileSnapshot snapshot,
        bool includeAcquisition,
        MongoId sessionId)
    {
        var type = requirement.Type.Trim();

        if (IsItemRequirement(requirement))
        {
            if (string.IsNullOrWhiteSpace(requirement.TemplateId)
                || !MongoId.IsValidMongoId(requirement.TemplateId))
            {
                return null;
            }

            var templateId = new MongoId(requirement.TemplateId);
            var catalogItem = catalogService.ResolveTemplate(templateId);
            if (catalogItem is null)
            {
                // The player-facing HERMES catalog deliberately excludes quest-only
                // and handbook-less items. Do not leak those objects through hideout data.
                return null;
            }

            var owned = requirement.FoundInRaidRequired
                ? snapshot.FoundInRaidInventory.GetValueOrDefault(requirement.TemplateId)
                : snapshot.Inventory.GetValueOrDefault(requirement.TemplateId);
            var required = Math.Max(0d, requirement.Count);
            var missing = Math.Max(0d, required - owned);
            var referencePrice = catalogService.GetReferencePrice(templateId);
            string? source = referencePrice.HasValue ? "Handbook reference" : null;
            long? unitPrice = referencePrice;

            if (includeAcquisition && missing > 0d)
            {
                var acquisition = EstimateAcquisition(catalogItem, sessionId, referencePrice);
                source = acquisition.Source;
                unitPrice = acquisition.UnitPrice;
            }

            return new HermesHideoutRequirement(
                "Item",
                catalogItem.Name,
                required,
                owned,
                missing,
                missing <= 0d,
                requirement.FoundInRaidRequired,
                requirement.FoundInRaidRequired ? "Found in raid required" : null,
                source,
                unitPrice,
                unitPrice.HasValue ? Convert.ToInt64(Math.Round(unitPrice.Value * missing)) : null);
        }

        if (type.Equals("Area", StringComparison.OrdinalIgnoreCase)
            || requirement.AreaType.HasValue)
        {
            var requiredLevel = Math.Max(0, requirement.RequiredLevel);
            var ownedLevel = requirement.AreaType.HasValue
                ? snapshot.Areas.GetValueOrDefault(requirement.AreaType.Value)?.Level ?? 0
                : 0;
            var areaName = requirement.AreaType.HasValue
                ? GetAreaName(requirement.AreaType.Value)
                : "Hideout area";

            return new HermesHideoutRequirement(
                "Area",
                areaName,
                requiredLevel,
                ownedLevel,
                Math.Max(0, requiredLevel - ownedLevel),
                ownedLevel >= requiredLevel,
                false,
                $"Requires {areaName} Level {requiredLevel}",
                null,
                null,
                null);
        }

        if (type.Contains("Trader", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(requirement.TraderId))
        {
            var requiredLevel = Math.Max(1, requirement.LoyaltyLevel);
            var ownedLevel = !string.IsNullOrWhiteSpace(requirement.TraderId)
                ? snapshot.TraderLoyalty.GetValueOrDefault(requirement.TraderId, 0)
                : 0;
            var traderName = !string.IsNullOrWhiteSpace(requirement.TraderId)
                ? _traderNames!.GetValueOrDefault(requirement.TraderId, "Trader")
                : "Trader";

            return new HermesHideoutRequirement(
                "Trader",
                traderName,
                requiredLevel,
                ownedLevel,
                Math.Max(0, requiredLevel - ownedLevel),
                ownedLevel >= requiredLevel,
                false,
                $"Requires {traderName} LL{requiredLevel}",
                null,
                null,
                null);
        }

        if (type.Equals("Skill", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(requirement.SkillName))
        {
            var requiredLevel = Math.Max(0, requirement.SkillLevel);
            var skillName = string.IsNullOrWhiteSpace(requirement.SkillName) ? "Skill" : requirement.SkillName;
            var ownedLevel = snapshot.SkillLevels.GetValueOrDefault(skillName, 0);

            return new HermesHideoutRequirement(
                "Skill",
                skillName,
                requiredLevel,
                ownedLevel,
                Math.Max(0, requiredLevel - ownedLevel),
                ownedLevel >= requiredLevel,
                false,
                $"Requires {skillName} Level {requiredLevel}",
                null,
                null,
                null);
        }

        if (type.Contains("Quest", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(requirement.QuestId))
        {
            var complete = !string.IsNullOrWhiteSpace(requirement.QuestId)
                           && snapshot.CompletedQuests.Contains(requirement.QuestId);
            var questName = !string.IsNullOrWhiteSpace(requirement.QuestId)
                            && MongoId.IsValidMongoId(requirement.QuestId)
                ? catalogService.GetQuestName(new MongoId(requirement.QuestId)) ?? "Required quest"
                : "Required quest";

            return new HermesHideoutRequirement(
                "Quest",
                questName,
                1,
                complete ? 1 : 0,
                complete ? 0 : 1,
                complete,
                false,
                complete ? "Quest completed" : $"Complete quest \"{questName}\"",
                null,
                null,
                null);
        }

        return new HermesHideoutRequirement(
            string.IsNullOrWhiteSpace(type) ? "Requirement" : FormatDisplayName(type),
            string.IsNullOrWhiteSpace(type) ? "Additional requirement" : FormatDisplayName(type),
            1,
            1,
            0,
            true,
            false,
            "SPT reports this requirement as satisfied or informational.",
            null,
            null,
            null);
    }

    private AcquisitionEstimate EstimateAcquisition(
        HermesCatalogItem item,
        MongoId sessionId,
        long? referencePrice)
    {
        var candidates = new List<AcquisitionEstimate>();

        if (referencePrice is > 0)
        {
            candidates.Add(new AcquisitionEstimate("Handbook reference", referencePrice.Value));
        }

        try
        {
            var traders = traderService.GetSummary(item.ItemKey, null, sessionId);
            foreach (var offer in traders.PurchaseOffers.Where(offer => offer.IsAvailable))
            {
                foreach (var payment in offer.PaymentOptions.Where(payment => payment.EstimatedRoubleValue > 0))
                {
                    candidates.Add(new AcquisitionEstimate(
                        payment.IsCash ? offer.TraderName : $"{offer.TraderName} barter",
                        payment.EstimatedRoubleValue));
                }
            }

            var market = marketService.GetSummary(item.ItemKey, sessionId);
            if (market.FleaUnlocked && market.LowestPrice is > 0)
            {
                candidates.Add(new AcquisitionEstimate("Local flea market", market.LowestPrice.Value));
            }

            if (market.CheapestAvailableTraderBuyPrice is > 0)
            {
                var source = string.IsNullOrWhiteSpace(market.CheapestAvailableTraderName)
                    ? "Vanilla trader"
                    : market.CheapestAvailableTraderName;
                candidates.Add(new AcquisitionEstimate(source!, market.CheapestAvailableTraderBuyPrice.Value));
            }
        }
        catch
        {
            // Handbook fallback remains available when market analysis fails.
        }

        return candidates
            .Where(candidate => candidate.UnitPrice > 0)
            .OrderBy(candidate => candidate.UnitPrice)
            .FirstOrDefault()
            ?? new AcquisitionEstimate(null, null);
    }

    private HermesCraftSummary BuildCraftEvaluation(
        CraftDefinition craft,
        ProfileSnapshot snapshot,
        MongoId sessionId,
        IDictionary<string, ItemValuation> valuationCache,
        IReadOnlyList<HermesCraftIngredient>? prebuiltIngredients = null,
        bool includeLivePricing = true)
    {
        var stationLevel = snapshot.Areas.GetValueOrDefault(craft.AreaType)?.Level ?? 0;
        var stationReady = stationLevel >= craft.RequiredStationLevel;
        var ingredients = prebuiltIngredients
                          ?? BuildCraftIngredients(craft, snapshot, sessionId, valuationCache, includeLivePricing);
        var ingredientsReady = ingredients.All(ingredient => ingredient.IsMet);
        var questReady = string.IsNullOrWhiteSpace(craft.QuestId)
                         || snapshot.CompletedQuests.Contains(craft.QuestId);
        var unlocked = !craft.Locked || snapshot.UnlockedRecipes.Contains(craft.Id);
        var activeProduction = snapshot.Productions.FirstOrDefault(production =>
            string.Equals(production.RecipeId, craft.Id, StringComparison.OrdinalIgnoreCase));
        var stationBusy = snapshot.Productions.Any(production =>
            production.InProgress
            && _craftsById!.TryGetValue(production.RecipeId, out var activeCraft)
            && activeCraft.AreaType == craft.AreaType
            && !string.Equals(activeCraft.Id, craft.Id, StringComparison.OrdinalIgnoreCase));

        var canStart = stationReady && ingredientsReady && questReady && unlocked && !stationBusy && activeProduction is null;
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
        else if (!questReady || !unlocked)
        {
            var questName = craft.QuestId is not null && MongoId.IsValidMongoId(craft.QuestId)
                ? catalogService.GetQuestName(new MongoId(craft.QuestId))
                : null;
            status = string.IsNullOrWhiteSpace(questName)
                ? "Locked by progression"
                : $"Complete quest \"{questName}\"";
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
            }
        }

        var cashProfit = outputValue - additionalCashCost;
        var economicProfit = outputValue - economicInputValue;
        var economicProfitPerHour = craft.DurationSeconds > 0
            ? Convert.ToInt64(Math.Round(economicProfit / (craft.DurationSeconds / 3600d)))
            : economicProfit;

        return new HermesCraftSummary(
            craft.Key,
            craft.StationName,
            craft.RequiredStationLevel,
            craft.OutputName,
            craft.OutputQuantity,
            craft.DurationSeconds,
            canStart,
            status,
            acquisitionPlanComplete,
            additionalCashCost,
            ownedIngredientValue,
            economicInputValue,
            outputValue,
            cashProfit,
            economicProfit,
            economicProfitPerHour,
            activeProduction is not null,
            activeProduction?.IsComplete == true);
    }

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
                    if (valuation.HandbookUnitValue is > 0)
                    {
                        acquisitionPlan.Add(new HermesCraftAcquisitionLine(
                            "Handbook fallback — no current purchase source",
                            remaining,
                            valuation.HandbookUnitValue.Value,
                            RoundCost(valuation.HandbookUnitValue, remaining),
                            true));
                    }
                }

                note = unavailableQuantity <= 0d
                    ? "Missing quantity is allocated across the cheapest currently available trader, barter, and local flea sources."
                    : "Part of the missing quantity has no current trader or flea source; handbook value is shown only as a fallback estimate.";
            }

            var purchaseCost = acquisitionPlan.Sum(line => line.TotalCost);
            var acquisitionAvailable = missing <= 0d || unavailableQuantity <= 0d;

            output.Add(new HermesCraftIngredient(
                catalogItem.Name,
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

        // Craft summary requests must stay fast. They can cover hundreds of recipes,
        // so only use handbook values there. Live trader/flea scans are reserved for
        // the selected recipe detail request.
        if (!includeLivePricing)
        {
            var lightweight = new ItemValuation(
                handbook,
                [],
                handbook is > 0 ? "handbook reference" : null,
                handbook);
            cache[item.ItemKey] = lightweight;
            return lightweight;
        }

        var acquisitionQuotes = new List<AcquisitionQuote>();
        var economicCandidates = new List<ValueEstimate>();

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
            if (market.FleaUnlocked)
            {
                foreach (var offer in market.LowestOffers.Where(offer => offer.UnitPrice > 0 && offer.Quantity > 0))
                {
                    acquisitionQuotes.Add(new AcquisitionQuote(
                        "Local flea",
                        offer.UnitPrice,
                        offer.Quantity));
                }
            }

            if (market.FleaUnlocked
                && market.CanSellOnFlea
                && market.EstimatedNetSale is > 0)
            {
                economicCandidates.Add(new ValueEstimate(
                    "local flea net",
                    market.EstimatedNetSale.Value));
            }
        }
        catch
        {
            // Trader and handbook values remain available.
        }

        if (economicCandidates.Count == 0 && handbook is > 0)
        {
            economicCandidates.Add(new ValueEstimate(
                "handbook fallback",
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

    private List<HermesActiveProductionSummary> BuildActiveProductions(ProfileSnapshot snapshot)
    {
        var output = new List<HermesActiveProductionSummary>();

        foreach (var production in snapshot.Productions)
        {
            _craftsById!.TryGetValue(production.RecipeId, out var craft);
            var outputName = craft?.OutputName ?? production.OutputName ?? "Hideout production";
            var stationName = craft?.StationName ?? production.StationName ?? "Hideout station";
            var outputQuantity = craft?.OutputQuantity ?? Math.Max(1, production.OutputQuantity);
            var remaining = production.IsComplete
                ? 0L
                : Math.Max(0L, Convert.ToInt64(Math.Ceiling(production.ProductionTime - production.Progress)));
            var status = production.IsComplete
                ? "Ready to collect"
                : production.InProgress
                    ? "In production"
                    : "Paused";

            output.Add(new HermesActiveProductionSummary(
                stationName,
                outputName,
                outputQuantity,
                production.IsComplete,
                production.IsContinuous,
                remaining,
                status));
        }

        return output
            .OrderByDescending(production => production.IsComplete)
            .ThenBy(production => production.SecondsRemaining)
            .ThenBy(production => production.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private HermesHideoutResourceSummary BuildResourceSummary(
        ProfileSnapshot snapshot,
        IReadOnlyList<HermesActiveProductionSummary> productions)
    {
        var generatorType = _areasByType!.Values
            .FirstOrDefault(area => area.Name.Equals("Generator", StringComparison.OrdinalIgnoreCase))
            ?.Type;
        var generator = generatorType.HasValue
            ? snapshot.Areas.GetValueOrDefault(generatorType.Value)
            : null;
        var fuelRemaining = generator?.ResourceRemaining ?? 0d;
        long? runtime = generator?.Active == true && fuelRemaining > 0d && _generatorFuelFlowRate > 0d
            ? Convert.ToInt64(Math.Floor(fuelRemaining / _generatorFuelFlowRate))
            : null;

        return new HermesHideoutResourceSummary(
            generator?.Active == true,
            generator?.ResourceItemCount ?? 0,
            fuelRemaining,
            runtime,
            snapshot.FuelCounter,
            snapshot.AirFilterCounter,
            snapshot.WaterFilterCounter,
            productions.Count(production => !production.IsComplete),
            productions.Count(production => production.IsComplete));
    }


    private ProfileSnapshot? BuildProfileSnapshot(MongoId sessionId)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return null;
        }

        var root = JsonNode.Parse(jsonUtil.Serialize(profile) ?? "{}");
        if (root is null)
        {
            return null;
        }

        var inventory = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var foundInRaid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var inventoryItems = GetArray(GetProperty(GetProperty(root, "Inventory", "inventory"), "items", "Items"));
        foreach (var node in inventoryItems)
        {
            var item = node as JsonObject;
            if (item is null)
            {
                continue;
            }

            var templateId = ReadString(item, "_tpl", "Template", "template");
            if (string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }

            var upd = GetProperty(item, "upd", "Upd");
            var count = Math.Max(1d, ReadDouble(upd, 1d, "StackObjectsCount", "stackObjectsCount"));
            inventory[templateId] = inventory.GetValueOrDefault(templateId) + count;

            if (ReadBool(upd, false, "SpawnedInSession", "spawnedInSession"))
            {
                foundInRaid[templateId] = foundInRaid.GetValueOrDefault(templateId) + count;
            }
        }

        var areas = new Dictionary<int, ProfileAreaState>();
        var hideout = GetProperty(root, "Hideout", "hideout");
        foreach (var node in GetArray(GetProperty(hideout, "Areas", "areas")))
        {
            if (node is not JsonObject area)
            {
                continue;
            }

            var type = ReadInt(area, -1, "type", "Type");
            if (type < 0)
            {
                continue;
            }

            var resourceItemCount = 0;
            var resourceRemaining = 0d;
            foreach (var slotNode in GetArray(GetProperty(area, "slots", "Slots")))
            {
                var itemNode = GetProperty(slotNode, "item", "Item", "items", "Items");
                foreach (var resourceNode in GetArray(itemNode))
                {
                    var upd = GetProperty(resourceNode, "upd", "Upd");
                    var resource = GetProperty(upd, "Resource", "resource");
                    var value = ReadDouble(resource, 0d, "Value", "value");
                    if (value > 0d)
                    {
                        resourceItemCount++;
                        resourceRemaining += value;
                    }
                }
            }

            areas[type] = new ProfileAreaState(
                type,
                ReadInt(area, 0, "level", "Level"),
                ReadBool(area, false, "active", "Active"),
                ReadBool(area, false, "constructing", "Constructing"),
                ReadNullableLong(area, "completeTime", "CompleteTime"),
                resourceItemCount,
                resourceRemaining);
        }

        var traderLoyalty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (GetProperty(root, "TradersInfo", "tradersInfo") is JsonObject traders)
        {
            foreach (var pair in traders)
            {
                traderLoyalty[pair.Key] = ReadInt(pair.Value, 0, "loyaltyLevel", "LoyaltyLevel");
            }
        }

        var skillLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skills = GetProperty(root, "Skills", "skills");
        foreach (var skillNode in GetArray(GetProperty(skills, "Common", "common")))
        {
            var skillName = ReadString(skillNode, "Id", "id");
            if (string.IsNullOrWhiteSpace(skillName))
            {
                continue;
            }

            var progress = ReadDouble(skillNode, 0d, "Progress", "progress");
            skillLevels[skillName] = Math.Max(0, Convert.ToInt32(Math.Floor(progress / 100d)));
        }

        var completedQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questStates = new Dictionary<string, ProfileQuestState>(StringComparer.OrdinalIgnoreCase);
        foreach (var questNode in GetArray(GetProperty(root, "Quests", "quests")))
        {
            var questId = ReadString(questNode, "qid", "QId", "questId", "QuestId", "_id", "id");
            if (string.IsNullOrWhiteSpace(questId))
            {
                continue;
            }

            var statusText = ReadString(questNode, "status", "Status") ?? string.Empty;
            var statusCode = ParseQuestStatusCode(statusText);
            var completedConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectStrings(GetProperty(questNode, "completedConditions", "CompletedConditions"), completedConditions);
            questStates[questId] = new ProfileQuestState(statusCode, completedConditions);
            if (statusCode == 4)
            {
                completedQuests.Add(questId);
            }
        }

        var unlockedRecipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectStrings(
            GetProperty(GetProperty(root, "UnlockedInfo", "unlockedInfo"),
                "unlockedProductionRecipe", "UnlockedProductionRecipe"),
            unlockedRecipes);

        var productions = new List<ProfileProductionState>();
        var productionNode = GetProperty(hideout, "Production", "production");
        if (productionNode is JsonObject productionObject)
        {
            foreach (var pair in productionObject)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                var recipeId = ReadString(pair.Value, "RecipeId", "recipeId") ?? pair.Key;
                if (string.IsNullOrWhiteSpace(recipeId))
                {
                    continue;
                }

                var products = GetArray(GetProperty(pair.Value, "Products", "products"));
                string? outputName = null;
                var outputQuantity = 1;
                if (products.FirstOrDefault() is JsonObject product)
                {
                    var outputTemplate = ReadString(product, "_tpl", "Template", "template");
                    if (!string.IsNullOrWhiteSpace(outputTemplate) && MongoId.IsValidMongoId(outputTemplate))
                    {
                        outputName = catalogService.ResolveTemplate(new MongoId(outputTemplate))?.Name;
                    }

                    outputQuantity = Convert.ToInt32(Math.Max(1d,
                        ReadDouble(GetProperty(product, "upd", "Upd"), 1d,
                            "StackObjectsCount", "stackObjectsCount")));
                }

                productions.Add(new ProfileProductionState(
                    recipeId,
                    ReadDouble(pair.Value, 0d, "Progress", "progress"),
                    ReadDouble(pair.Value, 0d, "ProductionTime", "productionTime"),
                    ReadBool(pair.Value, false, "inProgress", "InProgress"),
                    ReadBool(pair.Value, false, "sptIsComplete", "SptIsComplete")
                    || ReadBool(pair.Value, false, "AvailableForFinish", "availableForFinish"),
                    ReadBool(pair.Value, false, "sptIsContinuous", "SptIsContinuous"),
                    outputName,
                    outputQuantity,
                    null));
            }
        }

        var counters = GetProperty(hideout, "HideoutCounters", "hideoutCounters");
        return new ProfileSnapshot(
            inventory,
            foundInRaid,
            areas,
            traderLoyalty,
            skillLevels,
            completedQuests,
            questStates,
            unlockedRecipes,
            productions,
            ReadNullableDouble(counters, "fuelCounter", "FuelCounter"),
            ReadNullableDouble(counters, "airFilterCounter", "AirFilterCounter"),
            ReadNullableDouble(counters, "waterFilterCounter", "WaterFilterCounter"));
    }

    private void EnsureStaticIndex()
    {
        if (_areasByKey is not null)
        {
            return;
        }

        lock (_sync)
        {
            if (_areasByKey is not null)
            {
                return;
            }

            var root = JsonNode.Parse(jsonUtil.Serialize(databaseService.GetHideout()) ?? "{}");
            var areasByKey = new Dictionary<string, AreaDefinition>(StringComparer.OrdinalIgnoreCase);
            var areasByType = new Dictionary<int, AreaDefinition>();

            foreach (var node in GetArray(GetProperty(root, "areas", "Areas")))
            {
                if (node is not JsonObject areaObject)
                {
                    continue;
                }

                var type = ReadInt(areaObject, -1, "type", "Type");
                if (type < 0)
                {
                    continue;
                }

                var stages = new Dictionary<int, StageDefinition>();
                if (GetProperty(areaObject, "stages", "Stages") is JsonObject stagesObject)
                {
                    foreach (var pair in stagesObject)
                    {
                        if (!int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                            || pair.Value is not JsonObject stageObject)
                        {
                            continue;
                        }

                        stages[level] = new StageDefinition(
                            level,
                            ReadDouble(stageObject, 0d, "constructionTime", "ConstructionTime"),
                            ParseRequirements(GetArray(GetProperty(stageObject, "requirements", "Requirements"))));
                    }
                }

                var definition = new AreaDefinition(
                    type,
                    CreateOpaqueKey("AREA", type.ToString(CultureInfo.InvariantCulture)),
                    GetAreaName(type),
                    ReadBool(areaObject, true, "enabled", "IsEnabled"),
                    stages);
                areasByKey[definition.Key] = definition;
                areasByType[type] = definition;
            }

            var craftsByKey = new Dictionary<string, CraftDefinition>(StringComparer.OrdinalIgnoreCase);
            var craftsById = new Dictionary<string, CraftDefinition>(StringComparer.OrdinalIgnoreCase);
            var production = GetProperty(root, "production", "Production");
            foreach (var node in GetArray(GetProperty(production, "recipes", "Recipes")))
            {
                if (node is not JsonObject recipe)
                {
                    continue;
                }

                var id = ReadString(recipe, "_id", "Id", "id");
                var outputTemplate = ReadString(recipe, "endProduct", "EndProduct");
                if (string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(outputTemplate)
                    || !MongoId.IsValidMongoId(outputTemplate))
                {
                    continue;
                }

                var outputItem = catalogService.ResolveTemplate(new MongoId(outputTemplate));
                if (outputItem is null)
                {
                    continue;
                }

                var requirements = ParseRequirements(GetArray(GetProperty(recipe, "requirements", "Requirements")));
                var areaType = ReadInt(recipe, -1, "areaType", "AreaType");
                var areaRequirement = requirements.FirstOrDefault(requirement => requirement.AreaType.HasValue);
                if (areaType < 0 && areaRequirement?.AreaType is not null)
                {
                    areaType = areaRequirement.AreaType.Value;
                }

                if (areaType < 0)
                {
                    continue;
                }

                var requiredLevel = requirements
                    .Where(requirement => requirement.AreaType == areaType)
                    .Select(requirement => requirement.RequiredLevel)
                    .DefaultIfEmpty(0)
                    .Max();
                var questId = requirements
                    .Select(requirement => requirement.QuestId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                var definition = new CraftDefinition(
                    id,
                    CreateOpaqueKey("CRAFT", id),
                    areaType,
                    GetAreaName(areaType),
                    requiredLevel,
                    outputTemplate,
                    outputItem.Name,
                    Math.Max(1, ReadInt(recipe, 1, "count", "Count")),
                    Math.Max(0, Convert.ToInt32(Math.Round(ReadDouble(recipe, 0d,
                        "productionTime", "ProductionTime")))),
                    ReadBool(recipe, false, "locked", "Locked"),
                    ReadBool(recipe, false, "continuous", "Continuous"),
                    requirements,
                    questId);
                craftsByKey[definition.Key] = definition;
                craftsById[definition.Id] = definition;
            }

            var traderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, trader) in databaseService.GetTraders())
            {
                var name = trader.Base.Nickname;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = trader.Base.Name;
                }

                traderNames[id.ToString()] = string.IsNullOrWhiteSpace(name) ? "Trader" : name;
            }

            var questUsesByTemplate = BuildQuestUsageIndex(traderNames);

            _generatorFuelFlowRate = ReadDouble(
                GetProperty(root, "settings", "Settings"),
                0d,
                "generatorFuelFlowRate",
                "GeneratorFuelFlowRate");
            _areasByKey = areasByKey;
            _areasByType = areasByType;
            _craftsByKey = craftsByKey;
            _craftsById = craftsById;
            _traderNames = traderNames;
            _questUsesByTemplate = questUsesByTemplate;
        }
    }

    private Dictionary<string, List<QuestItemUseDefinition>> BuildQuestUsageIndex(
        IReadOnlyDictionary<string, string> traderNames)
    {
        var output = new Dictionary<string, List<QuestItemUseDefinition>>(StringComparer.OrdinalIgnoreCase);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonUtil.Serialize(databaseService.GetQuests()) ?? "{}");
        }
        catch
        {
            return output;
        }

        foreach (var node in GetArray(root))
        {
            if (node is not JsonObject quest)
            {
                continue;
            }

            var questId = ReadString(quest, "_id", "Id", "id");
            if (string.IsNullOrWhiteSpace(questId))
            {
                continue;
            }

            var questName = MongoId.IsValidMongoId(questId)
                ? catalogService.GetQuestName(new MongoId(questId))
                : null;
            questName ??= ReadString(quest, "QuestName", "questName", "name", "Name") ?? "Quest";
            var traderId = ReadString(quest, "traderId", "TraderId") ?? string.Empty;
            var traderName = traderNames.GetValueOrDefault(traderId, "Trader");
            var conditions = GetProperty(quest, "conditions", "Conditions");
            var seenConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var groupName in new[] { "AvailableForFinish", "Success" })
            {
                foreach (var conditionNode in GetArray(GetProperty(conditions, groupName)))
                {
                    if (conditionNode is not JsonObject condition)
                    {
                        continue;
                    }

                    var conditionType = ReadString(condition, "conditionType", "ConditionType", "type", "Type") ?? string.Empty;
                    if (!conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase)
                        && !conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var conditionId = ReadString(condition, "id", "Id")
                                      ?? $"{questId}:{conditionType}:{seenConditions.Count}";
                    if (!seenConditions.Add(conditionId))
                    {
                        continue;
                    }

                    var targets = ReadMongoIdList(GetProperty(condition, "target", "Target"))
                        .Where(value => catalogService.ResolveTemplate(new MongoId(value)) is not null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (targets.Count == 0)
                    {
                        continue;
                    }

                    var definition = new QuestItemUseDefinition(
                        questId,
                        questName,
                        traderName,
                        conditionId,
                        conditionType,
                        Math.Max(1d, ReadDouble(condition, 1d, "value", "Value")),
                        ReadBool(condition, false, "onlyFoundInRaid", "OnlyFoundInRaid"),
                        targets);
                    foreach (var target in targets)
                    {
                        if (!output.TryGetValue(target, out var list))
                        {
                            list = [];
                            output[target] = list;
                        }

                        list.Add(definition);
                    }
                }
            }
        }

        return output;
    }

    private static IReadOnlyList<string> ReadMongoIdList(JsonNode? node)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMongoIds(node, output);
        return output.ToList();
    }

    private static void CollectMongoIds(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && MongoId.IsValidMongoId(text))
            {
                output.Add(text);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectMongoIds(child, output);
            }
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (MongoId.IsValidMongoId(pair.Key))
                {
                    output.Add(pair.Key);
                }
                CollectMongoIds(pair.Value, output);
            }
        }
    }

    private static int ParseQuestStatusCode(string status)
    {
        if (int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "availableforstart" => 1,
            "started" => 2,
            "availableforfinish" => 3,
            "success" or "completed" or "complete" => 4,
            "fail" => 5,
            "failrestartable" => 6,
            "markedasfailed" => 7,
            "expired" => 8,
            "availableafter" => 9,
            _ => 0
        };
    }

    private static string QuestStatusDisplay(int statusCode)
    {
        return statusCode switch
        {
            1 => "Available to start",
            2 => "Active",
            3 => "Ready to finish",
            4 => "Completed",
            5 => "Failed",
            6 => "Failed — restartable",
            7 => "Marked as failed",
            8 => "Expired",
            9 => "Available later",
            _ => "Future / locked"
        };
    }

    private static int QuestStatusRank(string status)
    {
        return status switch
        {
            "Active" => 0,
            "Ready to finish" => 1,
            "Available to start" => 2,
            "Available later" => 3,
            "Future / locked" => 4,
            "Completed" => 5,
            _ => 6
        };
    }

    private static string FriendlyQuestConditionType(string conditionType)
    {
        return conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase)
            ? "Hand over"
            : conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase)
                ? "Find"
                : FormatDisplayName(conditionType);
    }

    private static IReadOnlyList<RequirementDefinition> ParseRequirements(IEnumerable<JsonNode?> nodes)
    {
        var output = new List<RequirementDefinition>();
        foreach (var node in nodes)
        {
            if (node is not JsonObject requirement)
            {
                continue;
            }

            output.Add(new RequirementDefinition(
                ReadString(requirement, "type", "Type") ?? "Requirement",
                ReadString(requirement, "templateId", "TemplateId"),
                ReadDouble(requirement, 0d, "count", "Count", "resource", "Resource"),
                ReadNullableInt(requirement, "areaType", "AreaType"),
                ReadInt(requirement, 0, "requiredLevel", "RequiredLevel"),
                ReadString(requirement, "traderId", "TraderId"),
                ReadInt(requirement, 0, "loyaltyLevel", "LoyaltyLevel"),
                ReadString(requirement, "skillName", "SkillName"),
                ReadInt(requirement, 0, "skillLevel", "SkillLevel"),
                ReadString(requirement, "questId", "QuestId"),
                ReadBool(requirement, false, "isSpawnedInSession", "IsSpawnedInSession")));
        }

        return output;
    }

    private static bool IsItemRequirement(RequirementDefinition requirement)
    {
        return !string.IsNullOrWhiteSpace(requirement.TemplateId)
               && (requirement.Type.Contains("Item", StringComparison.OrdinalIgnoreCase)
                   || requirement.Type.Contains("Tool", StringComparison.OrdinalIgnoreCase)
                   || requirement.Type.Contains("Resource", StringComparison.OrdinalIgnoreCase)
                   || (!requirement.AreaType.HasValue
                       && string.IsNullOrWhiteSpace(requirement.TraderId)
                       && string.IsNullOrWhiteSpace(requirement.SkillName)
                       && string.IsNullOrWhiteSpace(requirement.QuestId)));
    }

    private static bool IsCompletedQuestStatus(string status)
    {
        return status.Equals("4", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
    }

    private static int AreaStatusRank(string status)
    {
        return status switch
        {
            "Ready to upgrade" => 0,
            "Upgrade in progress" => 1,
            "Missing materials" => 2,
            "Blocked by progression" => 3,
            "Maximum level reached" => 4,
            _ => 5
        };
    }

    private static HermesHideoutResourceSummary EmptyResources()
    {
        return new HermesHideoutResourceSummary(false, 0, 0d, null, null, null, null, 0, 0);
    }

    private static string GetAreaName(int areaType)
    {
        try
        {
            var enumName = Enum.GetName(typeof(HideoutAreas), areaType);
            return string.IsNullOrWhiteSpace(enumName)
                ? $"Hideout Area {areaType}"
                : FormatDisplayName(enumName);
        }
        catch
        {
            return $"Hideout Area {areaType}";
        }
    }

    private static string FormatDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('_', ' ').Replace('-', ' ');
        normalized = Regex.Replace(normalized, "(?<=[a-z0-9])(?=[A-Z])", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        return normalized;
    }

    private static string CreateOpaqueKey(string category, string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"HERMES:{category}:{source}"));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var pair in obj)
        {
            if (names.Any(name => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonNode?> GetArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.ToList();
        }

        if (node is JsonObject obj)
        {
            return obj.Select(pair => pair.Value).ToList();
        }

        return [];
    }

    private static string? ReadString(JsonNode? node, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static int ReadInt(JsonNode? node, int fallback, params string[] names)
    {
        var value = ReadDouble(node, fallback, names);
        return Convert.ToInt32(Math.Round(value));
    }

    private static int? ReadNullableInt(JsonNode? node, params string[] names)
    {
        var value = ReadNullableDouble(node, names);
        return value.HasValue ? Convert.ToInt32(Math.Round(value.Value)) : null;
    }

    private static long? ReadNullableLong(JsonNode? node, params string[] names)
    {
        var value = ReadNullableDouble(node, names);
        return value.HasValue ? Convert.ToInt64(Math.Round(value.Value)) : null;
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        return ReadNullableDouble(node, names) ?? fallback;
    }

    private static double? ReadNullableDouble(JsonNode? node, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue != 0;
        }

        if (value.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static void CollectStrings(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectStrings(child, output);
            }

            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (MongoId.IsValidMongoId(pair.Key))
                {
                    output.Add(pair.Key);
                }

                CollectStrings(pair.Value, output);
            }
        }
    }

    private sealed class StashReservationBuilder
    {
        public double ActiveQuestQuantity { get; set; }
        public double ActiveQuestFoundInRaidQuantity { get; set; }
        public double FutureQuestQuantity { get; set; }
        public double FutureQuestFoundInRaidQuantity { get; set; }
        public double NextHideoutQuantity { get; set; }
        public double NextHideoutFoundInRaidQuantity { get; set; }
        public double FutureHideoutQuantity { get; set; }
        public double FutureHideoutFoundInRaidQuantity { get; set; }
        public HashSet<string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record AreaDefinition(
        int Type,
        string Key,
        string Name,
        bool Enabled,
        IReadOnlyDictionary<int, StageDefinition> Stages);

    private sealed record StageDefinition(
        int Level,
        double ConstructionSeconds,
        IReadOnlyList<RequirementDefinition> Requirements);

    private sealed record RequirementDefinition(
        string Type,
        string? TemplateId,
        double Count,
        int? AreaType,
        int RequiredLevel,
        string? TraderId,
        int LoyaltyLevel,
        string? SkillName,
        int SkillLevel,
        string? QuestId,
        bool FoundInRaidRequired);

    private sealed record CraftDefinition(
        string Id,
        string Key,
        int AreaType,
        string StationName,
        int RequiredStationLevel,
        string OutputTemplateId,
        string OutputName,
        int OutputQuantity,
        int DurationSeconds,
        bool Locked,
        bool Continuous,
        IReadOnlyList<RequirementDefinition> Requirements,
        string? QuestId);

    private sealed record QuestItemUseDefinition(
        string QuestId,
        string QuestName,
        string TraderName,
        string ConditionId,
        string ConditionType,
        double RequiredCount,
        bool FoundInRaidRequired,
        IReadOnlyList<string> TargetTemplateIds);

    private sealed record ProfileQuestState(
        int StatusCode,
        IReadOnlySet<string> CompletedConditions);

    private sealed record ProfileSnapshot(
        IReadOnlyDictionary<string, double> Inventory,
        IReadOnlyDictionary<string, double> FoundInRaidInventory,
        IReadOnlyDictionary<int, ProfileAreaState> Areas,
        IReadOnlyDictionary<string, int> TraderLoyalty,
        IReadOnlyDictionary<string, int> SkillLevels,
        IReadOnlySet<string> CompletedQuests,
        IReadOnlyDictionary<string, ProfileQuestState> QuestStates,
        IReadOnlySet<string> UnlockedRecipes,
        IReadOnlyList<ProfileProductionState> Productions,
        double? FuelCounter,
        double? AirFilterCounter,
        double? WaterFilterCounter);

    private sealed record ProfileAreaState(
        int Type,
        int Level,
        bool Active,
        bool Constructing,
        long? CompleteTime,
        int ResourceItemCount,
        double ResourceRemaining);

    private sealed record ProfileProductionState(
        string RecipeId,
        double Progress,
        double ProductionTime,
        bool InProgress,
        bool IsComplete,
        bool IsContinuous,
        string? OutputName,
        int OutputQuantity,
        string? StationName);

    private sealed record AreaEvaluation(
        HermesHideoutAreaSummary Summary,
        int ConstructionSeconds,
        IReadOnlyList<HermesHideoutRequirement> Requirements);

    private sealed record AcquisitionEstimate(string? Source, long? UnitPrice);

    private sealed record AcquisitionQuote(
        string Source,
        long UnitPrice,
        double? AvailableQuantity);

    private sealed record ValueEstimate(string Source, long UnitPrice);

    private sealed record ItemValuation(
        long? HandbookUnitValue,
        IReadOnlyList<AcquisitionQuote> AcquisitionQuotes,
        string? EconomicSource,
        long? EconomicUnitValue);
}
