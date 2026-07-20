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
    private ProfileSnapshot? BuildProfileSnapshot(MongoId sessionId)
    {
        var preparedProfile = preparedProfiles.Get(sessionId);
        if (preparedProfile is null)
        {
            return null;
        }

        var root = preparedProfile.Root;

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
                string? outputTemplateId = null;
                var outputQuantity = 1;
                if (products.FirstOrDefault() is JsonObject product)
                {
                    var outputTemplate = ReadString(product, "_tpl", "Template", "template");
                    if (!string.IsNullOrWhiteSpace(outputTemplate) && MongoId.IsValidMongoId(outputTemplate))
                    {
                        outputTemplateId = outputTemplate;
                        outputName = catalogService.ResolveTemplate(new MongoId(outputTemplate))?.Name;
                    }

                    outputQuantity = Convert.ToInt32(Math.Max(1d,
                        ReadDouble(GetProperty(product, "upd", "Upd"), 1d,
                            "StackObjectsCount", "stackObjectsCount")));
                }

                var progress = ReadDouble(pair.Value, 0d, "Progress", "progress");
                var productionTime = ReadDouble(pair.Value, 0d, "ProductionTime", "productionTime");
                var inProgress = ReadBool(pair.Value, false, "inProgress", "InProgress");
                var effectiveProgress = CalculateEffectiveProductionProgress(
                    progress,
                    productionTime,
                    inProgress,
                    ReadNullableLong(pair.Value, "StartTimestamp", "startTimestamp", "StartTimeStamp", "startTimeStamp"),
                    ReadDouble(pair.Value, 0d, "SkipTime", "skipTime"));
                var isComplete = ReadBool(pair.Value, false, "sptIsComplete", "SptIsComplete")
                                 || ReadBool(pair.Value, false, "AvailableForFinish", "availableForFinish")
                                 || (productionTime > 0d && effectiveProgress >= productionTime);

                productions.Add(new ProfileProductionState(
                    pair.Key,
                    recipeId,
                    effectiveProgress,
                    productionTime,
                    inProgress,
                    isComplete,
                    ReadBool(pair.Value, false, "sptIsContinuous", "SptIsContinuous"),
                    outputName,
                    outputQuantity,
                    outputTemplateId,
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

            var root = staticData.GetHideoutRoot();
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
            var recipeUnlockQuestIds = BuildRecipeUnlockQuestIndex();
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
                var locked = ReadBool(recipe, false, "locked", "Locked");
                var questId = requirements
                    .Select(requirement => requirement.QuestId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (locked
                    && string.IsNullOrWhiteSpace(questId)
                    && recipeUnlockQuestIds.TryGetValue(id, out var rewardQuestId))
                {
                    questId = rewardQuestId;
                }

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
                    locked,
                    ReadBool(recipe, false, "continuous", "Continuous"),
                    requirements,
                    questId);
                craftsByKey[definition.Key] = definition;
                craftsById[definition.Id] = definition;
            }

            var traderNames = staticData.GetTraderNames().ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

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

    private Dictionary<string, string> BuildRecipeUnlockQuestIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonNode? root;
        try
        {
            root = staticData.GetQuestsRoot();
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

            var rewards = GetProperty(quest, "rewards", "Rewards");
            foreach (var rewardNode in GetArray(GetProperty(rewards, "Success", "success")))
            {
                if (rewardNode is not JsonObject reward)
                {
                    continue;
                }

                var rewardType = ReadString(reward, "type", "Type", "rewardType", "RewardType")
                                 ?? string.Empty;
                if (!rewardType.Contains("Production", StringComparison.OrdinalIgnoreCase)
                    && !rewardType.Contains("Recipe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targets = ReadMongoIdList(GetProperty(
                    reward,
                    "target", "Target",
                    "recipeId", "RecipeId",
                    "productionId", "ProductionId",
                    "schemeId", "SchemeId"));
                foreach (var target in targets)
                {
                    // Keep the first quest found for a recipe. Vanilla production rewards
                    // should be unique, while this also remains deterministic for modded data.
                    output.TryAdd(target, questId);
                }
            }
        }

        return output;
    }

    private Dictionary<string, List<QuestItemUseDefinition>> BuildQuestUsageIndex(
        IReadOnlyDictionary<string, string> traderNames)
    {
        var output = new Dictionary<string, List<QuestItemUseDefinition>>(StringComparer.OrdinalIgnoreCase);
        var questIdsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode? root;
        try
        {
            root = staticData.GetQuestsRoot();
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
            var normalizedQuestName = NormalizeKnowledgeText(questName);
            if (normalizedQuestName.Length > 0)
            {
                questIdsByName.TryAdd(normalizedQuestName, questId);
            }

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

        _questIdsByNormalizedName = questIdsByName;
        return output;
    }
}
