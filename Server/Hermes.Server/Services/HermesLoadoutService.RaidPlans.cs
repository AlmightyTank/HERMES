using System.Globalization;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

public sealed partial class HermesLoadoutService
{
    private IReadOnlyList<HermesRaidPlanSummary> BuildRaidPlans(
        JsonObject profileRoot,
        IReadOnlyList<HermesQuestLoadoutRequirement> questRequirements)
    {
        var activeQuests = GetActiveQuestStates(profileRoot);
        if (activeQuests.Count == 0)
        {
            return [];
        }

        JsonNode? questsRoot;
        try
        {
            questsRoot = staticData.GetQuestsRoot();
        }
        catch
        {
            return [];
        }

        var traderNames = staticData.GetTraderNames();
        var questDrafts = new List<RaidQuestDraft>();

        foreach (var node in GetArray(questsRoot))
        {
            if (node is not JsonObject quest)
            {
                continue;
            }

            var questId = ReadString(quest, "_id", "Id", "id");
            if (string.IsNullOrWhiteSpace(questId) || !activeQuests.TryGetValue(questId, out var state))
            {
                continue;
            }

            var questName = MongoId.IsValidMongoId(questId)
                ? catalogService.GetQuestName(new MongoId(questId))
                : null;
            questName ??= ReadString(quest, "QuestName", "questName", "name", "Name") ?? "Active quest";
            var traderId = ReadString(quest, "traderId", "TraderId") ?? string.Empty;
            var traderName = traderNames.GetValueOrDefault(traderId, "Trader");
            var questMapName = FriendlyMapName(ReadString(quest, "location", "Location"));
            var conditions = GetProperty(quest, "conditions", "Conditions");
            var objectivesByMap = new Dictionary<string, List<HermesRaidPlanObjective>>(StringComparer.OrdinalIgnoreCase);
            var objectiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in new[] { "AvailableForFinish", "Success" })
            {
                foreach (var conditionNode in GetArray(GetProperty(conditions, group)))
                {
                    if (conditionNode is not JsonObject condition)
                    {
                        continue;
                    }

                    var conditionType = ReadString(condition, "conditionType", "ConditionType", "type", "Type") ?? "Quest objective";
                    var conditionId = ReadString(condition, "id", "Id") ?? $"{group}:{conditionType}:{objectiveIds.Count}";
                    if (!objectiveIds.Add(conditionId))
                    {
                        continue;
                    }

                    var mapName = ResolveObjectiveMap(questId, questMapName, conditionId, conditionType, condition);
                    if (!objectivesByMap.TryGetValue(mapName, out var mapObjectives))
                    {
                        mapObjectives = [];
                        objectivesByMap[mapName] = mapObjectives;
                    }

                    var completed = state.CompletedConditions.Contains(conditionId);
                    var isRaidObjective = IsRaidObjectiveCondition(condition, conditionType);
                    mapObjectives.Add(new HermesRaidPlanObjective(
                        FriendlyQuestConditionLabel(conditionType),
                        ResolveQuestObjectiveText(questId, conditionId, condition, conditionType),
                        completed,
                        isRaidObjective,
                        completed ? "Complete" : isRaidObjective ? "Active" : "Progress"));
                }
            }

            var requirementMaps = questRequirements
                .Where(requirement => requirement.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase))
                .Select(requirement => requirement.MapName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var stageMaps = objectivesByMap.Keys
                .Concat(requirementMaps)
                .DefaultIfEmpty(questMapName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var mapName in stageMaps)
            {
                var objectives = objectivesByMap.GetValueOrDefault(mapName) ?? [];
                var relatedRequirements = questRequirements
                    .Where(requirement => requirement.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase)
                                          && requirement.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var missingRequirements = relatedRequirements.Count(requirement =>
                    requirement.IsRaidCritical && !requirement.AcquireInRaid && !requirement.IsCompleted && !requirement.IsSatisfied);
                var questStatus = state.Status == 3
                    ? "Ready to finish"
                    : missingRequirements > 0
                        ? "Missing gear"
                        : "Active";

                questDrafts.Add(new RaidQuestDraft(
                    mapName,
                    new HermesRaidPlanQuest(
                        questName,
                        traderName,
                        questStatus,
                        objectives.Count,
                        objectives.Count(objective => objective.IsCompleted),
                        missingRequirements,
                        objectives
                            .OrderBy(objective => objective.IsCompleted)
                            .ThenByDescending(objective => objective.IsRaidObjective)
                            .ThenBy(objective => objective.Description, StringComparer.OrdinalIgnoreCase)
                            .ToList())));
            }
        }

