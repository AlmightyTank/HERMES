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

    private sealed record MaterializedSummaryEntry<T>(
        string Key,
        T Response);

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
        string ProductionKey,
        string RecipeId,
        double Progress,
        double ProductionTime,
        bool InProgress,
        bool IsComplete,
        bool IsContinuous,
        string? OutputName,
        int OutputQuantity,
        string? OutputTemplateId,
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

    private sealed record TraderSaleEstimate(string? TraderName, long UnitValue);

    private sealed record FleaSaleEstimate(
        bool FleaUnlocked,
        bool CanSellOnFlea,
        long UnitNetValue);

    private sealed class FleaSaleCache
    {
        public bool? FleaUnlocked { get; set; }
        public Dictionary<string, FleaSaleEstimate> ByItem { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ItemValuation(
        long? HandbookUnitValue,
        IReadOnlyList<AcquisitionQuote> AcquisitionQuotes,
        string? FallbackMarketSource,
        long? FallbackMarketUnitValue,
        string? EconomicSource,
        long? EconomicUnitValue);
}
