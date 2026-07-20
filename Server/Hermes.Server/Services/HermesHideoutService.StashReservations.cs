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

public sealed partial class HermesHideoutService
{
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
}
