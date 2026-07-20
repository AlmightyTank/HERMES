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
    private HermesHideoutSummaryResponse BuildSummaryCore(MongoId sessionId)
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
            .Where(area => IsAreaAvailable(area, snapshot))
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

        var requiredItems = requirements
            .Where(requirement => requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => new HermesHideoutItemRequirementSummary(
                ItemTemplateId: requirement.ItemTemplateId ?? string.Empty,
                Name: requirement.Name,
                Required: requirement.Required,
                Owned: requirement.Owned,
                Missing: requirement.Missing,
                IsMet: requirement.IsMet,
                FoundInRaidRequired: requirement.FoundInRaidRequired))
            .OrderBy(requirement => requirement.IsMet)
            .ThenByDescending(requirement => requirement.Missing)
            .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = new HermesHideoutAreaSummary(
            AreaKey: area.Key,
            Name: area.Name,
            CurrentLevel: currentLevel,
            MaximumLevel: maximumLevel,
            TargetLevel: targetStage?.Level,
            Status: status,
            IsActive: profileArea.Active,
            IsConstructing: profileArea.Constructing,
            SecondsUntilComplete: secondsUntilComplete,
            MissingItemTypes: requiredItems.Count(requirement => !requirement.IsMet),
            EstimatedMissingHandbookCost: requirements.Sum(requirement =>
                requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(Math.Round((requirement.UnitPrice ?? 0L) * requirement.Missing))
                    : 0L),
            RequiredItems: requiredItems);

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
                requirement.TemplateId,
                null,
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
            var areaKey = requirement.AreaType.HasValue
                          && _areasByType!.TryGetValue(requirement.AreaType.Value, out var requiredArea)
                ? requiredArea.Key
                : null;

            return new HermesHideoutRequirement(
                "Area",
                areaName,
                null,
                areaKey,
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
                null,
                null,
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
                null,
                null,
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
                null,
                null,
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
            null,
            null,
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

    private bool IsAreaAvailable(AreaDefinition area, ProfileSnapshot snapshot)
    {
        if (area is null
            || snapshot is null
            || snapshot.Areas is null
            || !area.Enabled
            || !snapshot.Areas.ContainsKey(area.Type))
        {
            return false;
        }

        if (area.Type == (int)HideoutAreas.ChristmasIllumination)
        {
            return IsChristmasHideoutActive();
        }

        return true;
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
                craft?.OutputTemplateId,
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

    private static double CalculateEffectiveProductionProgress(
        double storedProgress,
        double productionTime,
        bool inProgress,
        long? startTimestamp,
        double skipTime)
    {
        if (!inProgress || productionTime <= 0d || startTimestamp is not > 0L)
        {
            return storedProgress;
        }

        var startSeconds = NormalizeUnixSeconds(startTimestamp.Value);
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startSeconds;
        if (elapsed <= 0d)
        {
            return storedProgress;
        }

        return Math.Max(storedProgress, Math.Max(0d, elapsed + Math.Max(0d, skipTime)));
    }

    private static long NormalizeUnixSeconds(long timestamp)
        => timestamp > 10_000_000_000L ? timestamp / 1000L : timestamp;

    private static HermesHideoutResourceSummary EmptyResources()
    {
        return new HermesHideoutResourceSummary(false, 0, 0d, null, null, null, null, 0, 0);
    }

    private bool IsChristmasHideoutActive()
    {
        try
        {
            if (seasonalEventService.ChristmasEventEnabled())
            {
                return true;
            }
        }
        catch
        {
            // Fall through to globals. Some modded event configurations omit collections
            // that the vanilla seasonal service expects to be initialized.
        }

        try
        {
            // Also honor server mods that enable the Christmas hideout directly through globals.
            // EventType may be absent in stripped or custom global configurations.
            var globals = databaseService.GetGlobals();
            var eventTypes = globals?.Configuration?.EventType;
            return eventTypes is not null && eventTypes.Any(eventType =>
                string.Equals(
                    Convert.ToString(eventType, CultureInfo.InvariantCulture),
                    "Christmas",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