        var plans = new List<HermesRaidPlanSummary>();
        foreach (var mapGroup in questDrafts
                     .GroupBy(draft => draft.MapName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key.Equals("Any map", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mapName = mapGroup.Key;
            var quests = mapGroup
                .Select(draft => draft.Quest)
                .OrderByDescending(quest => quest.MissingRequirementCount)
                .ThenBy(quest => quest.QuestName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var combinedRequirements = BuildCombinedRaidRequirements(mapName, questRequirements);
            var missingRequirementCount = combinedRequirements.Count(requirement => !requirement.IsSatisfied);
            var objectiveCount = quests.Sum(quest => quest.ObjectiveCount);
            var completedObjectiveCount = quests.Sum(quest => quest.CompletedObjectiveCount);
            var notes = BuildRaidPlanNotes(mapName, combinedRequirements);
            var status = missingRequirementCount > 0
                ? "MISSING GEAR"
                : objectiveCount > 0 && completedObjectiveCount >= objectiveCount
                    ? "READY TO TURN IN"
                    : "PREPARED";

            plans.Add(new HermesRaidPlanSummary(
                mapName,
                status,
                quests.Count,
                objectiveCount,
                completedObjectiveCount,
                combinedRequirements.Count,
                missingRequirementCount,
                quests,
                combinedRequirements,
                notes));
        }

        return plans
            .OrderByDescending(plan => plan.MissingRequirementCount)
            .ThenBy(plan => plan.MapName.Equals("Any map", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<HermesRaidPlanRequirement> BuildCombinedRaidRequirements(
        string mapName,
        IReadOnlyList<HermesQuestLoadoutRequirement> questRequirements)
    {
        var relevant = questRequirements
            .Where(requirement => requirement.IsRaidCritical
                                  && !requirement.IsCompleted
                                  && requirement.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var output = new List<HermesRaidPlanRequirement>();

        foreach (var group in relevant.GroupBy(
                     requirement => new
                     {
                         Kind = requirement.RequirementKind.ToLowerInvariant(),
                         Equipment = requirement.RequiredEquipment.ToLowerInvariant(),
                         requirement.FoundInRaidRequired,
                         requirement.AcquireInRaid
                     }))
        {
            var values = group.ToList();
            var additive = values.Any(value => IsConsumableRaidRequirement(value.RequirementKind));
            var required = additive
                ? values.Sum(value => value.RequiredQuantity)
                : values.Max(value => value.RequiredQuantity);
            var carried = values.Max(value => value.CarriedQuantity);
            var carriedFir = values.Max(value => value.FoundInRaidCarriedQuantity);
            var effectiveCarried = group.Key.FoundInRaidRequired ? carriedFir : carried;
            var missing = group.Key.AcquireInRaid
                ? 0d
                : Math.Max(0d, required - effectiveCarried);
            var satisfied = group.Key.AcquireInRaid || missing <= 0.001d;
            var first = values[0];
            var questNames = values
                .Select(value => value.QuestName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var note = group.Key.AcquireInRaid
                ? carried >= required
                    ? $"Acquired during the raid and currently carried for {string.Join(", ", questNames)}."
                    : values.Select(value => value.Note).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                      ?? $"Acquire {FormatNumber(required)} during the raid for {string.Join(", ", questNames)}."
                : satisfied
                    ? $"Covered for {string.Join(", ", questNames)}."
                    : $"Bring {FormatNumber(missing)} more for {string.Join(", ", questNames)}.";

            output.Add(new HermesRaidPlanRequirement(
                first.RequirementKind,
                first.RequiredEquipment,
                required,
                carried,
                carriedFir,
                missing,
                group.Key.FoundInRaidRequired,
                group.Key.AcquireInRaid,
                satisfied,
                questNames,
                note));
        }

        return output
            .OrderBy(requirement => requirement.IsSatisfied)
            .ThenBy(requirement => requirement.RequirementKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.RequiredEquipment, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildRaidPlanNotes(
        string mapName,
        IReadOnlyList<HermesRaidPlanRequirement> requirements)
    {
        var notes = new List<string>();
        var weaponRequirements = requirements
            .Where(requirement => requirement.RequirementKind.Contains("Weapon", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => requirement.RequiredEquipment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var equipmentRequirements = requirements
            .Where(requirement => requirement.RequirementKind.Equals("Equipment", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => requirement.RequiredEquipment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (weaponRequirements.Count > 1)
        {
            notes.Add("Multiple weapon requirements apply on this map. Confirm one build can satisfy them together or plan separate raids.");
        }

        if (equipmentRequirements.Count > 2)
        {
            notes.Add("Several equipment restrictions apply. Review the quest cards for combinations that may require separate raids.");
        }

        if (mapName.Equals("Any map", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Any-map objectives can be combined with another raid when their individual conditions permit.");
        }

        if (requirements.Any(requirement => requirement.AcquireInRaid))
        {
            notes.Add("Items labeled Acquire in raid are required along the route but are intentionally not counted as missing pre-raid gear.");
        }

        if (requirements.Count == 0)
        {
            notes.Add("No explicit raid-item or equipment requirement is encoded for these objectives.");
        }
        else if (requirements.All(requirement => requirement.IsSatisfied))
        {
            notes.Add("All currently detectable raid-critical gear requirements are covered.");
        }

        notes.Add("Quest route keys come from the embedded TarkovForge quest-key catalog, exact HERMES vanilla rules, and local quest/key text inference. The installed SPT database remains authoritative for key identity, quest state, and map matching.");
        return notes;
    }

    private string ResolveQuestObjectiveText(
        string questId,
        string conditionId,
        JsonObject condition,
        string conditionType)
    {
        var localized = FindLocalizedObjectiveText(questId, conditionId, condition);
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        return DescribeQuestObjective(condition, conditionType);
    }

    private string? FindLocalizedObjectiveText(
        string questId,
        string conditionId,
        JsonObject condition)
    {
        EnsureLocaleStrings();

        var candidates = new List<string>();
        AddLocaleKeyCandidate(candidates, ReadString(
            condition,
            "descriptionLocaleKey",
            "DescriptionLocaleKey",
            "localeKey",
            "LocaleKey",
            "description",
            "Description"));
        AddLocaleKeyCandidate(candidates, conditionId);
        AddLocaleKeyCandidate(candidates, conditionId + " Description");
        AddLocaleKeyCandidate(candidates, conditionId + " description");
        AddLocaleKeyCandidate(candidates, conditionId + " Objective");
        AddLocaleKeyCandidate(candidates, conditionId + " objective");
        AddLocaleKeyCandidate(candidates, questId + " " + conditionId);
        AddLocaleKeyCandidate(candidates, questId + " " + conditionId + " Description");

        foreach (var key in candidates)
        {
            var value = GetLocaleString(key);
            if (IsUsefulObjectiveLocale(value, key))
            {
                return NormalizeObjectiveLocale(value!);
            }
        }

        var directDescription = ReadString(condition, "description", "Description");
        if (IsUsefulDirectObjectiveText(directDescription))
        {
            return NormalizeObjectiveLocale(directDescription!);
        }

        var counter = GetProperty(condition, "counter", "Counter");
        var nestedDescriptions = new List<string>();
        foreach (var nestedNode in GetArray(GetProperty(counter, "conditions", "Conditions")))
        {
            if (nestedNode is not JsonObject nested)
            {
                continue;
            }

            var nestedId = ReadString(nested, "id", "Id");
            if (string.IsNullOrWhiteSpace(nestedId))
            {
                continue;
            }

            var nestedText = FindLocalizedObjectiveText(questId, nestedId, nested);
            if (!string.IsNullOrWhiteSpace(nestedText))
            {
                nestedDescriptions.Add(nestedText);
            }
        }

        var distinct = nestedDescriptions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0
            ? null
            : string.Join("; ", distinct);
    }

    private string? GetLocaleString(string key)
    {
        lock (_localeSync)
        {
            return _localeStrings is not null
                   && _localeStrings.TryGetValue(key, out var localized)
                ? localized
                : null;
        }
    }

    private static void AddLocaleKeyCandidate(ICollection<string> output, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)
            && !output.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            output.Add(candidate.Trim());
        }
    }

    private static bool IsUsefulObjectiveLocale(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeObjectiveLocale(value);
        return normalized.Length > 2
               && !normalized.Equals(key, StringComparison.OrdinalIgnoreCase)
               && !normalized.Equals("???", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsefulDirectObjectiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeObjectiveLocale(value);
        return normalized.Length > 8
               && normalized.Any(char.IsWhiteSpace)
               && !MongoId.IsValidMongoId(normalized);
    }

    private static string NormalizeObjectiveLocale(string value)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(value)
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var output = new List<char>(decoded.Length);
        var insideTag = false;
        foreach (var character in decoded)
        {
            if (character == '<')
            {
                insideTag = true;
                continue;
            }

            if (character == '>' && insideTag)
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                output.Add(character);
            }
        }

        return string.Join(
            " ",
            new string(output.ToArray())
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private string DescribeQuestObjective(JsonObject condition, string conditionType)
    {
        var required = Math.Max(1d, ReadDouble(condition, 1d, "value", "Value"));
        var targetTemplates = GetExistingTargetTemplates(GetProperty(condition, "target", "Target"));
        var targetNames = targetTemplates
            .Select(templateId => GetTemplate(templateId).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var targetText = targetNames.Count == 0 ? string.Empty : string.Join(" or ", targetNames);
        var fir = ReadBool(condition, false, "onlyFoundInRaid", "OnlyFoundInRaid");

        if (IsRequiredEquipmentCondition(conditionType))
        {
            return targetText.Length > 0
                ? $"Wear or equip {targetText}."
                : "Satisfy the required equipment condition.";
        }

        if (IsPlantOrPlacementCondition(conditionType))
        {
            if (targetText.Length == 0 && conditionType.Contains("Beacon", StringComparison.OrdinalIgnoreCase))
            {
                targetText = GetTemplate(Ms2000MarkerTemplateId).Exists
                    ? GetTemplate(Ms2000MarkerTemplateId).Name
                    : "the required marker";
            }

            return targetText.Length > 0
                ? $"Plant or place {FormatNumber(required)} × {targetText}."
                : "Complete the planting or placement objective.";
        }

        if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase))
        {
            return targetText.Length > 0
                ? $"Hand over {FormatNumber(required)} × {targetText}{(fir ? " found in raid" : string.Empty)}."
                : "Complete the item handover objective.";
        }

        if (conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
        {
            return targetText.Length > 0
                ? $"Find {FormatNumber(required)} × {targetText}{(fir ? " found in raid" : string.Empty)}."
                : "Complete the item-find objective.";
        }

        if (conditionType.Equals("CounterCreator", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeCounterObjective(condition, required);
        }

        if (conditionType.Contains("Kill", StringComparison.OrdinalIgnoreCase))
        {
            return $"Complete {FormatNumber(required)} eliminations under the quest conditions.";
        }

        if (conditionType.Contains("Visit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Exploration", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Location", StringComparison.OrdinalIgnoreCase))
        {
            return "Visit the required objective location.";
        }

        if (conditionType.Contains("Exit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Survive", StringComparison.OrdinalIgnoreCase))
        {
            return "Survive and extract while satisfying the quest conditions.";
        }

        return $"Complete the {FormatDisplayName(conditionType)} objective ({FormatNumber(required)} required).";
    }

    private string DescribeCounterObjective(JsonObject condition, double required)
    {
        var counter = GetProperty(condition, "counter", "Counter");
        var descriptions = new List<string>();
        foreach (var nestedNode in GetArray(GetProperty(counter, "conditions", "Conditions")))
        {
            if (nestedNode is not JsonObject nested)
            {
                continue;
            }

            var nestedType = ReadString(nested, "conditionType", "ConditionType", "type", "Type") ?? string.Empty;
            if (nestedType.Contains("Kill", StringComparison.OrdinalIgnoreCase))
            {
                descriptions.Add("elimination conditions");
                continue;
            }

            if (nestedType.Contains("Visit", StringComparison.OrdinalIgnoreCase)
                || nestedType.Contains("Location", StringComparison.OrdinalIgnoreCase))
            {
                descriptions.Add("location conditions");
                continue;
            }

            var equipmentTargets = GetExistingTargetTemplates(GetProperty(nested, "equipmentInclusive", "EquipmentInclusive"));
            if (equipmentTargets.Count > 0)
            {
                var names = equipmentTargets.Select(target => GetTemplate(target).Name).Distinct().Take(3);
                descriptions.Add($"equipment: {string.Join(" or ", names)}");
            }

            var weaponTargets = GetExistingTargetTemplates(GetProperty(nested, "weapon", "Weapon"));
            if (weaponTargets.Count > 0)
            {
                var names = weaponTargets.Select(target => GetTemplate(target).Name).Distinct().Take(3);
                descriptions.Add($"weapon: {string.Join(" or ", names)}");
            }

            var calibers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectStrings(GetProperty(nested, "weaponCaliber", "WeaponCaliber"), calibers);
            if (calibers.Count > 0)
            {
                descriptions.Add($"caliber: {string.Join(" or ", calibers.Select(FriendlyCaliber).Take(3))}");
            }
        }

        var detail = descriptions.Count == 0
            ? "the encoded raid conditions"
            : string.Join(", ", descriptions.Distinct(StringComparer.OrdinalIgnoreCase));
        return $"Complete {FormatNumber(required)} progress toward {detail}.";
    }

    private static bool IsRaidObjectiveCondition(JsonObject condition, string conditionType)
    {
        if (IsPlantOrPlacementCondition(conditionType)
            || IsRequiredEquipmentCondition(conditionType)
            || conditionType.Equals("CounterCreator", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Kill", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Visit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Exploration", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Location", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Exit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Survive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase)
            || conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
        {
            return ReadBool(condition, false, "countInRaid", "CountInRaid")
                   || ReadBool(condition, false, "oneSessionOnly", "OneSessionOnly");
        }

        return false;
    }

    private static string FriendlyQuestConditionLabel(string conditionType)
    {
        if (conditionType.Equals("CounterCreator", StringComparison.OrdinalIgnoreCase))
        {
            return "Raid objective";
        }

        if (conditionType.Contains("Kill", StringComparison.OrdinalIgnoreCase))
        {
            return "Combat objective";
        }

        if (conditionType.Contains("Visit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Exploration", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Location", StringComparison.OrdinalIgnoreCase))
        {
            return "Location objective";
        }

        if (conditionType.Contains("Exit", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("Survive", StringComparison.OrdinalIgnoreCase))
        {
            return "Extraction objective";
        }

        if (IsRequiredEquipmentCondition(conditionType))
        {
            return "Equipment objective";
        }

        if (IsPlantOrPlacementCondition(conditionType))
        {
            return conditionType.Contains("Beacon", StringComparison.OrdinalIgnoreCase)
                ? "Marker objective"
                : "Plant objective";
        }

        if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Handover objective";
        }

        if (conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Find objective";
        }

        return FormatDisplayName(conditionType);
    }

    private static bool IsConsumableRaidRequirement(string requirementKind)
    {
        return requirementKind.Contains("item", StringComparison.OrdinalIgnoreCase)
               || requirementKind.Contains("tool", StringComparison.OrdinalIgnoreCase)
               || requirementKind.Contains("key", StringComparison.OrdinalIgnoreCase)
               || requirementKind.Contains("marker", StringComparison.OrdinalIgnoreCase);
    }
}
