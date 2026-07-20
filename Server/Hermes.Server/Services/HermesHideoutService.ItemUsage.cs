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

    private IReadOnlyList<HermesQuestKeyUse> BuildQuestKeyUses(
        string templateId,
        string itemName,
        ProfileSnapshot snapshot)
    {
        var entries = questKeyKnowledgeService.FindForKey(templateId, itemName);
        if (entries.Count == 0)
        {
            return [];
        }

        var output = new List<HermesQuestKeyUse>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var questName in entry.QuestNames.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var fingerprint = $"{questName}|{entry.MapName}|{entry.Opens}";
                if (!seen.Add(fingerprint))
                {
                    continue;
                }

                var normalizedQuestName = NormalizeKnowledgeText(questName);
                var questId = _questIdsByNormalizedName is not null
                              && _questIdsByNormalizedName.TryGetValue(normalizedQuestName, out var resolvedQuestId)
                    ? resolvedQuestId
                    : null;
                var statusCode = 0;
                if (!string.IsNullOrWhiteSpace(questId)
                    && snapshot.QuestStates.TryGetValue(questId, out var questState))
                {
                    statusCode = questState.StatusCode;
                }

                var acquisition = entry.Acquisition.Count == 0
                    ? string.Empty
                    : string.Join(" • ", entry.Acquisition.Where(value => !string.IsNullOrWhiteSpace(value)).Take(3));
                output.Add(new HermesQuestKeyUse(
                    questName,
                    entry.MapName,
                    entry.Opens,
                    entry.Purpose,
                    acquisition,
                    entry.AcquireInRaid,
                    QuestStatusDisplay(statusCode),
                    statusCode is 2 or 3,
                    statusCode == 4));
            }
        }

        return output
            .OrderByDescending(use => use.IsActive)
            .ThenBy(use => QuestStatusRank(use.QuestStatus))
            .ThenBy(use => use.QuestName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(use => use.MapName, StringComparer.OrdinalIgnoreCase)
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
}
