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
    private HermesVitalsSummary BuildVitals(
        JsonObject root,
        ICollection<HermesLoadoutWarning> warnings,
        HermesLoadoutAnalysisSettings settings)
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

        if (hydration.Maximum > 0d && hydrationPercent < settings.HydrationWarningPercent)
        {
            var criticalThreshold = Math.Max(1, settings.HydrationWarningPercent / 2);
            warnings.Add(new HermesLoadoutWarning(
                hydrationPercent < criticalThreshold ? "Critical" : "Warning",
                "Vitals",
                $"Hydration is low at {hydrationPercent}% (configured warning: {settings.HydrationWarningPercent}%)."));
        }

        if (energy.Maximum > 0d && energyPercent < settings.EnergyWarningPercent)
        {
            var criticalThreshold = Math.Max(1, settings.EnergyWarningPercent / 2);
            warnings.Add(new HermesLoadoutWarning(
                energyPercent < criticalThreshold ? "Critical" : "Warning",
                "Vitals",
                $"Energy is low at {energyPercent}% (configured warning: {settings.EnergyWarningPercent}%)."));
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
        IReadOnlyDictionary<string, List<InventoryNode>> children,
        HermesLoadoutAnalysisSettings settings)
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
                DetermineSlotStatus(root.SlotId, condition.Percent, settings)));
        }

        return output;
    }

    private IReadOnlyList<HermesWeaponReadiness> BuildWeaponReadiness(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyList<InventoryNode> equippedItems,
        IReadOnlyDictionary<string, List<InventoryNode>> children,
        ICollection<HermesLoadoutWarning> warnings,
        HermesLoadoutAnalysisSettings settings)
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

            if (condition.Percent < settings.MinimumWeaponDurabilityPercent)
            {
                var criticalThreshold = Math.Max(1, settings.MinimumWeaponDurabilityPercent - 20);
                localWarnings.Add($"Low durability: {condition.Percent}% (configured minimum: {settings.MinimumWeaponDurabilityPercent}%).");
                warnings.Add(new HermesLoadoutWarning(
                    condition.Percent < criticalThreshold ? "Critical" : "Warning",
                    "Weapons",
                    $"{template.Name} durability is {condition.Percent}% (configured minimum: {settings.MinimumWeaponDurabilityPercent}%)."));
            }

            if (attachedMagazine is null && !template.IsInternalMagazineWeapon)
            {
                localWarnings.Add("No magazine installed.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Weapons",
                    $"{template.Name} has no magazine installed."));
            }

            if (loadedRounds < settings.MinimumLoadedRounds)
            {
                localWarnings.Add($"Loaded ammunition: {FormatNumber(loadedRounds)}/{settings.MinimumLoadedRounds:N0} configured minimum.");
                warnings.Add(new HermesLoadoutWarning(
                    loadedRounds <= 0d ? "Critical" : "Warning",
                    "Ammunition",
                    $"{template.Name} has {FormatNumber(loadedRounds)} loaded round(s); configured minimum is {settings.MinimumLoadedRounds:N0}."));
            }

            if (mixedAmmo)
            {
                localWarnings.Add("Mixed ammunition is loaded.");
                warnings.Add(new HermesLoadoutWarning(
                    "Warning",
                    "Ammunition",
                    $"{template.Name} has mixed ammunition loaded."));
            }

            var caliberMismatch = !string.IsNullOrWhiteSpace(weaponCaliber)
                                  && ammoCalibers.Any(caliber => !CaliberMatches(weaponCaliber, caliber));
            if (caliberMismatch)
            {
                localWarnings.Add("Loaded ammunition caliber does not match the weapon.");
                warnings.Add(new HermesLoadoutWarning(
                    "Critical",
                    "Ammunition",
                    $"{template.Name} has ammunition that does not match {FriendlyCaliber(weaponCaliber)}."));
            }

            if (compatibleSpareMags.Count < settings.MinimumSpareMagazines)
            {
                localWarnings.Add($"Compatible spare magazines: {compatibleSpareMags.Count:N0}/{settings.MinimumSpareMagazines:N0} configured minimum.");
                warnings.Add(new HermesLoadoutWarning(
                    "Warning",
                    "Ammunition",
                    $"{template.Name} has {compatibleSpareMags.Count:N0} compatible spare magazine(s); configured minimum is {settings.MinimumSpareMagazines:N0}."));
            }

            var compatibleSpareRounds = spareRounds + looseRounds;
            if (compatibleSpareRounds < settings.MinimumSpareRounds)
            {
                localWarnings.Add($"Compatible spare rounds: {FormatNumber(compatibleSpareRounds)}/{settings.MinimumSpareRounds:N0} configured minimum.");
                warnings.Add(new HermesLoadoutWarning(
                    compatibleSpareRounds <= 0d && settings.MinimumSpareRounds > 0 ? "Critical" : "Warning",
                    "Ammunition",
                    $"{template.Name} has {FormatNumber(compatibleSpareRounds)} compatible spare round(s) across spare magazines and loose ammunition; configured minimum is {settings.MinimumSpareRounds:N0}."));
            }

            var hasCriticalReadinessIssue = (attachedMagazine is null && !template.IsInternalMagazineWeapon)
                                            || (settings.MinimumLoadedRounds > 0 && loadedRounds <= 0d)
                                            || caliberMismatch;
            var status = localWarnings.Count == 0
                ? "Ready"
                : hasCriticalReadinessIssue
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
        ICollection<HermesLoadoutWarning> warnings,
        HermesLoadoutAnalysisSettings settings)
    {
        var output = new List<HermesArmorReadiness>();
        var hasTorsoArmor = false;
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
            if (maximumArmorClass > 0
                && (string.Equals(root.SlotId, "ArmorVest", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(root.SlotId, "TacticalVest", StringComparison.OrdinalIgnoreCase)))
            {
                hasTorsoArmor = true;
            }

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

            if (conditionPercent < settings.MinimumArmorDurabilityPercent)
            {
                var criticalThreshold = Math.Max(1, settings.MinimumArmorDurabilityPercent / 2);
                localWarnings.Add($"Low armor durability: {conditionPercent}% (configured minimum: {settings.MinimumArmorDurabilityPercent}%).");
                warnings.Add(new HermesLoadoutWarning(
                    conditionPercent < criticalThreshold ? "Critical" : "Warning",
                    "Armor",
                    $"{template.Name} armor durability is {conditionPercent}% (configured minimum: {settings.MinimumArmorDurabilityPercent}%)."));
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

        if (!hasTorsoArmor)
        {
            warnings.Add(new HermesLoadoutWarning(
                "Critical",
                "Armor",
                "No body armor or armored rig is equipped."));
        }

        return output;
    }

    private HermesMedicalReadiness BuildMedicalReadiness(
        IReadOnlyList<InventoryNode> equippedItems,
        ICollection<HermesLoadoutWarning> warnings,
        HermesLoadoutAnalysisSettings settings)
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

            var hasUsableProvision = HasUsableConsumableResource(item, template);
            if (hasUsableProvision && template.ProvidesHydration)
            {
                hydrationProvisions++;
            }

            if (hasUsableProvision && template.ProvidesEnergy)
            {
                energyProvisions++;
            }
        }

        if (totalHealing < settings.MinimumHealingResource)
        {
            warnings.Add(new HermesLoadoutWarning(
                totalHealing <= 0d ? "Critical" : "Warning",
                "Medical",
                $"Total healing resource is {FormatNumber(totalHealing)}; configured minimum is {settings.MinimumHealingResource:N0}."));
        }

        if (settings.RequireHeavyBleedTreatment && !heavyBleed)
        {
            warnings.Add(new HermesLoadoutWarning("Critical", "Medical", "No heavy-bleeding treatment is carried."));
        }

        if (settings.RequireLightBleedTreatment && !lightBleed)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No light-bleeding treatment is carried."));
        }

        if (settings.RequireFractureTreatment && !fracture)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No fracture treatment is carried."));
        }

        if (settings.RequirePainTreatment && !pain)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Medical", "No pain treatment is carried."));
        }

        if (!surgery)
        {
            warnings.Add(new HermesLoadoutWarning("Critical", "Medical", "No surgery kit is carried."));
        }

        if (settings.RequireHydrationProvision && hydrationProvisions <= 0)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Sustainment", "No carried provision provides hydration."));
        }

        if (settings.RequireEnergyProvision && energyProvisions <= 0)
        {
            warnings.Add(new HermesLoadoutWarning("Warning", "Sustainment", "No carried provision provides energy."));
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
}
