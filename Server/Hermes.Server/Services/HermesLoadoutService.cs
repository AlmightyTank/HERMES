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
    HermesPreparedProfileSnapshotService preparedProfiles,
    HermesCatalogService catalogService,
    HermesLoadoutValueService loadoutValueService,
    HermesStashService stashService,
    HermesProfileScopeService profileScopeService,
    HermesQuestKeyKnowledgeService questKeyKnowledgeService,
    HermesStaticDataSnapshotService staticData,
    JsonUtil jsonUtil)
{
    private const string Ms2000MarkerTemplateId = "5991b51486f77447b112d44f";

    // SPT quest conditions do not always declare access keys or per-objective maps.
    // These rules cover known vanilla gaps; the runtime inference layer below also
    // matches localized quest text against the local key catalog for modded quests.
    private static readonly IReadOnlyList<QuestRouteKeyRule> QuestRouteKeyRules =
    [
        new(
            "5936da9e86f7742d65037edf",
            "Customs",
            "5937ee6486f77408994ba448",
            "FindItem",
            ["bronze pocket watch", "pocket watch"],
            "Required to reach the bronze pocket watch objective."),
        new(
            "59674eb386f774539f14813a",
            "Customs",
            "5780d0532459777a5108b9a2",
            "FindItem",
            ["secure folder 0022", "secure case for documents 0022", "folder 0022"],
            "Required to enter the Tarcone Director's office for the Secure Folder 0022 pickup."),
        new(
            "5967530a86f77462ba22226b",
            "Customs",
            "5938144586f77473c2087145",
            "FindItem",
            ["secure case for documents 0031", "documents 0031"],
            "Required to reach the Secure Folder 0031 objective."),
        new(
            "5967725e86f774601a446662",
            "Customs",
            "5938504186f7740991483f30",
            "FindItem",
            ["bank case"],
            "Required to reach the bank case objective.")
    ];

    // Some access keys are intentionally obtained inside the same raid rather than
    // brought from the stash. These remain raid-plan requirements, but do not count
    // as missing pre-raid gear.
    private static readonly IReadOnlyList<QuestInRaidRouteKeyRule> QuestInRaidRouteKeyRules =
    [
        new(
            "Saving the Mole",
            "Ground Zero",
            ["TerraGroup science office key", "science office key"],
            ["access the lab scientist's office", "scientist's office"],
            "Loot the TerraGroup science office key from the lab scientist's body, then use it to open office no. 4.")
    ];

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
    private readonly object _keyTemplateSync = new();
    private IReadOnlyList<TemplateInfo>? _keyTemplates;
    private readonly object _summaryCacheSync = new();
    private readonly object _summaryBuildSync = new();
    private readonly Dictionary<string, LoadoutCacheEntry> _summaryCache = new(StringComparer.OrdinalIgnoreCase);
    private long _summaryCacheHits;
    private long _summaryCacheMisses;
    private long _summaryCacheWrites;
    private long _contentRevision;
    private long _summaryCacheGeneration;
    private const int SummaryCacheTtlSeconds = 0; // revision-bound; no time expiry

    public HermesLoadoutSummaryResponse GetSummary(
        MongoId sessionId,
        HermesLoadoutAnalysisSettings? settings = null)
    {
        settings ??= HermesLoadoutAnalysisSettings.Default;
        var scope = profileScopeService.ResolveIdentity(sessionId);
        if (scope is null)
        {
            return NotFound("HERMES could not resolve the active PMC profile scope.");
        }

        var key = $"{scope.ScopeKey}:{settings.CacheKey}";
        lock (_summaryCacheSync)
        {
            if (_summaryCache.TryGetValue(key, out var cached))
            {
                Interlocked.Increment(ref _summaryCacheHits);
                return cached.Response;
            }
        }

        // Loadout, Raid Planner, pre-raid readiness, and Assistant all share this materialization
        // gate. Server source revisions clear this cache only when a relevant domain changes.
        lock (_summaryBuildSync)
        {
            lock (_summaryCacheSync)
            {
                if (_summaryCache.TryGetValue(key, out var cached))
                {
                    Interlocked.Increment(ref _summaryCacheHits);
                    return cached.Response;
                }
            }

            Interlocked.Increment(ref _summaryCacheMisses);
            var generation = Interlocked.Read(ref _summaryCacheGeneration);
            var response = BuildSummary(sessionId, settings);

            // Loadout analysis can overlap a manual/source invalidation. Rebuild once after the
            // generation changes so the completed request cannot repopulate the cache with the
            // equipment or vitals snapshot that Clear() intentionally retired.
            if (generation != Interlocked.Read(ref _summaryCacheGeneration))
            {
                generation = Interlocked.Read(ref _summaryCacheGeneration);
                response = BuildSummary(sessionId, settings);
            }

            if (response.Found)
            {
                lock (_summaryCacheSync)
                {
                    if (generation == Interlocked.Read(ref _summaryCacheGeneration))
                    {
                        response = response with
                        {
                            ContentRevision = Interlocked.Increment(ref _contentRevision)
                        };
                        _summaryCache[key] = new LoadoutCacheEntry(response);
                        Interlocked.Increment(ref _summaryCacheWrites);
                    }
                }
            }

            return response;
        }
    }

    public void Clear(string? reason = null)
    {
        lock (_summaryCacheSync)
        {
            Interlocked.Increment(ref _summaryCacheGeneration);
            _summaryCache.Clear();
        }
    }

    public HermesAnalysisCacheDiagnostics GetCacheDiagnostics()
    {
        lock (_summaryCacheSync)
        {
            return new HermesAnalysisCacheDiagnostics(
                _summaryCache.Count,
                Interlocked.Read(ref _summaryCacheHits),
                Interlocked.Read(ref _summaryCacheMisses),
                Interlocked.Read(ref _summaryCacheWrites),
                SummaryCacheTtlSeconds);
        }
    }

    private HermesLoadoutSummaryResponse BuildSummary(
        MongoId sessionId,
        HermesLoadoutAnalysisSettings settings)
    {
        var preparedProfile = preparedProfiles.Get(sessionId);
        if (preparedProfile is null)
        {
            return NotFound("HERMES could not read the active PMC profile.");
        }

        var root = preparedProfile.Root;

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
        var questRaidItemsId = ReadString(inventoryNode, "QuestRaidItems", "questRaidItems");
        IReadOnlyList<InventoryNode> questRaidRoots = string.IsNullOrWhiteSpace(questRaidItemsId)
            ? []
            : items
                .Where(item => string.Equals(item.ParentId, questRaidItemsId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        var carriedQuestItems = equippedItems
            .Concat(CollectDescendants(questRaidRoots, children))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var warnings = new List<HermesLoadoutWarning>();
        var vitals = BuildVitals(root, warnings, settings);
        var slots = BuildSlotSummaries(equippedRoots, children, settings);
        var weapons = BuildWeaponReadiness(equippedRoots, equippedItems, children, warnings, settings);
        var armor = BuildArmorReadiness(equippedRoots, children, warnings, settings);
        var medical = BuildMedicalReadiness(equippedItems, warnings, settings);
        var questRequirements = BuildQuestRequirements(root, equippedRoots, carriedQuestItems, warnings);
        var raidPlans = BuildRaidPlans(root, questRequirements);
        var valueSummary = settings.IncludeValueAnalysis
            ? loadoutValueService.GetSummary(
                sessionId,
                warnings,
                settings.EnableInsuranceWarnings,
                settings.HighValueUninsuredThreshold)
            : DisabledValueSummary();

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
            valueSummary,
            warnings
                .OrderBy(warning => SeverityRank(warning.Severity))
                .ThenBy(warning => warning.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(warning => warning.Message, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

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
        var damageEffects = GetProperty(props, "effects_damage", "EffectsDamage");
        var stimulatorBuffs = GetProperty(props, "StimulatorBuffs", "stimulatorBuffs", "Buffs", "buffs");
        var hydrationEffect = FindNamedEffectValue(props, "Hydration");
        var energyEffect = FindNamedEffectValue(props, "Energy");
        var foodUseTime = ReadDouble(props, 0d, "FoodUseTime", "foodUseTime");
        var isFoodDrink = foodUseTime > 0d
                          || maximumResource > 0d
                             && (hydrationEffect.HasValue
                                 || energyEffect.HasValue
                                 || serializedProps.Contains("FoodDrink", StringComparison.OrdinalIgnoreCase))
                          || ContainsAny(
                              lowerName,
                              "water",
                              "juice",
                              "milk",
                              "drink",
                              "cola",
                              "tea",
                              "coffee",
                              "kvass",
                              "aquamari",
                              "ration",
                              "mre",
                              "iskra",
                              "crackers",
                              "sausage",
                              "tushonka",
                              "stew",
                              "sugar",
                              "chocolate");
        // Food can legitimately contain Buffs/StimulatorBuffs. Provision identity takes
        // precedence so ration packs are not counted as medicine merely because they grant buffs.
        var isMedical = !isFoodDrink
                        && (maximumMedicalResource > 0d
                            || HasEntries(damageEffects)
                            || ReadBool(props, false, "UseStimulatorBuffs", "useStimulatorBuffs")
                            || HasEntries(stimulatorBuffs));
        var isSurgeryKit = isMedical
                           && (lowerName.Contains("cms", StringComparison.Ordinal)
                           || lowerName.Contains("surv12", StringComparison.Ordinal)
                           || lowerName.Contains("surgical", StringComparison.Ordinal)
                           || serializedProps.Contains("Surgery", StringComparison.OrdinalIgnoreCase));
        var isGeneralBleedMedkit = ContainsAny(
            lowerName,
            "ifak",
            "afak",
            "salewa",
            "car first aid",
            "car kit",
            "grizzly");
        var treatsLightBleed = isMedical
                               && (ContainsAny(serializedProps, "LightBleeding", "Light bleed")
                                   || ContainsAny(lowerName, "bandage", "army bandage")
                                   || isGeneralBleedMedkit);
        var treatsHeavyBleed = isMedical
                               && (ContainsAny(serializedProps, "HeavyBleeding", "Heavy bleed")
                                   || ContainsAny(lowerName, "esmarch", "hemostat", "cat tourniquet", "calok")
                                   || isGeneralBleedMedkit);
        var treatsFracture = isMedical
                             && (ContainsAny(serializedProps, "Fracture")
                                 || ContainsAny(lowerName, "splint", "surv12"));
        var treatsPain = isMedical
                         && (ContainsAny(serializedProps, "Pain")
                             || ContainsAny(lowerName, "painkiller", "analgin", "ibuprofen", "golden star", "vaseline"));
        var providesHydration = isFoodDrink
                                && (hydrationEffect is > 0d
                                    || !hydrationEffect.HasValue
                                    && ContainsAny(
                                        lowerName,
                                        "water",
                                        "juice",
                                        "milk",
                                        "drink",
                                        "cola",
                                        "tea",
                                        "coffee",
                                        "kvass",
                                        "aquamari",
                                        "thermos"));
        var providesEnergy = isFoodDrink
                             && (energyEffect is > 0d
                                 || !energyEffect.HasValue
                                 && ContainsAny(
                                     lowerName,
                                     "ration",
                                     "mre",
                                     "iskra",
                                     "crackers",
                                     "sausage",
                                     "tushonka",
                                     "stew",
                                     "sugar",
                                     "chocolate",
                                     "oat",
                                     "peas",
                                     "squash",
                                     "herring",
                                     "sprats",
                                     "mayo",
                                     "condensed milk",
                                     "energy drink",
                                     "hot rod",
                                     "max energy"));
        var isArmor = armorClass > 0 || serializedProps.Contains("ArmorType", StringComparison.OrdinalIgnoreCase);
        var keyId = ReadString(props, "KeyId", "keyId") ?? string.Empty;
        var isKey = ReadDouble(props, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage") > 0d
                    || !string.IsNullOrWhiteSpace(keyId)
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
            keyId,
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
            maximumResource,
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
                var root = staticData.GetLocalesRoot();
                var englishFound = false;
                CollectLanguageLocaleStrings(root, "en", output, ref englishFound);
                if (!englishFound)
                {
                    FlattenLocaleStrings(root, output);
                }
            }
            catch
            {
                // Missing locale data should not block loadout analysis.
            }

            _localeStrings = output;
        }
    }

    private static void CollectLanguageLocaleStrings(
        JsonNode? node,
        string language,
        IDictionary<string, string> output,
        ref bool found)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    FlattenLocaleStrings(pair.Value, output);
                    continue;
                }

                CollectLanguageLocaleStrings(pair.Value, language, output, ref found);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectLanguageLocaleStrings(child, language, output, ref found);
            }
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
                    output[pair.Key] = text;
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

    private static bool HasUsableConsumableResource(
        InventoryNode item,
        TemplateInfo template)
    {
        if (!template.ProvidesHydration && !template.ProvidesEnergy)
        {
            return false;
        }

        var foodDrink = GetProperty(item.Upd, "FoodDrink", "foodDrink");
        if (foodDrink is null)
        {
            return true;
        }

        var fallback = template.MaximumConsumableResource > 0d
            ? template.MaximumConsumableResource
            : 1d;
        var remaining = ReadDouble(
            foodDrink,
            fallback,
            "HpPercent",
            "hpPercent",
            "Resource",
            "resource",
            "Value",
            "value");
        return remaining > 0d;
    }

    private static double? FindNamedEffectValue(JsonNode? node, string effectName)
    {
        if (node is JsonObject obj)
        {
            var effectType = ReadString(
                obj,
                "BuffType",
                "buffType",
                "EffectType",
                "effectType",
                "Type",
                "type");
            if (!string.IsNullOrWhiteSpace(effectType)
                && effectType.Equals(effectName, StringComparison.OrdinalIgnoreCase))
            {
                var typedValue = ReadNullableDouble(
                    GetProperty(obj, "Value", "value", "Amount", "amount"));
                if (typedValue.HasValue)
                {
                    return typedValue;
                }
            }

            foreach (var pair in obj)
            {
                if (pair.Key.Equals(effectName, StringComparison.OrdinalIgnoreCase))
                {
                    var directValue = ReadEffectValue(pair.Value);
                    if (directValue.HasValue)
                    {
                        return directValue;
                    }
                }

                var nestedValue = FindNamedEffectValue(pair.Value, effectName);
                if (nestedValue.HasValue)
                {
                    return nestedValue;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nestedValue = FindNamedEffectValue(child, effectName);
                if (nestedValue.HasValue)
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }

    private static double? ReadEffectValue(JsonNode? node)
    {
        var direct = ReadNullableDouble(node);
        if (direct.HasValue)
        {
            return direct;
        }

        if (node is JsonObject obj)
        {
            var named = ReadNullableDouble(
                GetProperty(obj, "Value", "value", "Amount", "amount"));
            if (named.HasValue)
            {
                return named;
            }
        }

        return null;
    }

    private static double? ReadNullableDouble(JsonNode? node)
    {
        if (node is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<double>(out var number))
        {
            return number;
        }

        if (jsonValue.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (jsonValue.TryGetValue<string>(out var text)
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
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

    private static string DetermineSlotStatus(
        string? slotId,
        int conditionPercent,
        HermesLoadoutAnalysisSettings settings)
    {
        var threshold = WeaponSlots.Contains(slotId ?? string.Empty)
            ? settings.MinimumWeaponDurabilityPercent
            : ArmorSlots.Contains(slotId ?? string.Empty)
                ? settings.MinimumArmorDurabilityPercent
                : 0;
        return threshold > 0 && conditionPercent < threshold
            ? "Low condition"
            : "Equipped";
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
            "sandbox" or "sandbox_high" or "groundzero" or "653e6760052c01c1c805532f" => "Ground Zero",
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

    private static bool HasEntries(JsonNode? node)
    {
        return node switch
        {
            JsonArray array => array.Count > 0,
            JsonObject obj => obj.Count > 0,
            JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
            JsonValue value when value.TryGetValue<string>(out var text) => !string.IsNullOrWhiteSpace(text),
            _ => false
        };
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

    private static HermesLoadoutValueSummary DisabledValueSummary()
    {
        return new HermesLoadoutValueSummary(
            false,
            "Value and insurance analysis is disabled in the HERMES BepInEx settings.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            "Disabled",
            0,
            0,
            0,
            0,
            0,
            0,
            "DISABLED",
            [],
            [],
            []);
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
            new HermesLoadoutValueSummary(
                false,
                message,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                "Unavailable",
                0,
                0,
                0,
                0,
                0,
                0,
                "UNAVAILABLE",
                [],
                [],
                []),
            [],
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private sealed record LoadoutCacheEntry(HermesLoadoutSummaryResponse Response);

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
