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
    private IReadOnlyList<HermesQuestLoadoutRequirement> BuildQuestRequirements(
        JsonObject profileRoot,
        IReadOnlyList<InventoryNode> equippedRoots,
        IReadOnlyList<InventoryNode> carriedItems,
        ICollection<HermesLoadoutWarning> warnings)
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
        var output = new List<HermesQuestLoadoutRequirement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            foreach (var group in new[] { "AvailableForFinish", "Success" })
            {
                foreach (var conditionNode in GetArray(GetProperty(conditions, group)))
                {
                    if (conditionNode is not JsonObject condition)
                    {
                        continue;
                    }

                    var conditionType = ReadString(condition, "conditionType", "ConditionType", "type", "Type") ?? string.Empty;
                    var conditionId = ReadString(condition, "id", "Id") ?? $"{group}:{conditionType}:{output.Count}";
                    var mapName = ResolveObjectiveMap(questId, questMapName, conditionId, conditionType, condition);
                    var completed = state.CompletedConditions.Contains(conditionId);
                    var required = Math.Max(1d, ReadDouble(condition, 1d, "value", "Value"));
                    var foundInRaidRequired = ReadBool(condition, false, "onlyFoundInRaid", "OnlyFoundInRaid");
                    var countInRaid = ReadBool(condition, false, "countInRaid", "CountInRaid");
                    var oneSessionOnly = ReadBool(condition, false, "oneSessionOnly", "OneSessionOnly");
                    var targetTemplates = GetExistingTargetTemplates(GetProperty(condition, "target", "Target"));

                    if (IsRequiredEquipmentCondition(conditionType))
                    {
                        AddTemplateRequirement(
                            output,
                            warnings,
                            seen,
                            questId,
                            conditionId,
                            questName,
                            traderName,
                            mapName,
                            "Equipment",
                            FriendlyQuestCondition(conditionType),
                            targetTemplates,
                            required,
                            equippedRoots,
                            completed,
                            foundInRaidRequired: false,
                            isRaidCritical: true,
                            satisfiedNote: "Required equipment is currently equipped.",
                            missingVerb: "Equip");
                        continue;
                    }

                    if (IsPlantOrPlacementCondition(conditionType))
                    {
                        if (targetTemplates.Count == 0
                            && conditionType.Contains("Beacon", StringComparison.OrdinalIgnoreCase)
                            && GetTemplate(Ms2000MarkerTemplateId).Exists)
                        {
                            targetTemplates = [Ms2000MarkerTemplateId];
                        }

                        AddTemplateRequirement(
                            output,
                            warnings,
                            seen,
                            questId,
                            conditionId,
                            questName,
                            traderName,
                            mapName,
                            ClassifyRaidItemRequirement(targetTemplates, "Plant item"),
                            FriendlyQuestCondition(conditionType),
                            targetTemplates,
                            required,
                            carriedItems,
                            completed,
                            foundInRaidRequired,
                            isRaidCritical: true,
                            satisfiedNote: "Required raid item is currently carried.",
                            missingVerb: "Bring");
                        continue;
                    }

                    if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase)
                        || conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
                    {
                        var raidCritical = countInRaid || oneSessionOnly;
                        AddTemplateRequirement(
                            output,
                            warnings,
                            seen,
                            questId,
                            conditionId,
                            questName,
                            traderName,
                            mapName,
                            ClassifyRaidItemRequirement(targetTemplates, "Turn-in item"),
                            FriendlyQuestCondition(conditionType),
                            targetTemplates,
                            required,
                            carriedItems,
                            completed,
                            foundInRaidRequired,
                            raidCritical,
                            satisfiedNote: raidCritical
                                ? "Required quest item is currently carried."
                                : "Matching turn-in items are currently carried.",
                            missingVerb: raidCritical ? "Bring" : "Collect or retain");
                        continue;
                    }

                    if (conditionType.Equals("CounterCreator", StringComparison.OrdinalIgnoreCase))
                    {
                        AddNestedCounterRequirements(
                            output,
                            warnings,
                            seen,
                            questId,
                            conditionId,
                            questName,
                            traderName,
                            mapName,
                            condition,
                            equippedRoots,
                            carriedItems,
                            completed);
                    }
                }
            }

            AddInferredRouteKeyRequirements(
                output,
                warnings,
                seen,
                questId,
                questName,
                traderName,
                questMapName,
                quest,
                state,
                carriedItems);
        }

        return output
            .OrderByDescending(requirement => requirement.IsRaidCritical && !requirement.AcquireInRaid && !requirement.IsSatisfied)
            .ThenByDescending(requirement => requirement.AcquireInRaid)
            .ThenBy(requirement => requirement.IsSatisfied)
            .ThenBy(requirement => requirement.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.QuestName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.RequiredEquipment, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddInferredRouteKeyRequirements(
        ICollection<HermesQuestLoadoutRequirement> output,
        ICollection<HermesLoadoutWarning> warnings,
        ISet<string> seen,
        string questId,
        string questName,
        string traderName,
        string questMapName,
        JsonObject quest,
        ActiveQuestState state,
        IReadOnlyList<InventoryNode> carriedItems)
    {
        var candidates = InferRouteKeys(questId, questName, questMapName, quest, state);
        foreach (var candidate in candidates)
        {
            if (output.Any(requirement =>
                    requirement.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase)
                    && requirement.RequiredEquipment.Equals(candidate.Key.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var carried = carriedItems
                .Where(item => item.TemplateId.Equals(candidate.Key.TemplateId, StringComparison.OrdinalIgnoreCase))
                .Sum(GetStackCount);
            var curated = !candidate.Source.Equals("quest text/key match", StringComparison.OrdinalIgnoreCase);
            AddTextRequirement(
                output,
                warnings,
                seen,
                $"{questId}:inferred-route-key:{candidate.Key.TemplateId}:{candidate.MapName}",
                questName,
                traderName,
                candidate.MapName,
                candidate.AcquireInRaid
                    ? "Acquire route key in raid"
                    : curated
                        ? "Quest route key"
                        : "Inferred route key",
                candidate.AcquireInRaid
                    ? "In-raid access requirement"
                    : curated
                        ? "Curated quest access requirement"
                        : "Inferred access requirement",
                candidate.Key.Name,
                1d,
                carried,
                completed: false,
                isRaidCritical: true,
                satisfiedNote: candidate.AcquireInRaid
                    ? "Quest route key has been acquired and is currently carried."
                    : curated
                        ? "Quest route key is currently carried."
                        : "Inferred route key is currently carried.",
                missingNote: candidate.AcquireInRaid
                    ? $"Acquire 1 × {candidate.Key.Name} during the raid. {candidate.Reason}"
                    : $"Bring 1 × {candidate.Key.Name}. {candidate.Reason}",
                acquireInRaid: candidate.AcquireInRaid);
        }
    }

    private IReadOnlyList<InferredRouteKey> InferRouteKeys(
        string questId,
        string questName,
        string questMapName,
        JsonObject quest,
        ActiveQuestState state)
    {
        var output = new Dictionary<string, InferredRouteKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in questKeyKnowledgeService.FindForQuest(questId, questName))
        {
            if (string.IsNullOrWhiteSpace(entry.MapName)
                || entry.MapName.Equals("Any map", StringComparison.OrdinalIgnoreCase)
                || IsKnowledgeRouteKeyStageComplete(questId, questMapName, quest, state, entry))
            {
                continue;
            }

            TemplateInfo? key = null;
            if (!string.IsNullOrWhiteSpace(entry.KeyTemplateId))
            {
                var byTemplate = GetTemplate(entry.KeyTemplateId);
                if (byTemplate.Exists && byTemplate.IsKey)
                {
                    key = byTemplate;
                }
            }

            key ??= FindKeyTemplate(entry.KeyNames);
            if (key is null)
            {
                continue;
            }

            output.TryAdd(
                key.TemplateId,
                new InferredRouteKey(
                    key,
                    entry.MapName,
                    BuildKnowledgeRouteKeyReason(entry),
                    "TarkovForge quest-key knowledge",
                    entry.AcquireInRaid));
        }

        foreach (var rule in QuestInRaidRouteKeyRules.Where(rule =>
                     NormalizeSearchText(questName).Equals(
                         NormalizeSearchText(rule.QuestName),
                         StringComparison.Ordinal)))
        {
            if (IsInRaidRouteKeyStageComplete(questId, quest, state, rule))
            {
                continue;
            }

            var key = FindKeyTemplate(rule.KeyNameAliases);
            if (key is null)
            {
                continue;
            }

            output.TryAdd(
                key.TemplateId,
                new InferredRouteKey(
                    key,
                    rule.MapName,
                    rule.Reason,
                    "vanilla in-raid quest-route rule",
                    true));
        }

        foreach (var rule in QuestRouteKeyRules.Where(rule => rule.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase)))
        {
            if (IsRouteKeyStageComplete(quest, state, rule))
            {
                continue;
            }

            var key = GetTemplate(rule.KeyTemplateId);
            if (!key.Exists || !key.IsKey)
            {
                continue;
            }

            output.TryAdd(
                key.TemplateId,
                new InferredRouteKey(key, rule.MapName, rule.Reason, "vanilla quest-route rule", false));
        }

        var questSearchText = NormalizeSearchText(BuildQuestSearchText(questId, questName, quest));
        if (questSearchText.Length == 0)
        {
            return output.Values.ToList();
        }

        foreach (var key in GetKeyTemplates())
        {
            if (output.ContainsKey(key.TemplateId))
            {
                continue;
            }

            var keyIdMatch = !string.IsNullOrWhiteSpace(key.KeyId)
                             && questSearchText.Contains(NormalizeSearchText(key.KeyId), StringComparison.Ordinal);
            var keyNamePhrase = BuildKeyMatchPhrase(key.Name);
            var nameMatch = keyNamePhrase.Length >= 8
                            && questSearchText.Contains(keyNamePhrase, StringComparison.Ordinal);
            if (!keyIdMatch && !nameMatch)
            {
                continue;
            }

            var mapName = InferMapFromText(questSearchText) ?? questMapName;
            output.TryAdd(
                key.TemplateId,
                new InferredRouteKey(
                    key,
                    mapName,
                    "Inferred from the active quest text and the local key catalog.",
                    "quest text/key match",
                    false));
        }

        return output.Values
            .OrderBy(value => value.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsKnowledgeRouteKeyStageComplete(
        string questId,
        string questMapName,
        JsonObject quest,
        ActiveQuestState state,
        HermesQuestKeyKnowledgeEntry entry)
    {
        // Status 3 means all finish requirements are satisfied and the quest can be turned in.
        if (state.Status == 3)
        {
            return true;
        }

        var conditions = GetProperty(quest, "conditions", "Conditions");
        var mappedConditions = new List<(string Id, string Description)>();
        foreach (var group in new[] { "AvailableForFinish", "Success" })
        {
            foreach (var node in GetArray(GetProperty(conditions, group)))
            {
                if (node is not JsonObject condition)
                {
                    continue;
                }

                var conditionType = ReadString(condition, "conditionType", "ConditionType", "type", "Type") ?? string.Empty;
                var conditionId = ReadString(condition, "id", "Id") ?? string.Empty;
                var mapName = ResolveObjectiveMap(questId, questMapName, conditionId, conditionType, condition);
                if (!mapName.Equals(entry.MapName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mappedConditions.Add((
                    conditionId,
                    NormalizeSearchText(ResolveQuestObjectiveText(questId, conditionId, condition, conditionType))));
            }
        }

        if (entry.ObjectiveHints.Count > 0)
        {
            var normalizedHints = entry.ObjectiveHints
                .Select(NormalizeSearchText)
                .Where(hint => hint.Length > 0)
                .ToList();
            var hintMatches = mappedConditions
                .Where(condition => normalizedHints.Any(hint =>
                    condition.Description.Contains(hint, StringComparison.Ordinal)))
                .ToList();
            if (hintMatches.Count > 0)
            {
                return hintMatches.All(condition =>
                    !string.IsNullOrWhiteSpace(condition.Id)
                    && state.CompletedConditions.Contains(condition.Id));
            }
        }

        // A curated map association is considered complete only when all mapped quest
        // objectives on that map are complete. When the local quest data cannot map
        // an objective confidently, keep the association available to the Raid Planner
        // but never move it to another map.
        return mappedConditions.Count > 0
               && mappedConditions.All(condition =>
                   !string.IsNullOrWhiteSpace(condition.Id)
                   && state.CompletedConditions.Contains(condition.Id));
    }

    private static string BuildKnowledgeRouteKeyReason(HermesQuestKeyKnowledgeEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Opens))
        {
            parts.Add($"Opens: {entry.Opens.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(entry.Purpose))
        {
            parts.Add(entry.Purpose.Trim());
        }

        if (entry.Acquisition.Count > 0)
        {
            parts.Add($"Acquisition: {string.Join(", ", entry.Acquisition)}.");
        }

        parts.Add("Quest-key association: TarkovForge; key identity and quest progress: installed SPT database.");
        return string.Join(" ", parts);
    }

    private TemplateInfo? FindKeyTemplate(IReadOnlyList<string> aliases)
    {
        var normalizedAliases = aliases
            .Select(NormalizeSearchText)
            .Where(alias => alias.Length > 0)
            .ToList();
        return GetKeyTemplates()
            .FirstOrDefault(key =>
            {
                var normalizedName = NormalizeSearchText(key.Name);
                return normalizedAliases.Any(alias =>
                    normalizedName.Equals(alias, StringComparison.Ordinal)
                    || normalizedName.Contains(alias, StringComparison.Ordinal));
            });
    }

    private bool IsInRaidRouteKeyStageComplete(
        string questId,
        JsonObject quest,
        ActiveQuestState state,
        QuestInRaidRouteKeyRule rule)
    {
        var conditions = GetProperty(quest, "conditions", "Conditions");
        foreach (var group in new[] { "AvailableForFinish", "Success" })
        {
            foreach (var node in GetArray(GetProperty(conditions, group)))
            {
                if (node is not JsonObject condition)
                {
                    continue;
                }

                var conditionId = ReadString(condition, "id", "Id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(conditionId)
                    || !state.CompletedConditions.Contains(conditionId))
                {
                    continue;
                }

                var conditionType = ReadString(
                    condition,
                    "conditionType",
                    "ConditionType",
                    "type",
                    "Type") ?? string.Empty;
                var description = NormalizeSearchText(
                    ResolveQuestObjectiveText(questId, conditionId, condition, conditionType));
                if (rule.ObjectiveHints.Any(hint =>
                        description.Contains(NormalizeSearchText(hint), StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsRouteKeyStageComplete(JsonObject quest, ActiveQuestState state, QuestRouteKeyRule rule)
    {
        var typeMatches = new List<JsonObject>();
        var hintMatches = new List<JsonObject>();
        var conditions = GetProperty(quest, "conditions", "Conditions");
        foreach (var group in new[] { "AvailableForFinish", "Success" })
        {
            foreach (var node in GetArray(GetProperty(conditions, group)))
            {
                if (node is not JsonObject condition)
                {
                    continue;
                }

                var type = ReadString(condition, "conditionType", "ConditionType", "type", "Type") ?? string.Empty;
                if (!type.Contains(rule.RelatedConditionType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                typeMatches.Add(condition);
                var conditionId = ReadString(condition, "id", "Id") ?? string.Empty;
                var description = NormalizeSearchText(
                    ResolveQuestObjectiveText(rule.QuestId, conditionId, condition, type));
                if (rule.TargetHints.Count == 0
                    || rule.TargetHints.Any(hint => description.Contains(NormalizeSearchText(hint), StringComparison.Ordinal)))
                {
                    hintMatches.Add(condition);
                }
            }
        }

        IReadOnlyList<JsonObject> related = hintMatches.Count > 0
            ? hintMatches
            : typeMatches.Count == 1
                ? typeMatches
                : [];
        return related.Count > 0
               && related.All(condition =>
               {
                   var conditionId = ReadString(condition, "id", "Id");
                   return !string.IsNullOrWhiteSpace(conditionId)
                          && state.CompletedConditions.Contains(conditionId);
               });
    }

    private IReadOnlyList<TemplateInfo> GetKeyTemplates()
    {
        lock (_keyTemplateSync)
        {
            if (_keyTemplates is not null)
            {
                return _keyTemplates;
            }
        }

        EnsureLocaleStrings();
        var candidateIds = new HashSet<string>(
            QuestRouteKeyRules.Select(rule => rule.KeyTemplateId),
            StringComparer.OrdinalIgnoreCase);
        lock (_localeSync)
        {
            if (_localeStrings is not null)
            {
                foreach (var pair in _localeStrings)
                {
                    if (!pair.Key.EndsWith(" Name", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var lowerName = pair.Value.ToLowerInvariant();
                    if (!lowerName.Contains("key", StringComparison.Ordinal)
                        && !lowerName.Contains("keycard", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var templateId = pair.Key[..^5].Trim();
                    if (MongoId.IsValidMongoId(templateId))
                    {
                        candidateIds.Add(templateId);
                    }
                }
            }
        }

        lock (_templateSync)
        {
            foreach (var cached in _templateCache.Values.Where(template => template.Exists && template.IsKey))
            {
                candidateIds.Add(cached.TemplateId);
            }
        }

        var keys = candidateIds
            .Select(GetTemplate)
            .Where(template => template.Exists && template.IsKey)
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_keyTemplateSync)
        {
            _keyTemplates ??= keys;
            return _keyTemplates;
        }
    }

    private string BuildQuestSearchText(string questId, string questName, JsonObject quest)
    {
        var conditions = GetProperty(quest, "conditions", "Conditions");
        var parts = new List<string>
        {
            questName,
            conditions?.ToJsonString() ?? string.Empty
        };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { questId };
        CollectConditionIds(conditions, ids);
        EnsureLocaleStrings();
        lock (_localeSync)
        {
            if (_localeStrings is not null)
            {
                foreach (var pair in _localeStrings)
                {
                    if (ids.Any(id => pair.Key.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        parts.Add(pair.Value);
                    }
                }
            }
        }

        return string.Join(" ", parts);
    }

    private static void CollectConditionIds(JsonNode? node, ISet<string> output)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase)
                    && pair.Value is JsonValue value
                    && value.TryGetValue<string>(out var id)
                    && !string.IsNullOrWhiteSpace(id))
                {
                    output.Add(id);
                }

                CollectConditionIds(pair.Value, output);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectConditionIds(child, output);
            }
        }
    }

    private string ResolveObjectiveMap(
        string questId,
        string questMapName,
        string conditionId,
        string conditionType,
        JsonObject condition)
    {
        var directLocationNode = GetProperty(condition, "location", "Location", "map", "Map");
        var directLocation = directLocationNode is JsonValue
            ? ReadString(directLocationNode)
            : null;
        if (!string.IsNullOrWhiteSpace(directLocation)
            && !MongoId.IsValidMongoId(directLocation))
        {
            var directMap = FriendlyMapName(directLocation);
            if (!directMap.Equals("Any map", StringComparison.OrdinalIgnoreCase))
            {
                return directMap;
            }
        }

        // Delivery From the Past is stored as an any-map quest even though its
        // pickup and placement conditions belong to different raids.
        if (questId.Equals("59674eb386f774539f14813a", StringComparison.OrdinalIgnoreCase))
        {
            if (IsPlantOrPlacementCondition(conditionType))
            {
                return "Factory";
            }

            if (conditionType.Contains("FindItem", StringComparison.OrdinalIgnoreCase)
                || conditionType.Contains("HandoverItem", StringComparison.OrdinalIgnoreCase)
                || conditionType.Contains("VisitPlace", StringComparison.OrdinalIgnoreCase))
            {
                return "Customs";
            }
        }

        var conditionText = new List<string>
        {
            condition.ToJsonString(),
            DescribeQuestObjective(condition, conditionType),
            GetLocalizedConditionText(conditionId)
        };
        return InferMapFromText(string.Join(" ", conditionText)) ?? questMapName;
    }

    private string GetLocalizedConditionText(string conditionId)
    {
        if (string.IsNullOrWhiteSpace(conditionId))
        {
            return string.Empty;
        }

        EnsureLocaleStrings();
        lock (_localeSync)
        {
            if (_localeStrings is null)
            {
                return string.Empty;
            }

            return string.Join(
                " ",
                _localeStrings
                    .Where(pair => pair.Key.Contains(conditionId, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value));
        }
    }

    private static string? InferMapFromText(string text)
    {
        var normalized = NormalizeSearchText(text);
        var matches = new List<string>();
        AddMapMatch(matches, normalized, "Customs", "customs", "big red", "tarcone", "dorms");
        AddMapMatch(matches, normalized, "Factory", "factory", "gate 3", "break room");
        AddMapMatch(matches, normalized, "Woods", "woods", "sawmill");
        AddMapMatch(matches, normalized, "Shoreline", "shoreline", "health resort", "resort");
        AddMapMatch(matches, normalized, "Interchange", "interchange", "ultra mall", "idea", "oli", "goshan");
        AddMapMatch(matches, normalized, "Reserve", "reserve", "rezervbase", "white pawn", "black pawn");
        AddMapMatch(matches, normalized, "The Lab", "laboratory", "the lab", "terragroup labs");
        AddMapMatch(matches, normalized, "Lighthouse", "lighthouse", "water treatment plant");
        AddMapMatch(matches, normalized, "Streets of Tarkov", "streets of tarkov", "tarkovstreets");
        AddMapMatch(matches, normalized, "Ground Zero", "ground zero", "sandbox");
        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? matches[0]
            : null;
    }

    private static void AddMapMatch(ICollection<string> matches, string text, string mapName, params string[] aliases)
    {
        if (aliases.Any(alias => text.Contains(NormalizeSearchText(alias), StringComparison.Ordinal)))
        {
            matches.Add(mapName);
        }
    }

    private static string BuildKeyMatchPhrase(string name)
    {
        var normalized = NormalizeSearchText(name);
        var words = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => !word.Equals("key", StringComparison.Ordinal)
                           && !word.Equals("keycard", StringComparison.Ordinal))
            .ToList();
        return string.Join(" ", words);
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var output = new char[value.Length];
        var length = 0;
        var previousSpace = true;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                output[length++] = character;
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                output[length++] = ' ';
                previousSpace = true;
            }
        }

        return new string(output, 0, length).Trim();
    }

    private void AddNestedCounterRequirements(
        ICollection<HermesQuestLoadoutRequirement> output,
        ICollection<HermesLoadoutWarning> warnings,
        ISet<string> seen,
        string questId,
        string conditionId,
        string questName,
        string traderName,
        string mapName,
        JsonObject condition,
        IReadOnlyList<InventoryNode> equippedRoots,
        IReadOnlyList<InventoryNode> carriedItems,
        bool completed)
    {
        var counter = GetProperty(condition, "counter", "Counter");
        foreach (var nestedNode in GetArray(GetProperty(counter, "conditions", "Conditions")))
        {
            if (nestedNode is not JsonObject nested)
            {
                continue;
            }

            var nestedType = ReadString(nested, "conditionType", "ConditionType", "type", "Type") ?? "Counter condition";
            var nestedId = ReadString(nested, "id", "Id") ?? nestedType;
            var equipmentTargets = GetExistingTargetTemplates(GetProperty(nested, "equipmentInclusive", "EquipmentInclusive"));
            if (equipmentTargets.Count > 0)
            {
                AddTemplateRequirement(
                    output,
                    warnings,
                    seen,
                    questId,
                    $"{conditionId}:{nestedId}:equipment",
                    questName,
                    traderName,
                    mapName,
                    "Equipment",
                    "Required quest equipment",
                    equipmentTargets,
                    1d,
                    equippedRoots,
                    completed,
                    foundInRaidRequired: false,
                    isRaidCritical: true,
                    satisfiedNote: "Required quest equipment is currently equipped.",
                    missingVerb: "Equip");
            }

            var weaponTargets = GetExistingTargetTemplates(GetProperty(nested, "weapon", "Weapon"));
            if (weaponTargets.Count > 0)
            {
                AddTemplateRequirement(
                    output,
                    warnings,
                    seen,
                    questId,
                    $"{conditionId}:{nestedId}:weapon",
                    questName,
                    traderName,
                    mapName,
                    "Weapon requirement",
                    "Required weapon",
                    weaponTargets,
                    1d,
                    equippedRoots,
                    completed,
                    foundInRaidRequired: false,
                    isRaidCritical: true,
                    satisfiedNote: "A required weapon is currently equipped.",
                    missingVerb: "Equip");
            }

            var carriedTargets = GetExistingTargetTemplates(GetProperty(nested, "target", "Target"));
            if (carriedTargets.Count > 0 && IsPlantOrPlacementCondition(nestedType))
            {
                AddTemplateRequirement(
                    output,
                    warnings,
                    seen,
                    questId,
                    $"{conditionId}:{nestedId}:carried",
                    questName,
                    traderName,
                    mapName,
                    ClassifyRaidItemRequirement(carriedTargets, "Raid item"),
                    FriendlyQuestCondition(nestedType),
                    carriedTargets,
                    1d,
                    carriedItems,
                    completed,
                    foundInRaidRequired: false,
                    isRaidCritical: true,
                    satisfiedNote: "Required raid item is currently carried.",
                    missingVerb: "Bring");
            }

            var calibers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectStrings(GetProperty(nested, "weaponCaliber", "WeaponCaliber"), calibers);
            if (calibers.Count > 0)
            {
                var equippedCount = equippedRoots
                    .Where(item => GetTemplate(item.TemplateId).IsWeapon)
                    .Count(item => calibers.Any(caliber => CaliberMatches(GetTemplate(item.TemplateId).Caliber, caliber)));
                var requiredText = string.Join(" or ", calibers.Select(FriendlyCaliber).Take(4));
                AddTextRequirement(
                    output,
                    warnings,
                    seen,
                    $"{questId}:{conditionId}:{nestedId}:caliber",
                    questName,
                    traderName,
                    mapName,
                    "Weapon requirement",
                    "Required weapon caliber",
                    requiredText,
                    1d,
                    equippedCount,
                    completed,
                    isRaidCritical: true,
                    satisfiedNote: "A weapon in the required caliber is currently equipped.",
                    missingNote: $"Equip a weapon chambered in {requiredText}.");
            }
        }
    }

    private void AddTemplateRequirement(
        ICollection<HermesQuestLoadoutRequirement> output,
        ICollection<HermesLoadoutWarning> warnings,
        ISet<string> seen,
        string questId,
        string conditionId,
        string questName,
        string traderName,
        string mapName,
        string requirementKind,
        string conditionType,
        IReadOnlyList<string> targetTemplates,
        double required,
        IReadOnlyList<InventoryNode> candidateItems,
        bool completed,
        bool foundInRaidRequired,
        bool isRaidCritical,
        string satisfiedNote,
        string missingVerb)
    {
        if (targetTemplates.Count == 0)
        {
            return;
        }

        var key = $"{questId}:{conditionId}:{requirementKind}:{string.Join(',', targetTemplates.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";
        if (!seen.Add(key))
        {
            return;
        }

        var matchingItems = candidateItems
            .Where(item => targetTemplates.Contains(item.TemplateId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var carried = matchingItems.Sum(GetStackCount);
        var carriedFir = matchingItems.Where(IsFoundInRaid).Sum(GetStackCount);
        var effectiveCount = foundInRaidRequired ? carriedFir : carried;
        var satisfied = completed || effectiveCount >= required;
        var itemNames = targetTemplates
            .Select(target => GetTemplate(target).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiredText = itemNames.Count == 1
            ? itemNames[0]
            : string.Join(" or ", itemNames.Take(4));
        var note = completed
            ? "Quest condition is already complete."
            : satisfied
                ? satisfiedNote
                : isRaidCritical
                    ? $"{missingVerb} {FormatNumber(required)} × {requiredText}{(foundInRaidRequired ? " found in raid" : string.Empty)}."
                    : $"Not required in the current raid loadout. Retain {FormatNumber(required)} × {requiredText}{(foundInRaidRequired ? " found in raid" : string.Empty)} for quest progress.";

        output.Add(new HermesQuestLoadoutRequirement(
            questName,
            traderName,
            mapName,
            requirementKind,
            conditionType,
            requiredText,
            required,
            carried,
            carriedFir,
            foundInRaidRequired,
            completed,
            isRaidCritical,
            false,
            satisfied,
            note));

        if (isRaidCritical && !satisfied)
        {
            warnings.Add(new HermesLoadoutWarning(
                "Warning",
                "Quest gear",
                $"{questName} ({mapName}): {note}"));
        }
    }

    private static void AddTextRequirement(
        ICollection<HermesQuestLoadoutRequirement> output,
        ICollection<HermesLoadoutWarning> warnings,
        ISet<string> seen,
        string key,
        string questName,
        string traderName,
        string mapName,
        string requirementKind,
        string conditionType,
        string requiredText,
        double required,
        double carried,
        bool completed,
        bool isRaidCritical,
        string satisfiedNote,
        string missingNote,
        bool acquireInRaid = false)
    {
        if (!seen.Add(key))
        {
            return;
        }

        var satisfied = completed || carried >= required;
        var note = completed
            ? "Quest condition is already complete."
            : satisfied
                ? satisfiedNote
                : missingNote;
        output.Add(new HermesQuestLoadoutRequirement(
            questName,
            traderName,
            mapName,
            requirementKind,
            conditionType,
            requiredText,
            required,
            carried,
            0d,
            false,
            completed,
            isRaidCritical,
            acquireInRaid,
            satisfied,
            note));

        if (isRaidCritical && !acquireInRaid && !satisfied)
        {
            warnings.Add(new HermesLoadoutWarning(
                "Warning",
                "Quest gear",
                $"{questName} ({mapName}): {note}"));
        }
    }

    private IReadOnlyList<string> GetExistingTargetTemplates(JsonNode? node)
    {
        return ReadMongoIds(node)
            .Where(target => GetTemplate(target).Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ClassifyRaidItemRequirement(IReadOnlyList<string> templateIds, string fallback)
    {
        var templates = templateIds.Select(GetTemplate).ToList();
        if (templates.Any(template => template.IsKey))
        {
            return "Quest key";
        }

        if (templates.Any(template => ContainsAny(template.Name.ToLowerInvariant(), "marker", "camera", "jammer", "beacon")))
        {
            return "Quest tool";
        }

        return fallback;
    }
}
