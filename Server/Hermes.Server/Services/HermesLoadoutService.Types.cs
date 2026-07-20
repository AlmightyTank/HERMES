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
    private sealed record InventoryNode(
        string Id,
        string TemplateId,
        string? ParentId,
        string? SlotId,
        JsonObject? Upd);

    private sealed record TemplateInfo(
        bool Exists,
        string TemplateId,
        string Name,
        JsonObject? Properties,
        string SerializedProperties,
        string Caliber,
        bool IsWeapon,
        bool IsMagazine,
        bool IsAmmo,
        bool IsArmor,
        bool IsKey,
        string KeyId,
        int ArmorClass,
        int MagazineCapacity,
        bool IsInternalMagazineWeapon,
        bool IsMedical,
        double MaximumMedicalResource,
        bool TreatsLightBleed,
        bool TreatsHeavyBleed,
        bool TreatsFracture,
        bool TreatsPain,
        bool IsSurgeryKit,
        double MaximumConsumableResource,
        bool ProvidesHydration,
        bool ProvidesEnergy,
        IReadOnlyList<ArmorSlotDefinition> ArmorSlots)
    {
        public static TemplateInfo Missing(string templateId) => new(
            Exists: false,
            TemplateId: templateId,
            Name: "Unknown item",
            Properties: null,
            SerializedProperties: string.Empty,
            Caliber: string.Empty,
            IsWeapon: false,
            IsMagazine: false,
            IsAmmo: false,
            IsArmor: false,
            IsKey: false,
            KeyId: string.Empty,
            ArmorClass: 0,
            MagazineCapacity: 0,
            IsInternalMagazineWeapon: false,
            IsMedical: false,
            MaximumMedicalResource: 0d,
            TreatsLightBleed: false,
            TreatsHeavyBleed: false,
            TreatsFracture: false,
            TreatsPain: false,
            IsSurgeryKit: false,
            MaximumConsumableResource: 0d,
            ProvidesHydration: false,
            ProvidesEnergy: false,
            ArmorSlots: []);
    }

    private sealed record ArmorSlotDefinition(string Name, bool Required);
    private sealed record PlateSlotState(string Name, bool Required, bool Installed);
    private sealed record ConditionInfo(int Percent, string Description, bool HasCondition);
    private sealed record RaidQuestDraft(string MapName, HermesRaidPlanQuest Quest);
    private sealed record ActiveQuestState(int Status, IReadOnlySet<string> CompletedConditions);
    private sealed record QuestRouteKeyRule(
        string QuestId,
        string MapName,
        string KeyTemplateId,
        string RelatedConditionType,
        IReadOnlyList<string> TargetHints,
        string Reason);
    private sealed record QuestInRaidRouteKeyRule(
        string QuestName,
        string MapName,
        IReadOnlyList<string> KeyNameAliases,
        IReadOnlyList<string> ObjectiveHints,
        string Reason);
    private sealed record InferredRouteKey(
        TemplateInfo Key,
        string MapName,
        string Reason,
        string Source,
        bool AcquireInRaid);
}
