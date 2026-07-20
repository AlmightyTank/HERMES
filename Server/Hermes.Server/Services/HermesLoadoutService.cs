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
public sealed partial class HermesLoadoutService(
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
}
