using System.Globalization;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesLoadoutService(
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    HermesCatalogService catalogService,
    JsonUtil jsonUtil)
{
    private const string Ms2000MarkerTemplateId = "5991b51486f77447b112d44f";

    private static readonly string[] EquipmentSlotOrder =
    [
        "FirstPrimaryWeapon",
        "SecondPrimaryWeapon",
        "Holster",
        "Scabbard",
        "ArmorVest",
        "TacticalVest",
        "Headwear",
        "Earpiece",
        "FaceCover",
        "Eyewear",
        "Backpack",
        "Pockets",
        "SecuredContainer",
        "Armband"
    ];

    private static readonly HashSet<string> WeaponSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "FirstPrimaryWeapon",
        "SecondPrimaryWeapon",
        "Holster"
    };

    private static readonly HashSet<string> ArmorSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArmorVest",
        "TacticalVest",
        "Headwear"
    };

    // These are carried inventory containers, not wearable/equipped gear.
    // Their contents still remain in equippedItems for quest, medical,
    // ammunition, food, and other carried-item readiness checks.
    private static readonly HashSet<string> CarriedStorageSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pockets",
        "SecuredContainer"
    };

    private readonly object _templateSync = new();
    private readonly Dictionary<string, TemplateInfo> _templateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _localeSync = new();
    private Dictionary<string, string>? _localeStrings;

    public HermesLoadoutSummaryResponse GetSummary(MongoId sessionId)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return NotFound("HERMES could not read the active PMC profile.");
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(jsonUtil.Serialize(profile) ?? "{}") as JsonObject;
        }
        catch
        {
            return NotFound("HERMES could not parse the active PMC profile.");
        }

        if (root is null)
        {
            return NotFound("HERMES could not parse the active PMC profile.");
        }

        var inventoryNode = GetProperty(root, "Inventory", "inventory");
        var equipmentId = ReadString(inventoryNode, "Equipment", "equipment");
        var items = ParseInventoryItems(inventoryNode);
        if (string.IsNullOrWhiteSpace(equipmentId) || items.Count == 0)
        {
            return NotFound("HERMES could not locate the active PMC equipment inventory.");
        }

        var children = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentId))
            .GroupBy(item => item.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var equippedRoots = items
            .Where(item => string.Equals(item.ParentId, equipmentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => SlotRank(item.SlotId))
            .ThenBy(item => item.SlotId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var equippedItems = CollectDescendants(equippedRoots, children);

        var warnings = new List<HermesLoadoutWarning>();
        var vitals = BuildVitals(root, warnings);
        var slots = BuildSlotSummaries(equippedRoots, children);
        var weapons = BuildWeaponReadiness(equippedRoots, equippedItems, children, warnings);
        var armor = BuildArmorReadiness(equippedRoots, children, warnings);
        var medical = BuildMedicalReadiness(equippedItems, warnings);
        var questRequirements = BuildQuestRequirements(root, equippedRoots, equippedItems, warnings);
        var raidPlans = BuildRaidPlans(root, questRequirements);

        if (weapons.Count == 0)
        {
            warnings.Add(new HermesLoadoutWarning(
                "Critical",
                "Weapons",
                "No firearm is equipped in a primary-weapon or holster slot."));
        }

        var criticalCount = warnings.Count(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var warningCount = warnings.Count(warning => warning.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        var score = CalculateReadinessScore(warnings);
        var readiness = criticalCount >= 2 || score < 60
            ? "NOT READY"
            : criticalCount > 0 || score < 85
                ? "CAUTION"
                : "READY";

        return new HermesLoadoutSummaryResponse(
            true,
            null,
            readiness,
            score,
            warningCount,
            criticalCount,
            vitals,
            slots,
            weapons,
            armor,
            medical,
            questRequirements,
            raidPlans,
            warnings
                .OrderBy(warning => SeverityRank(warning.Severity))
                .ThenBy(warning => warning.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(warning => warning.Message, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private HermesVitalsSummary BuildVitals(JsonObject root, ICollection<HermesLoadoutWarning> warnings)
    {
        var health = GetProperty(root, "Health", "health");
        var hydration = ReadCurrentMaximum(GetProperty(health, "Hydration", "hydration"));
        var energy = ReadCurrentMaximum(GetProperty(health, "Energy", "energy"));
        var bodyParts = GetProperty(health, "BodyParts", "bodyParts");
        double currentHealth = 0d;
        double maximumHealth = 0d;
        if (bodyParts is JsonObject bodyPartsObject)
        {
            foreach (var pair in bodyPartsObject)
            {
                var partHealth = GetProperty(pair.Value, "Health", "health") ?? pair.Value;
                var value = ReadCurrentMaximum(partHealth);
                currentHealth += value.Current;
                maximumHealth += value.Maximum;
            }
        }

        var healthPercent = ToPercent(currentHealth, maximumHealth);
        var hydrationPercent = ToPercent(hydration.Current, hydration.Maximum);
        var energyPercent = ToPercent(energy.Current, energy.Maximum);

        if (maximumHealth > 0d && healthPercent < 90)
        {
            warnings.Add(new HermesLoadoutWarning(
                healthPercent < 60 ? "Critical" : "Warning",
                "Vitals",
                $"PMC health is {healthPercent}% ({FormatNumber(currentHealth)}/{FormatNumber(maximumHealth)})."));
        }

        if (hydration.Maximum > 0d && hydrationPercent < 50)
        {
            warnings.Add(new HermesLoadoutWarning(
                hydrationPercent < 30 ? "Critical" : "Warning",
                "Vitals",
                $"Hydration is low at {hydrationPercent}%."));
        }

        if (energy.Maximum > 0d && energyPercent < 50)
        {
            warnings.Add(new HermesLoadoutWarning(
                energyPercent < 30 ? "Critical" : "Warning",
                "Vitals",
                $"Energy is low at {energyPercent}%."));
        }

        return new HermesVitalsSummary(
            currentHealth,
            maximumHealth,
            healthPercent,
            hydration.Current,
            hydration.Maximum,
            hydrationPercent,
            energy.Current,
            energy.Maximum,
            energyPercent);
    }

    private IReadOnlyList<HermesLoadoutSlotSummary> BuildSlotSummaries(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        var output = new List<HermesLoadoutSlotSummary>();
        foreach (var root in roots.Where(root =>
                     !CarriedStorageSlots.Contains(root.SlotId ?? string.Empty)))
        {
            var tree = CollectTree(root, children);
            var template = GetTemplate(root.TemplateId);
            var condition = GetCondition(root, template);
            output.Add(new HermesLoadoutSlotSummary(
                FriendlySlotName(root.SlotId),
                template.Name,
                condition.Percent,
                condition.Description,
                Math.Max(0, tree.Count - 1),
                DetermineSlotStatus(root.SlotId, condition.Percent)));
        }

        return output;
    }

    private IReadOnlyList<HermesWeaponReadiness> BuildWeaponReadiness(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyList<InventoryNode> equippedItems,
        IReadOnlyDictionary<string, List<InventoryNode>> children,
        ICollection<HermesLoadoutWarning> warnings)
    {
        var weaponRoots = roots
            .Where(item => WeaponSlots.Contains(item.SlotId ?? string.Empty))
            .Where(item => GetTemplate(item.TemplateId).IsWeapon)
            .ToList();
        var output = new List<HermesWeaponReadiness>();

        var allWeaponTreeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var weapon in weaponRoots)
        {
            foreach (var node in CollectTree(weapon, children))
            {
                allWeaponTreeIds.Add(node.Id);
            }
        }

        var spareMagazines = equippedItems
            .Where(item => !allWeaponTreeIds.Contains(item.Id))
            .Where(item => GetTemplate(item.TemplateId).IsMagazine)
            .ToList();
        var looseAmmo = equippedItems
            .Where(item => !allWeaponTreeIds.Contains(item.Id))
            .Where(item => GetTemplate(item.TemplateId).IsAmmo)
            .ToList();

        foreach (var weapon in weaponRoots)
        {
            var tree = CollectTree(weapon, children);
            var template = GetTemplate(weapon.TemplateId);
            var condition = GetCondition(weapon, template);
            var allowedMagazineTemplates = CollectAllowedMagazineTemplates(tree);
            var attachedMagazine = tree
                .Skip(1)
                .FirstOrDefault(item => IsMagazineSlot(item.SlotId) && GetTemplate(item.TemplateId).IsMagazine);
            var loadedAmmo = tree
                .Where(item => IsLoadedAmmoSlot(item.SlotId) && GetTemplate(item.TemplateId).IsAmmo)
                .ToList();
            var loadedRounds = loadedAmmo.Sum(GetStackCount);
            var ammoTemplates = loadedAmmo
                .Select(item => item.TemplateId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var ammoCalibers = loadedAmmo
                .Select(item => GetTemplate(item.TemplateId).Caliber)
                .Where(caliber => !string.IsNullOrWhiteSpace(caliber))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var mixedAmmo = ammoTemplates.Count > 1;
            var weaponCaliber = template.Caliber;
            if (string.IsNullOrWhiteSpace(weaponCaliber) && ammoCalibers.Count == 1)
            {
                weaponCaliber = ammoCalibers[0];
            }

            var compatibleSpareMags = spareMagazines
                .Where(magazine => IsMagazineCompatible(
                    magazine,
                    weaponCaliber,
                    allowedMagazineTemplates,
                    children))
                .ToList();
            var spareRounds = compatibleSpareMags.Sum(magazine =>
                CollectTree(magazine, children)
                    .Where(item => IsLoadedAmmoSlot(item.SlotId) && GetTemplate(item.TemplateId).IsAmmo)
                    .Sum(GetStackCount));
            var looseRounds = looseAmmo
                .Where(ammo => CaliberMatches(weaponCaliber, GetTemplate(ammo.TemplateId).Caliber))
                .Sum(GetStackCount);
            var magazineCapacity = attachedMagazine is null
                ? 0
                : GetTemplate(attachedMagazine.TemplateId).MagazineCapacity;
            var magazineName = attachedMagazine is null
                ? null
                : GetTemplate(attachedMagazine.TemplateId).Name;
            var localWarnings = new List<string>();

            if (condition.Percent < 70)
            {
                localWarnings.Add($"Low durability: {condition.Percent}%.");
                warnings.Add(new HermesLoadoutWarning(
                    condition.Percent < 50 ? "Critical" : "Warning",
                    "Weapons",
                    $"{template.Name} durability is {condition.Percent}%."));
            }

            if (attachedMagazine is null && !template.IsInternalMagazineWeapon)
            {
                localWarnings.Add("No magazine installed.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Weapons",
                    $"{template.Name} has no magazine installed."));
            }

            if (loadedRounds <= 0d)
            {
                localWarnings.Add("Weapon contains no loaded ammunition.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Ammunition",
                    $"{template.Name} contains no loaded ammunition."));
            }

            if (mixedAmmo)
            {
                localWarnings.Add("Mixed ammunition is loaded.");
                warnings.Add(new HermesLoadoutWarning(
                    "Warning",
                    "Ammunition",
                    $"{template.Name} has mixed ammunition loaded."));
            }

            if (!string.IsNullOrWhiteSpace(weaponCaliber)
                && ammoCalibers.Any(caliber => !CaliberMatches(weaponCaliber, caliber)))
            {
                localWarnings.Add("Loaded ammunition caliber does not match the weapon.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Ammunition",
                    $"{template.Name} has ammunition that does not match {FriendlyCaliber(weaponCaliber)}."));
            }

            if (compatibleSpareMags.Count == 0 && looseRounds <= 0d)
            {
                localWarnings.Add("No compatible spare magazine or loose ammunition is carried.");
                warnings.Add(new HermesLoadoutWarning(
                    "Warning",
                    "Ammunition",
                    $"No compatible spare magazine or loose ammunition was found for {template.Name}."));
            }

            var status = localWarnings.Count == 0
                ? "Ready"
                : localWarnings.Any(text => text.Contains("no loaded", StringComparison.OrdinalIgnoreCase)
                                            || text.Contains("does not match", StringComparison.OrdinalIgnoreCase)
                                            || text.Contains("No magazine", StringComparison.OrdinalIgnoreCase))
                    ? "Not ready"
                    : "Review";

            output.Add(new HermesWeaponReadiness(
                FriendlySlotName(weapon.SlotId),
                template.Name,
                condition.Percent,
                condition.Description,
                FriendlyCaliber(weaponCaliber),
                magazineName,
                magazineCapacity,
                loadedRounds,
                compatibleSpareMags.Count,
                spareRounds,
                looseRounds,
                ammoTemplates.Count == 1 ? GetTemplate(ammoTemplates[0]).Name : mixedAmmo ? "Mixed ammunition" : null,
                mixedAmmo,
                status,
                localWarnings));
        }

        return output;
    }

    private IReadOnlyList<HermesArmorReadiness> BuildArmorReadiness(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyDictionary<string, List<InventoryNode>> children,
        ICollection<HermesLoadoutWarning> warnings)
    {
        var output = new List<HermesArmorReadiness>();
        foreach (var root in roots.Where(item => ArmorSlots.Contains(item.SlotId ?? string.Empty)))
        {
            var tree = CollectTree(root, children);
            var template = GetTemplate(root.TemplateId);
            if (!template.IsArmor && tree.All(item => !GetTemplate(item.TemplateId).IsArmor))
            {
                continue;
            }

            var armorNodes = tree.Where(item => GetTemplate(item.TemplateId).IsArmor).ToList();
            var conditionValues = armorNodes
                .Select(item => GetCondition(item, GetTemplate(item.TemplateId)))
                .Where(condition => condition.HasCondition)
                .ToList();
            var conditionPercent = conditionValues.Count > 0
                ? conditionValues.Min(condition => condition.Percent)
                : 100;
            var conditionText = conditionValues.Count > 0
                ? conditionValues.OrderBy(condition => condition.Percent).First().Description
                : "No durability resource";
            var maximumArmorClass = armorNodes
                .Select(item => GetTemplate(item.TemplateId).ArmorClass)
                .DefaultIfEmpty(0)
                .Max();
            var plateSlots = new List<PlateSlotState>();

            foreach (var parent in tree)
            {
                var parentTemplate = GetTemplate(parent.TemplateId);
                foreach (var slot in parentTemplate.ArmorSlots)
                {
                    var installed = children.GetValueOrDefault(parent.Id)?
                        .FirstOrDefault(child => string.Equals(child.SlotId, slot.Name, StringComparison.OrdinalIgnoreCase));
                    plateSlots.Add(new PlateSlotState(slot.Name, slot.Required, installed is not null));
                }
            }

            var installedCount = plateSlots.Count(slot => slot.Installed);
            var missingRequired = plateSlots.Count(slot => slot.Required && !slot.Installed);
            var emptyOptional = plateSlots.Count(slot => !slot.Required && !slot.Installed);
            var localWarnings = new List<string>();

            if (conditionPercent < 50)
            {
                localWarnings.Add($"Low armor durability: {conditionPercent}%.");
                warnings.Add(new HermesLoadoutWarning(
                    conditionPercent < 25 ? "Critical" : "Warning",
                    "Armor",
                    $"{template.Name} armor durability is {conditionPercent}%."));
            }

            if (missingRequired > 0)
            {
                localWarnings.Add($"{missingRequired} required armor insert slot(s) are empty.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Armor",
                    $"{template.Name} is missing {missingRequired} required armor insert(s)."));
            }
            else if (emptyOptional > 0)
            {
                localWarnings.Add($"{emptyOptional} optional armor insert slot(s) are empty.");
                warnings.Add(new HermesLoadoutWarning(
                    "Warning",
                    "Armor",
                    $"{template.Name} has {emptyOptional} empty optional armor insert slot(s)."));
            }

            output.Add(new HermesArmorReadiness(
                FriendlySlotName(root.SlotId),
                template.Name,
                conditionPercent,
                conditionText,
                maximumArmorClass,
                plateSlots.Count,
                installedCount,
                missingRequired,
                emptyOptional,
                localWarnings.Count == 0 ? "Ready" : missingRequired > 0 ? "Not ready" : "Review",
                localWarnings));
        }

        if (output.Count == 0)
        {
            warnings.Add(new HermesLoadoutWarning(
                "Warning",
                "Armor",
                "No body armor, armored rig, or armored headwear is equipped."));
        }

        return output;
    }

    private HermesMedicalReadiness BuildMedicalReadiness(
        IReadOnlyList<InventoryNode> equippedItems,
        ICollection<HermesLoadoutWarning> warnings)
    {
        var medicalItems = new List<HermesCarriedMedicalItem>();
        var lightBleed = false;
        var heavyBleed = false;
        var fracture = false;
        var pain = false;
        var surgery = false;
        var totalHealing = 0d;
        var hydrationProvisions = 0;
        var energyProvisions = 0;

        foreach (var item in equippedItems)
        {
            var template = GetTemplate(item.TemplateId);
            if (template.IsMedical)
            {
                var currentResource = ReadDouble(
                    GetProperty(item.Upd, "MedKit", "medKit"),
                    template.MaximumMedicalResource,
                    "HpResource",
                    "hpResource");
                if (template.MaximumMedicalResource > 0d)
                {
                    totalHealing += Math.Max(0d, currentResource);
                }

                lightBleed |= template.TreatsLightBleed;
                heavyBleed |= template.TreatsHeavyBleed;
                fracture |= template.TreatsFracture;
                pain |= template.TreatsPain;
                surgery |= template.IsSurgeryKit;
                medicalItems.Add(new HermesCarriedMedicalItem(
                    template.Name,
                    currentResource,
                    template.MaximumMedicalResource,
                    BuildMedicalCoverageText(template)));
            }

            if (template.ProvidesHydration)
            {
                hydrationProvisions++;
            }

            if (template.ProvidesEnergy)
            {
                energyProvisions++;
            }
        }

        if (totalHealing <= 0d)
        {
            warnings.Add(new HermesLoadoutWarning("Critical", "Medical", "No usable health-restoration resource is carried."));
        }

        if (!heavyBleed)
        {
            warnings.Add(new HermesLoadoutWarning("Critical", "Medical", "No heavy-bleeding treatment is carried."));
        }

        if (!lightBleed)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No light-bleeding treatment is carried."));
        }

        if (!fracture)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No fracture treatment is carried."));
        }

        if (!pain)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No pain treatment is carried."));
        }

        return new HermesMedicalReadiness(
            medicalItems.Count,
            totalHealing,
            lightBleed,
            heavyBleed,
            fracture,
            pain,
            surgery,
            hydrationProvisions,
            energyProvisions,
            medicalItems
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

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
            questsRoot = JsonNode.Parse(jsonUtil.Serialize(databaseService.GetQuests()) ?? "{}");
        }
        catch
        {
            return [];
        }

        var traderNames = databaseService.GetTraders().ToDictionary(
            pair => pair.Key.ToString(),
            pair => string.IsNullOrWhiteSpace(pair.Value.Base.Nickname)
                ? pair.Value.Base.Name ?? "Trader"
                : pair.Value.Base.Nickname,
            StringComparer.OrdinalIgnoreCase);
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
            var mapName = FriendlyMapName(ReadString(quest, "location", "Location"));
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
        }

        return output
            .OrderByDescending(requirement => requirement.IsRaidCritical && !requirement.IsSatisfied)
            .ThenBy(requirement => requirement.IsSatisfied)
            .ThenBy(requirement => requirement.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.QuestName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.RequiredEquipment, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        string missingNote)
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

    private TemplateInfo GetTemplate(string templateId)
    {
        lock (_templateSync)
        {
            if (_templateCache.TryGetValue(templateId, out var cached))
            {
                return cached;
            }
        }

        if (!MongoId.IsValidMongoId(templateId)
            || !databaseService.GetItems().TryGetValue(new MongoId(templateId), out var template))
        {
            var missing = TemplateInfo.Missing(templateId);
            lock (_templateSync)
            {
                _templateCache[templateId] = missing;
            }

            return missing;
        }

        var root = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
        var props = GetProperty(root, "_props", "Properties", "properties") as JsonObject;
        var name = ResolveTemplateName(templateId);
        var serializedProps = props?.ToJsonString() ?? string.Empty;
        var lowerName = name.ToLowerInvariant();
        var caliber = ReadString(props, "Caliber", "caliber", "AmmoCaliber", "ammoCaliber") ?? string.Empty;
        var weaponClass = ReadString(props, "weapClass", "WeapClass") ?? string.Empty;
        var cartridges = GetArray(GetProperty(props, "Cartridges", "cartridges"));
        var magazineCapacity = cartridges
            .Select(cartridge => ReadInt(cartridge, 0, "_max_count", "MaxCount", "max_count"))
            .DefaultIfEmpty(0)
            .Max();
        var armorClass = ReadInt(props, 0, "ArmorClass", "armorClass");
        var maximumMedicalResource = ReadDouble(props, 0d, "MaxHpResource", "maxHpResource");
        var maximumResource = ReadDouble(props, 0d, "MaxResource", "maxResource");
        var isAmmo = ReadDouble(props, 0d, "Damage", "damage") > 0d
                     || ReadDouble(props, 0d, "PenetrationPower", "penetrationPower") > 0d;
        var isMagazine = cartridges.Count > 0
                         || serializedProps.Contains("magAnimationIndex", StringComparison.OrdinalIgnoreCase);
        var isWeapon = !string.IsNullOrWhiteSpace(weaponClass)
                       || GetArray(GetProperty(props, "weapFireType", "WeapFireType")).Count > 0;
        var isMedical = maximumMedicalResource > 0d
                        || serializedProps.Contains("effects_damage", StringComparison.OrdinalIgnoreCase)
                        || serializedProps.Contains("EffectsDamage", StringComparison.OrdinalIgnoreCase)
                        || serializedProps.Contains("MedUseType", StringComparison.OrdinalIgnoreCase);
        var isSurgeryKit = lowerName.Contains("cms", StringComparison.Ordinal)
                           || lowerName.Contains("surv12", StringComparison.Ordinal)
                           || lowerName.Contains("surgical", StringComparison.Ordinal)
                           || serializedProps.Contains("Surgery", StringComparison.OrdinalIgnoreCase);
        var treatsLightBleed = ContainsAny(serializedProps, "LightBleeding", "Light bleed")
                               || ContainsAny(lowerName, "bandage", "army bandage");
        var treatsHeavyBleed = ContainsAny(serializedProps, "HeavyBleeding", "Heavy bleed")
                               || ContainsAny(lowerName, "esmarch", "hemostat", "cat tourniquet", "calok");
        var treatsFracture = ContainsAny(serializedProps, "Fracture")
                             || ContainsAny(lowerName, "splint", "surv12");
        var treatsPain = ContainsAny(serializedProps, "Pain")
                         || ContainsAny(lowerName, "painkiller", "analgin", "ibuprofen", "golden star", "vaseline");
        var isFoodDrink = ReadDouble(props, 0d, "FoodUseTime", "foodUseTime") > 0d
                          || serializedProps.Contains("FoodDrink", StringComparison.OrdinalIgnoreCase)
                          || ContainsAny(lowerName, "water", "juice", "milk", "drink", "cola", "tea", "coffee", "ration", "iskra", "crackers", "sausage", "tushonka", "sugar", "chocolate");
        var providesHydration = isFoodDrink
                                && serializedProps.Contains("Hydration", StringComparison.OrdinalIgnoreCase)
                                && maximumResource > 0d;
        var providesEnergy = isFoodDrink
                             && serializedProps.Contains("Energy", StringComparison.OrdinalIgnoreCase)
                             && maximumResource > 0d;
        var isArmor = armorClass > 0 || serializedProps.Contains("ArmorType", StringComparison.OrdinalIgnoreCase);
        var isKey = ReadDouble(props, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage") > 0d
                    || !string.IsNullOrWhiteSpace(ReadString(props, "KeyId", "keyId"))
                    || lowerName.EndsWith(" key", StringComparison.Ordinal)
                    || lowerName.Contains("marked key", StringComparison.Ordinal);
        var armorSlots = ParseArmorSlots(props);
        var internalMagazine = isWeapon
                               && magazineCapacity > 0
                               && !GetArray(GetProperty(props, "Slots", "slots"))
                                   .Any(slot => IsMagazineSlot(ReadString(slot, "_name", "Name", "name")));

        var info = new TemplateInfo(
            true,
            templateId,
            name,
            props,
            serializedProps,
            caliber,
            isWeapon,
            isMagazine,
            isAmmo,
            isArmor,
            isKey,
            armorClass,
            magazineCapacity,
            internalMagazine,
            isMedical,
            maximumMedicalResource,
            treatsLightBleed,
            treatsHeavyBleed,
            treatsFracture,
            treatsPain,
            isSurgeryKit,
            providesHydration,
            providesEnergy,
            armorSlots);
        lock (_templateSync)
        {
            _templateCache[templateId] = info;
        }

        return info;
    }

    private string ResolveTemplateName(string templateId)
    {
        if (MongoId.IsValidMongoId(templateId))
        {
            var catalogName = catalogService.GetPlayerFacingName(new MongoId(templateId));
            if (!string.IsNullOrWhiteSpace(catalogName)
                && !catalogName.Equals("Unknown item", StringComparison.OrdinalIgnoreCase))
            {
                return catalogName;
            }
        }

        EnsureLocaleStrings();
        lock (_localeSync)
        {
            if (_localeStrings is not null
                && _localeStrings.TryGetValue(templateId + " Name", out var localized)
                && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        return "Unknown quest item";
    }

    private void EnsureLocaleStrings()
    {
        if (_localeStrings is not null)
        {
            return;
        }

        lock (_localeSync)
        {
            if (_localeStrings is not null)
            {
                return;
            }

            var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = JsonNode.Parse(jsonUtil.Serialize(databaseService.GetLocales()) ?? "{}");
                var english = FindObjectPropertyRecursive(root, "en") ?? root;
                FlattenLocaleStrings(english, output);
            }
            catch
            {
                // Missing locale data should not block loadout analysis.
            }

            _localeStrings = output;
        }
    }

    private static JsonNode? FindObjectPropertyRecursive(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && pair.Value is JsonObject)
                {
                    return pair.Value;
                }

                var nested = FindObjectPropertyRecursive(pair.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nested = FindObjectPropertyRecursive(child, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void FlattenLocaleStrings(JsonNode? node, IDictionary<string, string> output)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    output.TryAdd(pair.Key, text);
                }
                else
                {
                    FlattenLocaleStrings(pair.Value, output);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                FlattenLocaleStrings(child, output);
            }
        }
    }

    private static IReadOnlyList<ArmorSlotDefinition> ParseArmorSlots(JsonObject? props)
    {
        var output = new List<ArmorSlotDefinition>();
        foreach (var slot in GetArray(GetProperty(props, "Slots", "slots")))
        {
            var name = ReadString(slot, "_name", "Name", "name") ?? string.Empty;
            if (!IsArmorSlot(name))
            {
                continue;
            }

            output.Add(new ArmorSlotDefinition(
                name,
                ReadBool(slot, false, "_required", "Required", "required")));
        }

        return output;
    }

    private IReadOnlySet<string> CollectAllowedMagazineTemplates(IReadOnlyList<InventoryNode> weaponTree)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in weaponTree)
        {
            var props = GetTemplate(node.TemplateId).Properties;
            foreach (var slot in GetArray(GetProperty(props, "Slots", "slots")))
            {
                var slotName = ReadString(slot, "_name", "Name", "name");
                if (!IsMagazineSlot(slotName))
                {
                    continue;
                }

                CollectMongoIds(GetProperty(slot, "_props", "Props", "props", "filters", "Filters"), output);
            }
        }

        return output;
    }

    private bool IsMagazineCompatible(
        InventoryNode magazine,
        string weaponCaliber,
        IReadOnlySet<string> allowedTemplates,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        if (allowedTemplates.Count > 0)
        {
            return allowedTemplates.Contains(magazine.TemplateId);
        }

        var loadedCalibers = CollectTree(magazine, children)
            .Where(item => IsLoadedAmmoSlot(item.SlotId) && GetTemplate(item.TemplateId).IsAmmo)
            .Select(item => GetTemplate(item.TemplateId).Caliber)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (loadedCalibers.Count > 0)
        {
            return loadedCalibers.All(caliber => CaliberMatches(weaponCaliber, caliber));
        }

        var supportedCalibers = GetMagazineSupportedCalibers(GetTemplate(magazine.TemplateId));
        return supportedCalibers.Count == 0
               || supportedCalibers.Any(caliber => CaliberMatches(weaponCaliber, caliber));
    }

    private IReadOnlyList<string> GetMagazineSupportedCalibers(TemplateInfo magazine)
    {
        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMongoIds(GetProperty(magazine.Properties, "Cartridges", "cartridges"), templateIds);
        return templateIds
            .Select(GetTemplate)
            .Where(template => template.IsAmmo && !string.IsNullOrWhiteSpace(template.Caliber))
            .Select(template => template.Caliber)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<InventoryNode> ParseInventoryItems(JsonNode? inventory)
    {
        var output = new List<InventoryNode>();
        foreach (var node in GetArray(GetProperty(inventory, "items", "Items")))
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var id = ReadString(item, "_id", "Id", "id");
            var templateId = ReadString(item, "_tpl", "Template", "template");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }

            output.Add(new InventoryNode(
                id,
                templateId,
                ReadString(item, "parentId", "ParentId"),
                ReadString(item, "slotId", "SlotId"),
                GetProperty(item, "upd", "Upd") as JsonObject));
        }

        return output;
    }

    private static IReadOnlyList<InventoryNode> CollectDescendants(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        var output = new List<InventoryNode>();
        var queue = new Queue<InventoryNode>(roots);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Id))
            {
                continue;
            }

            output.Add(current);
            if (!children.TryGetValue(current.Id, out var childItems))
            {
                continue;
            }

            foreach (var child in childItems)
            {
                queue.Enqueue(child);
            }
        }

        return output;
    }

    private static IReadOnlyList<InventoryNode> CollectTree(
        InventoryNode root,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        return CollectDescendants([root], children);
    }

    private ConditionInfo GetCondition(InventoryNode item, TemplateInfo template)
    {
        var repairable = GetProperty(item.Upd, "Repairable", "repairable");
        if (repairable is not null)
        {
            var current = ReadDouble(repairable, 0d, "Durability", "durability");
            var maximum = ReadDouble(repairable, current, "MaxDurability", "maxDurability");
            return new ConditionInfo(
                ToPercent(current, maximum),
                $"Durability {FormatNumber(current)}/{FormatNumber(maximum)}",
                true);
        }

        return new ConditionInfo(100, "Full condition", false);
    }

    private static string BuildMedicalCoverageText(TemplateInfo template)
    {
        var coverage = new List<string>();
        if (template.MaximumMedicalResource > 0d)
        {
            coverage.Add("healing");
        }

        if (template.TreatsLightBleed)
        {
            coverage.Add("light bleed");
        }

        if (template.TreatsHeavyBleed)
        {
            coverage.Add("heavy bleed");
        }

        if (template.TreatsFracture)
        {
            coverage.Add("fracture");
        }

        if (template.TreatsPain)
        {
            coverage.Add("pain");
        }

        if (template.IsSurgeryKit)
        {
            coverage.Add("surgery");
        }

        return coverage.Count == 0 ? "medical utility" : string.Join(", ", coverage);
    }

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
            questsRoot = JsonNode.Parse(jsonUtil.Serialize(databaseService.GetQuests()) ?? "{}");
        }
        catch
        {
            return [];
        }

        var traderNames = databaseService.GetTraders().ToDictionary(
            pair => pair.Key.ToString(),
            pair => string.IsNullOrWhiteSpace(pair.Value.Base.Nickname)
                ? pair.Value.Base.Name ?? "Trader"
                : pair.Value.Base.Nickname,
            StringComparer.OrdinalIgnoreCase);
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
            var mapName = FriendlyMapName(ReadString(quest, "location", "Location"));
            var conditions = GetProperty(quest, "conditions", "Conditions");
            var objectives = new List<HermesRaidPlanObjective>();
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
                    var conditionId = ReadString(condition, "id", "Id") ?? $"{group}:{conditionType}:{objectives.Count}";
                    if (!objectiveIds.Add(conditionId))
                    {
                        continue;
                    }

                    var completed = state.CompletedConditions.Contains(conditionId);
                    var isRaidObjective = IsRaidObjectiveCondition(condition, conditionType);
                    objectives.Add(new HermesRaidPlanObjective(
                        FriendlyQuestConditionLabel(conditionType),
                        DescribeQuestObjective(condition, conditionType),
                        completed,
                        isRaidObjective,
                        completed ? "Complete" : isRaidObjective ? "Active" : "Progress"));
                }
            }

            var relatedRequirements = questRequirements
                .Where(requirement => requirement.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase)
                                      && requirement.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var missingRequirements = relatedRequirements.Count(requirement =>
                requirement.IsRaidCritical && !requirement.IsCompleted && !requirement.IsSatisfied);
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
                         requirement.FoundInRaidRequired
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
            var missing = Math.Max(0d, required - effectiveCarried);
            var satisfied = missing <= 0.001d;
            var first = values[0];
            var questNames = values
                .Select(value => value.QuestName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var note = satisfied
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

        if (requirements.Count == 0)
        {
            notes.Add("No explicit raid-item or equipment requirement is encoded for these objectives.");
        }
        else if (requirements.All(requirement => requirement.IsSatisfied))
        {
            notes.Add("All currently detectable raid-critical gear requirements are covered.");
        }

        notes.Add("HERMES only lists keys and tools explicitly declared by quest data; undeclared route keys are not inferred yet.");
        return notes;
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

    private static Dictionary<string, ActiveQuestState> GetActiveQuestStates(JsonObject profileRoot)
    {
        var activeQuests = new Dictionary<string, ActiveQuestState>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in GetArray(GetProperty(profileRoot, "Quests", "quests")))
        {
            var questId = ReadString(node, "qid", "QId", "questId", "QuestId", "_id", "id");
            if (string.IsNullOrWhiteSpace(questId))
            {
                continue;
            }

            var status = ParseQuestStatus(ReadString(node, "status", "Status") ?? string.Empty);
            if (status is not (2 or 3))
            {
                continue;
            }

            var completedConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectStrings(GetProperty(node, "completedConditions", "CompletedConditions"), completedConditions);
            activeQuests[questId] = new ActiveQuestState(status, completedConditions);
        }

        return activeQuests;
    }

    private static int CalculateReadinessScore(IEnumerable<HermesLoadoutWarning> warnings)
    {
        var score = 100;
        foreach (var warning in warnings)
        {
            score -= warning.Severity.ToLowerInvariant() switch
            {
                "critical" => 15,
                "warning" => 5,
                _ => 0
            };
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int SeverityRank(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" => 0,
            "warning" => 1,
            _ => 2
        };
    }

    private static int SlotRank(string? slotId)
    {
        var index = Array.FindIndex(EquipmentSlotOrder, slot =>
            slot.Equals(slotId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 999;
    }

    private static string FriendlySlotName(string? slotId)
    {
        return slotId switch
        {
            "FirstPrimaryWeapon" => "Primary weapon",
            "SecondPrimaryWeapon" => "Secondary weapon",
            "ArmorVest" => "Body armor",
            "TacticalVest" => "Tactical rig",
            "Headwear" => "Headwear",
            "Earpiece" => "Headset",
            "FaceCover" => "Face cover",
            "SecuredContainer" => "Secure container",
            null or "" => "Equipment",
            _ => SplitPascalCase(slotId)
        };
    }

    private static string DetermineSlotStatus(string? slotId, int conditionPercent)
    {
        if ((WeaponSlots.Contains(slotId ?? string.Empty) || ArmorSlots.Contains(slotId ?? string.Empty))
            && conditionPercent < 50)
        {
            return "Low condition";
        }

        return "Equipped";
    }

    private static bool IsMagazineSlot(string? slotId)
    {
        return (slotId ?? string.Empty).Contains("magazine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoadedAmmoSlot(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("cartridge", StringComparison.Ordinal)
               || normalized.Contains("chamber", StringComparison.Ordinal)
               || normalized.Contains("patron", StringComparison.Ordinal)
               || normalized.Equals("ammo", StringComparison.Ordinal);
    }

    private static bool IsArmorSlot(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("plate", StringComparison.Ordinal)
               || normalized.Contains("soft_armor", StringComparison.Ordinal)
               || normalized.Contains("softarmor", StringComparison.Ordinal)
               || normalized.Contains("armor_front", StringComparison.Ordinal)
               || normalized.Contains("armor_back", StringComparison.Ordinal)
               || normalized.Contains("armor_side", StringComparison.Ordinal);
    }

    private static bool IsPlantOrPlacementCondition(string conditionType)
    {
        return conditionType.Contains("Plant", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("PlaceBeacon", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("LeaveItemAtLocation", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("PlaceItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFoundInRaid(InventoryNode item)
    {
        return ReadBool(item.Upd, false, "SpawnedInSession", "spawnedInSession");
    }

    private static string FriendlyMapName(string? location)
    {
        var normalized = (location ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "any" or "all" => "Any map",
            "bigmap" or "customs" => "Customs",
            "factory4_day" or "factory4_night" or "factory" => "Factory",
            "woods" => "Woods",
            "shoreline" => "Shoreline",
            "interchange" => "Interchange",
            "rezervbase" or "reserve" => "Reserve",
            "laboratory" or "lab" => "The Lab",
            "lighthouse" => "Lighthouse",
            "tarkovstreets" or "streets" => "Streets of Tarkov",
            "sandbox" or "sandbox_high" or "groundzero" => "Ground Zero",
            "labyrinth" => "The Labyrinth",
            "terminal" => "Terminal",
            _ => FormatDisplayName(location ?? "Any map")
        };
    }

    private static string FormatDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Any map";
        }

        var normalized = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.Join(
            " ",
            normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static bool IsRequiredEquipmentCondition(string conditionType)
    {
        if (conditionType.Contains("Exclusive", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return conditionType.Contains("Equipment", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("Wear", StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyQuestCondition(string conditionType)
    {
        if (conditionType.Contains("Wear", StringComparison.OrdinalIgnoreCase))
        {
            return "Wear equipment";
        }

        if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Handover item";
        }

        if (conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Find item";
        }

        if (conditionType.Contains("Beacon", StringComparison.OrdinalIgnoreCase))
        {
            return "Place marker";
        }

        if (conditionType.Contains("Plant", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("LeaveItem", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("PlaceItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Plant item";
        }

        return "Required equipment";
    }

    private static bool CaliberMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        return NormalizeCaliber(left).Equals(NormalizeCaliber(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCaliber(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string FriendlyCaliber(string? caliber)
    {
        if (string.IsNullOrWhiteSpace(caliber))
        {
            return "Unknown caliber";
        }

        return caliber
            .Replace("Caliber", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("x", "×", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static double GetStackCount(InventoryNode item)
    {
        return Math.Max(1d, ReadDouble(item.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
    }

    private static int ParseQuestStatus(string status)
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
            _ => 0
        };
    }

    private static IReadOnlyList<string> ReadMongoIds(JsonNode? node)
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

    private static void CollectStrings(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
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
        }
    }

    private static (double Current, double Maximum) ReadCurrentMaximum(JsonNode? node)
    {
        return (
            ReadDouble(node, 0d, "Current", "current"),
            ReadDouble(node, 0d, "Maximum", "maximum"));
    }

    private static int ToPercent(double current, double maximum)
    {
        return maximum <= 0d
            ? 0
            : Math.Clamp(Convert.ToInt32(Math.Round(current / maximum * 100d)), 0, 100);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var output = new List<char>(value.Length + 8) { value[0] };
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsWhiteSpace(value[index - 1]))
            {
                output.Add(' ');
            }

            output.Add(value[index]);
        }

        return new string(output.ToArray());
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var direct))
            {
                return direct;
            }

            var pair = obj.FirstOrDefault(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Key))
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
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value?.ToString();
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (jsonValue.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonNode? node, int fallback, params string[] names)
    {
        return Convert.ToInt32(Math.Round(ReadDouble(node, fallback, names)));
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static HermesLoadoutSummaryResponse NotFound(string message)
    {
        return new HermesLoadoutSummaryResponse(
            false,
            message,
            "UNAVAILABLE",
            0,
            0,
            0,
            new HermesVitalsSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            [],
            [],
            [],
            new HermesMedicalReadiness(0, 0, false, false, false, false, false, 0, 0, []),
            [],
            [],
            [],
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

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
        bool ProvidesHydration,
        bool ProvidesEnergy,
        IReadOnlyList<ArmorSlotDefinition> ArmorSlots)
    {
        public static TemplateInfo Missing(string templateId) => new(
            false,
            templateId,
            "Unknown item",
            null,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            false,
            false,
            0,
            0,
            false,
            false,
            0d,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            []);
    }

    private sealed record ArmorSlotDefinition(string Name, bool Required);
    private sealed record PlateSlotState(string Name, bool Required, bool Installed);
    private sealed record ConditionInfo(int Percent, string Description, bool HasCondition);
    private sealed record RaidQuestDraft(string MapName, HermesRaidPlanQuest Quest);
    private sealed record ActiveQuestState(int Status, IReadOnlySet<string> CompletedConditions);
}
