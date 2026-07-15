using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesLoadoutPanel
{
    private enum LoadoutView
    {
        Overview,
        Weapons,
        Armor,
        Medical,
        Quests,
        RaidPlanner,
        ValueInsurance
    }

    private HermesLoadoutSummaryResponse? _summary;
    private Vector2 _scroll;
    private bool _loading;
    private bool _requested;
    private int _requestVersion;
    private LoadoutView _view;
    private readonly Dictionary<string, bool> _warningGroupExpanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _raidPlanExpanded = new(StringComparer.OrdinalIgnoreCase);
    private bool _defaultsInitialized;
    private bool _showCriticalWarnings = true;
    private bool _showAdvisoryWarnings = true;
    private bool _showOnlyUninsuredItems;
    private string _raidSearch = string.Empty;
    private string _raidStatusFilter = "All maps";
    private string _raidSorting = "Best prepared";
    private float _nextAutomaticRefresh;
    private HermesLoadoutRequestSettings _lastRequestSettings;
    private string _status = "Open this tab to inspect the active PMC raid loadout.";

    public void Draw()
    {
        InitializeDefaults();

        if (!_requested && !_loading)
        {
            _requested = true;
            _ = RefreshFromServerAsync(false);
        }

        var automaticRefreshSeconds = Plugin.Settings.GetAutomaticRefreshSeconds();
        if (automaticRefreshSeconds > 0
            && _summary is not null
            && !_loading
            && Time.realtimeSinceStartup >= _nextAutomaticRefresh)
        {
            _nextAutomaticRefresh = Time.realtimeSinceStartup + automaticRefreshSeconds;
            _ = RefreshFromServerAsync(false);
        }

        HermesUi.DrawPanelHeader(
            "LOADOUT READINESS & RAID PLANNING",
            "Configurable readiness thresholds, localized raid plans, carried-item valuation, and exact insurance state.",
            _status,
            _loading,
            () => _ = RefreshFromServerAsync(true));

        DrawViewTabs();
        GUILayout.Space(6f);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        if (_summary is not null)
        {
            DrawSummary(_summary);
        }
        else if (_loading)
        {
            GUILayout.Label("Reading the active PMC equipment tree, carried raid items, and active quest state...");
        }
        GUILayout.EndScrollView();
    }

    public async Task RefreshFromServerAsync(bool force)
    {
        if (_loading && !force)
        {
            return;
        }

        var version = ++_requestVersion;
        _loading = true;
        _requested = true;
        _status = "Analyzing equipment, localized objectives, carried raid tools, replacement value, insurance, ammunition, armor, treatment coverage, and vitals...";

        try
        {
            var requestSettings = Plugin.Settings.CreateLoadoutRequestSettings();
            _lastRequestSettings = requestSettings;
            var response = await HermesApiClient.GetLoadoutSummaryAsync(requestSettings);
            if (version != _requestVersion)
            {
                return;
            }

            _summary = response;
            _nextAutomaticRefresh = Time.realtimeSinceStartup + Plugin.Settings.GetAutomaticRefreshSeconds();
            InitializeWarningGroups(response.Warnings);
            _status = response.Found
                ? $"Loadout assessment: {response.Readiness} ({response.ReadinessScore}/100) • {response.CriticalCount} critical • {response.WarningCount} warning(s)."
                : response.Message ?? "HERMES could not analyze the active loadout.";
        }
        catch (Exception ex)
        {
            if (version == _requestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Loadout analysis");
            }

            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (version == _requestVersion)
            {
                _loading = false;
            }
        }
    }

    public void Clear()
    {
        _requestVersion++;
        _summary = null;
        _scroll = Vector2.zero;
        _loading = false;
        _requested = false;
        _view = LoadoutView.Overview;
        _defaultsInitialized = false;
        _showCriticalWarnings = true;
        _showAdvisoryWarnings = true;
        _showOnlyUninsuredItems = false;
        _raidSearch = string.Empty;
        _raidStatusFilter = "All maps";
        _raidPlanExpanded.Clear();
        _status = "Open this tab to inspect the active PMC raid loadout.";
    }

    private void InitializeDefaults()
    {
        if (_defaultsInitialized)
        {
            return;
        }

        _view = ParseLoadoutView(Plugin.Settings.GetDefaultLoadoutView());
        if (_view == LoadoutView.ValueInsurance && !Plugin.Settings.ShowValueAndInsurance.Value)
        {
            _view = LoadoutView.Overview;
        }

        _raidSorting = Plugin.Settings.GetRaidPlannerSorting();
        _defaultsInitialized = true;
    }

    private void DrawViewTabs()
    {
        GUILayout.BeginHorizontal();
        DrawViewButton("Overview", LoadoutView.Overview, 95f);
        DrawViewButton("Weapons & Ammo", LoadoutView.Weapons, 135f);
        DrawViewButton("Armor", LoadoutView.Armor, 80f);
        DrawViewButton("Medical", LoadoutView.Medical, 85f);
        DrawViewButton("Quest Gear", LoadoutView.Quests, 100f);
        DrawViewButton("Raid Planner", LoadoutView.RaidPlanner, 110f);
        if (Plugin.Settings.ShowValueAndInsurance.Value)
        {
            DrawViewButton("Value & Insurance", LoadoutView.ValueInsurance, 130f);
        }
        else if (_view == LoadoutView.ValueInsurance)
        {
            _view = LoadoutView.Overview;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawViewButton(string label, LoadoutView view, float width)
    {
        GUI.enabled = _view != view;
        if (GUILayout.Button(label, GUILayout.Width(width)))
        {
            _view = view;
            _scroll = Vector2.zero;
        }

        GUI.enabled = true;
    }

    private void DrawSummary(HermesLoadoutSummaryResponse summary)
    {
        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Loadout analysis is unavailable.");
            return;
        }

        DrawReadinessHeader(summary);
        GUILayout.Space(8f);

        switch (_view)
        {
            case LoadoutView.Weapons:
                DrawWeapons(summary.Weapons, _lastRequestSettings);
                break;
            case LoadoutView.Armor:
                DrawArmor(summary.Armor, _lastRequestSettings);
                break;
            case LoadoutView.Medical:
                DrawMedical(summary.Medical, _lastRequestSettings);
                break;
            case LoadoutView.Quests:
                DrawQuestRequirements(summary.QuestRequirements);
                break;
            case LoadoutView.RaidPlanner:
                DrawRaidPlans(summary);
                break;
            case LoadoutView.ValueInsurance:
                DrawValueAndInsurance(summary.ValueSummary);
                break;
            default:
                DrawOverview(summary);
                break;
        }

        var generated = DateTimeOffset.FromUnixTimeSeconds(summary.GeneratedUnixTime).ToLocalTime();
        GUILayout.Space(8f);
        GUILayout.Label($"Assessment generated {generated:yyyy-MM-dd HH:mm:ss} from the active PMC profile.");
    }

    private void DrawReadinessHeader(HermesLoadoutSummaryResponse summary)
    {
        GUILayout.BeginHorizontal();
        DrawMetric("READINESS", summary.Readiness, $"Score: {summary.ReadinessScore}/100");
        DrawMetric("CRITICAL", summary.CriticalCount.ToString("N0"), "Must-fix loadout problems.");
        DrawMetric("ADVISORIES", summary.WarningCount.ToString("N0"), "Recommended review before raid.");
        DrawMetric("EQUIPPED SLOTS", summary.EquippedSlots.Count.ToString("N0"), "Top-level equipment items.");
        GUILayout.EndHorizontal();

        if (Plugin.Settings.ShowReadinessScoreBar.Value)
        {
            DrawProgressBar(summary.ReadinessScore / 100f, $"READINESS SCORE  {summary.ReadinessScore}/100  •  {summary.Readiness}");
        }
    }

    private void DrawOverview(HermesLoadoutSummaryResponse summary)
    {
        DrawVitals(summary.Vitals);
        GUILayout.Space(8f);
        GUILayout.Label("CURRENT READINESS FINDINGS");
        GUILayout.BeginHorizontal();
        DrawToggleButton("Critical", ref _showCriticalWarnings, 95f);
        DrawToggleButton("Advisories", ref _showAdvisoryWarnings, 105f);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        DrawWarningGroups(summary.Warnings);

        GUILayout.Space(8f);
        DrawActiveSettings();

        if (Plugin.Settings.HideEmptyLoadoutSections.Value && summary.EquippedSlots.Count == 0)
        {
            return;
        }

        GUILayout.Space(8f);
        GUILayout.Label("EQUIPPED ITEMS");
        if (summary.EquippedSlots.Count == 0)
        {
            GUILayout.Label("No top-level equipment items were found.");
        }
        else
        {
            var visibleSlots = HermesUi.LimitRows(summary.EquippedSlots, out var hiddenSlots);
            foreach (var slot in visibleSlots)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{slot.SlotName}: {slot.ItemName}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(slot.Status.ToUpperInvariant());
                GUILayout.EndHorizontal();
                DrawProgressBar(slot.ConditionPercent / 100f, slot.ConditionDescription);
                if (slot.ChildItemCount > 0)
                {
                    GUILayout.Label($"Installed or contained child items: {slot.ChildItemCount:N0}");
                }
                GUILayout.EndVertical();
            }
            HermesUi.DrawHiddenRowsNotice(hiddenSlots);
        }
    }

    private static void DrawVitals(HermesVitalsSummary vitals)
    {
        GUILayout.Label("PMC VITALS");
        GUILayout.BeginHorizontal();
        DrawMetric(
            "HEALTH",
            $"{vitals.HealthPercent}%",
            $"{FormatNumber(vitals.CurrentHealth)} / {FormatNumber(vitals.MaximumHealth)}");
        DrawMetric(
            "HYDRATION",
            $"{vitals.HydrationPercent}%",
            $"{FormatNumber(vitals.CurrentHydration)} / {FormatNumber(vitals.MaximumHydration)}");
        DrawMetric(
            "ENERGY",
            $"{vitals.EnergyPercent}%",
            $"{FormatNumber(vitals.CurrentEnergy)} / {FormatNumber(vitals.MaximumEnergy)}");
        GUILayout.EndHorizontal();
    }

    private static void DrawWeapons(
        IReadOnlyList<HermesWeaponReadiness> weapons,
        HermesLoadoutRequestSettings settings)
    {
        GUILayout.Label("WEAPONS AND AMMUNITION");
        if (weapons.Count == 0)
        {
            if (!Plugin.Settings.HideEmptyLoadoutSections.Value)
            {
                GUILayout.Label("No equipped firearm was detected.");
            }
            return;
        }

        GUILayout.BeginHorizontal();
        DrawMetric("FIREARMS", weapons.Count.ToString("N0"), "Equipped primary, secondary, and holster weapons.");
        DrawMetric("LOADED", FormatNumber(weapons.Sum(weapon => weapon.LoadedRounds)), $"Minimum per weapon: {settings.MinimumLoadedRounds:N0}");
        DrawMetric("SPARE MAGS", weapons.Sum(weapon => weapon.CompatibleSpareMagazineCount).ToString("N0"), $"Minimum per weapon: {settings.MinimumSpareMagazines:N0}");
        DrawMetric("SPARE ROUNDS", FormatNumber(weapons.Sum(weapon => weapon.SpareMagazineRounds + weapon.LooseCompatibleRounds)), $"Minimum per weapon: {settings.MinimumSpareRounds:N0}");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        var visibleWeapons = HermesUi.LimitRows(weapons, out var hiddenWeapons);
        foreach (var weapon in visibleWeapons)
        {
            var spareRounds = weapon.SpareMagazineRounds + weapon.LooseCompatibleRounds;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{weapon.SlotName}: {weapon.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label(weapon.Status.ToUpperInvariant());
            GUILayout.EndHorizontal();
            GUILayout.Label($"{weapon.Caliber} • {weapon.DurabilityDescription}");
            DrawProgressBar(weapon.DurabilityPercent / 100f, $"DURABILITY  {weapon.DurabilityPercent}%");

            GUILayout.BeginHorizontal();
            DrawMetric("IN WEAPON", FormatNumber(weapon.LoadedRounds), weapon.MagazineName ?? "No magazine detected");
            DrawMetric("SPARE MAGS", weapon.CompatibleSpareMagazineCount.ToString("N0"), $"Rounds in spares: {FormatNumber(weapon.SpareMagazineRounds)}");
            DrawMetric("LOOSE ROUNDS", FormatNumber(weapon.LooseCompatibleRounds), $"Compatible reserve total: {FormatNumber(spareRounds)}");
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(weapon.LoadedAmmoName))
            {
                GUILayout.Label($"Loaded ammunition: {weapon.LoadedAmmoName}{(weapon.HasMixedAmmo ? " • MIXED" : string.Empty)}");
            }

            foreach (var warning in weapon.Warnings)
            {
                DrawNotice("!", warning);
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenWeapons);
    }

    private static void DrawArmor(
        IReadOnlyList<HermesArmorReadiness> armorItems,
        HermesLoadoutRequestSettings settings)
    {
        GUILayout.Label("ARMOR AND INSERTS");
        if (armorItems.Count == 0)
        {
            if (!Plugin.Settings.HideEmptyLoadoutSections.Value)
            {
                GUILayout.Label("No equipped armor was detected.");
            }
            return;
        }

        GUILayout.BeginHorizontal();
        DrawMetric("ARMOR ITEMS", armorItems.Count.ToString("N0"), "Equipped armor, armored rigs, and armored headwear.");
        DrawMetric("PLATE SLOTS", armorItems.Sum(item => item.ArmorInsertSlotCount).ToString("N0"), $"Installed: {armorItems.Sum(item => item.InstalledArmorInsertCount):N0}");
        DrawMetric("REQUIRED EMPTY", armorItems.Sum(item => item.MissingRequiredArmorInsertCount).ToString("N0"), "Missing required inserts.");
        DrawMetric("MIN CONDITION", $"{settings.MinimumArmorDurabilityPercent}%", "Configured durability threshold.");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        var visibleArmor = HermesUi.LimitRows(armorItems, out var hiddenArmor);
        foreach (var armor in visibleArmor)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{armor.SlotName}: {armor.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label(armor.Status.ToUpperInvariant());
            GUILayout.EndHorizontal();
            GUILayout.Label($"Highest installed armor class: {armor.MaximumArmorClass:N0} • {armor.ConditionDescription}");
            DrawProgressBar(armor.ConditionPercent / 100f, $"CONDITION  {armor.ConditionPercent}%");

            if (armor.ArmorInsertSlotCount > 0)
            {
                var filledRatio = armor.ArmorInsertSlotCount <= 0
                    ? 0f
                    : armor.InstalledArmorInsertCount / (float)armor.ArmorInsertSlotCount;
                DrawProgressBar(
                    filledRatio,
                    $"INSERTS  {armor.InstalledArmorInsertCount:N0}/{armor.ArmorInsertSlotCount:N0} installed • {armor.MissingRequiredArmorInsertCount:N0} required empty • {armor.EmptyOptionalArmorInsertCount:N0} optional empty");
            }
            else
            {
                GUILayout.Label("No removable armor-insert slots were detected.");
            }

            foreach (var warning in armor.Warnings)
            {
                DrawNotice("!", warning);
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenArmor);
    }

    private static void DrawMedical(
        HermesMedicalReadiness medical,
        HermesLoadoutRequestSettings settings)
    {
        GUILayout.Label("MEDICAL AND SUSTAINMENT COVERAGE");
        GUILayout.BeginHorizontal();
        DrawMetric("MED ITEMS", medical.MedicalItemCount.ToString("N0"), $"Healing: {FormatNumber(medical.TotalHealingResource)}/{settings.MinimumHealingResource:N0}");
        DrawMetric("BLEEDING", Coverage(medical.HasLightBleedTreatment, medical.HasHeavyBleedTreatment), "Light / heavy bleed treatment");
        DrawMetric("TRAUMA", Coverage(medical.HasFractureTreatment, medical.HasSurgeryKit), "Fracture / surgery coverage");
        DrawMetric("PAIN", medical.HasPainTreatment ? "Covered" : "Missing", "Pain treatment");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        DrawMetric("HYDRATION", medical.HydrationProvisionCount.ToString("N0"), settings.RequireHydrationProvision ? "Required by current settings." : "Optional by current settings.");
        DrawMetric("ENERGY", medical.EnergyProvisionCount.ToString("N0"), settings.RequireEnergyProvision ? "Required by current settings." : "Optional by current settings.");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("READINESS CHECKLIST");
        GUILayout.BeginVertical(GUI.skin.box);
        DrawCoverageRow("Healing resource", medical.TotalHealingResource >= settings.MinimumHealingResource, settings.MinimumHealingResource <= 0, $"{FormatNumber(medical.TotalHealingResource)} / {settings.MinimumHealingResource:N0}");
        DrawCoverageRow("Heavy-bleed treatment", medical.HasHeavyBleedTreatment, !settings.RequireHeavyBleedTreatment, settings.RequireHeavyBleedTreatment ? "Required" : "Optional");
        DrawCoverageRow("Light-bleed treatment", medical.HasLightBleedTreatment, !settings.RequireLightBleedTreatment, settings.RequireLightBleedTreatment ? "Required" : "Optional");
        DrawCoverageRow("Fracture treatment", medical.HasFractureTreatment, !settings.RequireFractureTreatment, settings.RequireFractureTreatment ? "Required" : "Optional");
        DrawCoverageRow("Pain treatment", medical.HasPainTreatment, !settings.RequirePainTreatment, settings.RequirePainTreatment ? "Required" : "Optional");
        DrawCoverageRow("Hydration provision", medical.HydrationProvisionCount > 0, !settings.RequireHydrationProvision, settings.RequireHydrationProvision ? "Required" : "Optional");
        DrawCoverageRow("Energy provision", medical.EnergyProvisionCount > 0, !settings.RequireEnergyProvision, settings.RequireEnergyProvision ? "Required" : "Optional");
        GUILayout.EndVertical();

        if (medical.Items.Count == 0 && Plugin.Settings.HideEmptyLoadoutSections.Value)
        {
            return;
        }

        GUILayout.Space(8f);
        GUILayout.Label("CARRIED MEDICAL ITEMS");
        if (medical.Items.Count == 0)
        {
            GUILayout.Label("No medical items were detected in equipped containers.");
            return;
        }

        var visibleItems = HermesUi.LimitRows(medical.Items, out var hiddenMedicalItems);
        foreach (var item in visibleItems)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(item.Name);
            GUILayout.FlexibleSpace();
            GUILayout.Label(item.Coverage);
            GUILayout.EndHorizontal();
            if (item.MaximumResource > 0d)
            {
                DrawProgressBar(
                    item.MaximumResource <= 0d ? 0f : (float)(item.CurrentResource / item.MaximumResource),
                    $"RESOURCE  {FormatNumber(item.CurrentResource)} / {FormatNumber(item.MaximumResource)}");
            }
            GUILayout.EndVertical();
        }
        HermesUi.DrawHiddenRowsNotice(hiddenMedicalItems);
    }

    private void DrawQuestRequirements(IReadOnlyList<HermesQuestLoadoutRequirement> requirements)
    {
        GUILayout.Label("ACTIVE QUEST RAID GEAR");
        var visibleRequirements = requirements
            .Where(requirement => Plugin.Settings.RaidPlannerShowInferredRouteKeys.Value
                                  || !IsInferredRouteKey(requirement.RequirementKind, requirement.Note))
            .Where(requirement => Plugin.Settings.RaidPlannerShowAcquireInRaidItems.Value
                                  || !requirement.AcquireInRaid)
            .ToList();
        if (visibleRequirements.Count == 0)
        {
            GUILayout.Label("No explicit active-quest equipment, raid-item, marker, plant-item, or carried turn-in requirements were identified.");
            GUILayout.Label("HERMES also adds known and inferred route-key requirements when quest data omits the locked-door access item.");
            return;
        }

        var criticalMissing = visibleRequirements.Count(requirement => requirement.IsRaidCritical && !requirement.AcquireInRaid && !requirement.IsSatisfied);
        var criticalReady = visibleRequirements.Count(requirement => requirement.IsRaidCritical && !requirement.AcquireInRaid && requirement.IsSatisfied);
        var acquireInRaid = visibleRequirements.Count(requirement => requirement.AcquireInRaid);
        var maps = visibleRequirements
            .Select(requirement => requirement.MapName)
            .Where(map => !string.IsNullOrWhiteSpace(map))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        GUILayout.BeginHorizontal();
        DrawMetric("MISSING RAID GEAR", criticalMissing.ToString("N0"), "Missing equipment or carried items needed for raid objectives.");
        DrawMetric("READY", criticalReady.ToString("N0"), "Raid-critical quest requirements currently satisfied.");
        DrawMetric("ACQUIRE IN RAID", acquireInRaid.ToString("N0"), "Required route items obtained after deployment; not missing pre-raid gear.");
        DrawMetric("MAPS", maps.ToString("N0"), "Maps represented by active quest requirements.");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        foreach (var mapGroup in visibleRequirements
                     .GroupBy(requirement => string.IsNullOrWhiteSpace(requirement.MapName) ? "Any map" : requirement.MapName)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            GUILayout.Label(mapGroup.Key.ToUpperInvariant());
            foreach (var questGroup in mapGroup
                         .GroupBy(requirement => new { requirement.QuestName, requirement.TraderName })
                         .OrderBy(group => group.Key.QuestName, StringComparer.OrdinalIgnoreCase))
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{questGroup.Key.QuestName} — {questGroup.Key.TraderName}");
                GUILayout.FlexibleSpace();
                var missingForQuest = questGroup.Count(requirement => requirement.IsRaidCritical && !requirement.AcquireInRaid && !requirement.IsSatisfied);
                GUILayout.Label(missingForQuest > 0 ? $"{missingForQuest:N0} missing" : "Checked");
                GUILayout.EndHorizontal();

                foreach (var requirement in questGroup
                             .OrderByDescending(value => value.IsRaidCritical && !value.IsSatisfied)
                             .ThenBy(value => value.RequirementKind, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(value => value.RequiredEquipment, StringComparer.OrdinalIgnoreCase))
                {
                    GUILayout.Space(3f);
                    GUILayout.BeginHorizontal();
                    var requirementIcon = requirement.AcquireInRaid
                        ? "↳"
                        : requirement.IsSatisfied
                            ? "✓"
                            : requirement.IsRaidCritical
                                ? "✗"
                                : "•";
                    GUILayout.Label($"{requirementIcon} {requirement.RequirementKind}: {requirement.RequiredEquipment}");
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(requirement.ConditionType);
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"Required: {FormatNumber(requirement.RequiredQuantity)} • carried: {FormatNumber(requirement.CarriedQuantity)}");
                    if (requirement.FoundInRaidRequired || requirement.FoundInRaidCarriedQuantity > 0d)
                    {
                        GUILayout.Label($"Found in raid carried: {FormatNumber(requirement.FoundInRaidCarriedQuantity)}{(requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)}");
                    }

                    if (requirement.AcquireInRaid && !requirement.IsCompleted)
                    {
                        GUILayout.Label("Acquire during raid — this does not count as missing pre-raid gear.");
                    }
                    else if (!requirement.IsRaidCritical && !requirement.IsCompleted)
                    {
                        GUILayout.Label("Information only — this item is not treated as mandatory raid gear.");
                    }

                    GUILayout.Label(requirement.Note);
                }

                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }
        }
    }

    private void DrawRaidPlans(HermesLoadoutSummaryResponse summary)
    {
        GUILayout.Label("MAP-BASED RAID PLANNER");
        if (summary.RaidPlans.Count == 0)
        {
            GUILayout.Label("No active quest objectives were available for raid planning.");
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Map search", GUILayout.Width(78f));
        _raidSearch = GUILayout.TextField(_raidSearch, GUILayout.MinWidth(180f));
        DrawStatusFilterButton("All maps", 80f);
        DrawStatusFilterButton("Prepared", 85f);
        DrawStatusFilterButton("Missing gear", 100f);
        DrawStatusFilterButton("Incomplete", 90f);
        if (GUILayout.Button($"Sort: {_raidSorting}", GUILayout.Width(190f)))
        {
            _raidSorting = NextRaidSorting(_raidSorting);
        }
        if (GUILayout.Button("Clear", GUILayout.Width(62f)))
        {
            _raidSearch = string.Empty;
            _raidStatusFilter = "All maps";
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        var plans = summary.RaidPlans
            .Where(plan => string.IsNullOrWhiteSpace(_raidSearch)
                           || plan.MapName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)
                           || plan.Quests.Any(quest => quest.QuestName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)
                                                      || quest.TraderName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)))
            .Where(MatchesRaidStatusFilter)
            .ToList();
        plans = SortRaidPlans(plans, _raidSorting).ToList();

        if (plans.Count == 0)
        {
            HermesUi.DrawEmptyState("No raid plans match the current map search and status filter.");
            return;
        }

        var prepared = plans.Count(plan => GetVisibleMissingRequirementCount(plan) == 0);
        var missing = plans.Sum(GetVisibleMissingRequirementCount);
        var objectives = plans.Sum(plan => VisibleObjectives(plan).Count());
        var activeQuests = plans.Sum(plan => plan.ActiveQuestCount);

        GUILayout.BeginHorizontal();
        DrawMetric("MAPS", plans.Count.ToString("N0"), "Locations matching the current planner filters.");
        DrawMetric("PREPARED", prepared.ToString("N0"), "Maps with all visible pre-raid requirements covered.");
        DrawMetric("MISSING GEAR", missing.ToString("N0"), "Unsatisfied visible pre-raid requirements.");
        DrawMetric("OBJECTIVES", objectives.ToString("N0"), $"Across {activeQuests:N0} active quest stage(s).");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        DrawRaidReadinessContext(summary.Warnings);

        var visiblePlans = HermesUi.LimitRows(plans, out var hiddenPlans);
        foreach (var plan in visiblePlans)
        {
            var requirements = VisiblePlanRequirements(plan).ToList();
            var visibleMissing = requirements.Count(requirement => !requirement.AcquireInRaid && !requirement.IsSatisfied);
            var visibleObjectives = VisibleObjectives(plan).ToList();
            var expanded = _raidPlanExpanded.GetValueOrDefault(plan.MapName, true);

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            var marker = expanded ? "▼" : "▶";
            if (GUILayout.Button($"{marker} {plan.MapName.ToUpperInvariant()}", GUILayout.Height(28f), GUILayout.ExpandWidth(true)))
            {
                expanded = !expanded;
                _raidPlanExpanded[plan.MapName] = expanded;
            }
            GUILayout.Label(visibleMissing > 0 ? $"MISSING {visibleMissing:N0}" : plan.Status.ToUpperInvariant(), GUILayout.Width(125f));
            GUILayout.EndHorizontal();
            GUILayout.Label($"Active quests: {plan.ActiveQuestCount:N0} • visible objectives: {visibleObjectives.Count:N0} • visible raid requirements: {requirements.Count:N0}");

            if (!expanded)
            {
                GUILayout.EndVertical();
                GUILayout.Space(5f);
                continue;
            }

            if (Plugin.Settings.RaidPlannerIncludeQuestGearRestrictions.Value)
            {
                GUILayout.Space(5f);
                GUILayout.Label("COMBINED RAID CHECKLIST");
                if (requirements.Count == 0)
                {
                    GUILayout.Label("No visible raid-critical item, equipment, route-key, or acquire-in-raid requirements are encoded for this map.");
                }
                else
                {
                    foreach (var group in requirements
                                 .GroupBy(ClassifyRaidRequirementGroup, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(group => RaidRequirementGroupRank(group.Key)))
                    {
                        GUILayout.Label(group.Key.ToUpperInvariant());
                        foreach (var requirement in group
                                     .OrderBy(requirement => requirement.IsSatisfied)
                                     .ThenBy(requirement => requirement.RequiredEquipment, StringComparer.OrdinalIgnoreCase))
                        {
                            DrawRaidRequirement(requirement);
                        }
                    }
                }
            }

            GUILayout.Space(5f);
            GUILayout.Label("QUEST OBJECTIVES");
            foreach (var quest in plan.Quests)
            {
                var questObjectives = quest.Objectives.Where(IsObjectiveVisible).ToList();
                if (questObjectives.Count == 0 && Plugin.Settings.HideEmptyLoadoutSections.Value)
                {
                    continue;
                }

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{quest.QuestName} — {quest.TraderName}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(quest.Status.ToUpperInvariant());
                GUILayout.EndHorizontal();
                GUILayout.Label($"Visible objectives: {questObjectives.Count:N0} • completed overall: {quest.CompletedObjectiveCount:N0}/{quest.ObjectiveCount:N0} • missing raid requirements: {quest.MissingRequirementCount:N0}");

                if (questObjectives.Count == 0)
                {
                    GUILayout.Label("All objective rows are hidden by the current HERMES settings.");
                }
                else
                {
                    foreach (var objective in questObjectives)
                    {
                        var icon = objective.IsCompleted ? "✓" : objective.IsRaidObjective ? "•" : "○";
                        GUILayout.Label($"{icon} {objective.Description}");
                        if (Plugin.Settings.ShowSectionDescriptions.Value)
                        {
                            GUILayout.Label($"   {objective.ConditionType} • {objective.Status}");
                        }
                    }
                }

                GUILayout.EndVertical();
            }

            if (Plugin.Settings.RaidPlannerShowPlanNotes.Value && plan.Notes.Count > 0)
            {
                GUILayout.Space(5f);
                GUILayout.Label("PLAN NOTES");
                foreach (var note in plan.Notes)
                {
                    GUILayout.Label($"• {note}");
                }
            }

            GUILayout.EndVertical();
            GUILayout.Space(6f);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenPlans);
    }

    private void DrawValueAndInsurance(HermesLoadoutValueSummary value)
    {
        GUILayout.Label("LOADOUT VALUE AND INSURANCE");
        if (!value.Found)
        {
            GUILayout.Label(value.Message ?? "Loadout valuation is unavailable.");
            return;
        }

        GUILayout.BeginHorizontal();
        DrawMetric("AT-RISK VALUE", $"₽{value.AtRiskReplacementValue:N0}", $"{value.AtRiskItemCount:N0} carried item instance(s).");
        if (Plugin.Settings.ShowProtectedSlotValue.Value)
        {
            DrawMetric("PROTECTED VALUE", $"₽{value.ProtectedReplacementValue:N0}", $"{value.ProtectedItemCount:N0} protected item instance(s).");
        }
        DrawMetric("UNINSURED VALUE", $"₽{value.UninsuredReplacementValue:N0}", $"{value.UninsuredItemCount:N0} uninsured insurable item(s).");
        var insuranceNote = !Plugin.Settings.ShowInsuranceCostEstimate.Value
            ? "Cost estimate hidden by settings."
            : value.EstimatedInsuranceCost.HasValue
                ? $"Estimated cost: ₽{value.EstimatedInsuranceCost.Value:N0}"
                : "Cost estimate unavailable.";
        DrawMetric("INSURANCE", value.InsuranceStatus, insuranceNote);
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        DrawMetric("TRADER LIQUIDATION", $"₽{value.TraderLiquidationValue:N0}", "Condition-adjusted exact carried instances.");
        DrawMetric("MARKET REPLACEMENT", $"₽{value.MarketReplacementValue:N0}", "Shared flea-price fallback order.");
        DrawMetric("BEST REPLACEMENT", $"₽{value.BestReplacementValue:N0}", "Cheapest supported trader or market source.");
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("CATEGORY BREAKDOWN");
        foreach (var category in value.Categories)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(category.Category);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{category.ItemCount:N0} item(s)");
            GUILayout.EndHorizontal();
            GUILayout.Label($"Trader liquidation: ₽{category.TraderLiquidationValue:N0} • market replacement: ₽{category.MarketReplacementValue:N0}");
            GUILayout.Label($"Best replacement: ₽{category.BestReplacementValue:N0} • at risk: ₽{category.AtRiskReplacementValue:N0} • uninsured: ₽{category.UninsuredReplacementValue:N0}");
            GUILayout.EndVertical();
        }

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("CARRIED ITEM INSTANCES");
        GUILayout.FlexibleSpace();
        DrawToggleButton("Uninsured only", ref _showOnlyUninsuredItems, 120f);
        GUILayout.EndHorizontal();
        var displayedValueItems = value.Items
            .Where(item => Plugin.Settings.ShowProtectedSlotValue.Value || !item.IsProtected)
            .Where(item => !_showOnlyUninsuredItems || item.InsuranceStatus.Equals("Uninsured", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (displayedValueItems.Count == 0)
        {
            GUILayout.Label("No supported carried item instances were returned.");
        }
        else
        {
            var visibleItems = HermesUi.LimitRows(displayedValueItems, out var hiddenItems);
            foreach (var item in visibleItems)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{item.Name} — {item.SlotName}");
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrWhiteSpace(item.ProfileItemId)
                    && GUILayout.Button("Ask HERMES", GUILayout.Width(105f)))
                {
                    Plugin.Instance?.OpenForInventoryItem(item.ProfileItemId);
                }
                GUILayout.EndHorizontal();
                GUILayout.Label($"{item.Category} • quantity {FormatNumber(item.Quantity)} • {item.ConditionDescription}");
                GUILayout.Label($"Best replacement: {(item.BestReplacementValue.HasValue ? $"₽{item.BestReplacementValue.Value:N0}" : "Unavailable")} — {item.BestReplacementSource}");
                GUILayout.Label($"Trader liquidation: {(item.TraderLiquidationValue.HasValue ? $"₽{item.TraderLiquidationValue.Value:N0}" : "Unavailable")} • insurance: {item.InsuranceStatus}{(item.IsHighValueUninsured ? " • HIGH-VALUE UNINSURED" : string.Empty)}");
                GUILayout.EndVertical();
            }
            HermesUi.DrawHiddenRowsNotice(hiddenItems);
        }

        if (value.Notes.Count > 0)
        {
            GUILayout.Space(8f);
            GUILayout.Label("VALUATION NOTES");
            foreach (var note in value.Notes)
            {
                GUILayout.Label("• " + note);
            }
        }
    }

    private void DrawStatusFilterButton(string label, float width)
    {
        var selected = _raidStatusFilter.Equals(label, StringComparison.OrdinalIgnoreCase);
        if (GUILayout.Button((selected ? "● " : string.Empty) + label, GUILayout.Width(width)))
        {
            _raidStatusFilter = label;
        }
    }

    private bool MatchesRaidStatusFilter(HermesRaidPlanSummary plan)
    {
        return _raidStatusFilter switch
        {
            "Prepared" => GetVisibleMissingRequirementCount(plan) == 0,
            "Missing gear" => GetVisibleMissingRequirementCount(plan) > 0,
            "Incomplete" => plan.CompletedObjectiveCount < plan.ObjectiveCount,
            _ => true
        };
    }

    private IEnumerable<HermesRaidPlanSummary> SortRaidPlans(
        IEnumerable<HermesRaidPlanSummary> plans,
        string sorting)
    {
        return sorting switch
        {
            "Most active quests" => plans
                .OrderByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(plan => GetVisibleMissingRequirementCount(plan))
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Most incomplete objectives" => plans
                .OrderByDescending(plan => Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount))
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Fewest missing requirements" => plans
                .OrderBy(plan => GetVisibleMissingRequirementCount(plan))
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Alphabetical" => plans.OrderBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            _ => plans
                .OrderBy(plan => GetVisibleMissingRequirementCount(plan) > 0 ? 1 : 0)
                .ThenBy(plan => GetVisibleMissingRequirementCount(plan))
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenByDescending(plan => Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount))
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string NextRaidSorting(string current)
    {
        return current switch
        {
            "Best prepared" => "Most active quests",
            "Most active quests" => "Most incomplete objectives",
            "Most incomplete objectives" => "Fewest missing requirements",
            "Fewest missing requirements" => "Alphabetical",
            _ => "Best prepared"
        };
    }

    private IEnumerable<HermesRaidPlanRequirement> VisiblePlanRequirements(HermesRaidPlanSummary plan)
    {
        if (!Plugin.Settings.RaidPlannerIncludeQuestGearRestrictions.Value)
        {
            return [];
        }

        return plan.CombinedRequirements
            .Where(requirement => Plugin.Settings.RaidPlannerShowInferredRouteKeys.Value
                                  || !IsInferredRouteKey(requirement.RequirementKind, requirement.Note))
            .Where(requirement => Plugin.Settings.RaidPlannerShowAcquireInRaidItems.Value
                                  || !requirement.AcquireInRaid);
    }

    private int GetVisibleMissingRequirementCount(HermesRaidPlanSummary plan)
    {
        return VisiblePlanRequirements(plan)
            .Count(requirement => !requirement.AcquireInRaid && !requirement.IsSatisfied);
    }

    private IEnumerable<HermesRaidPlanObjective> VisibleObjectives(HermesRaidPlanSummary plan)
    {
        return plan.Quests.SelectMany(quest => quest.Objectives).Where(IsObjectiveVisible);
    }

    private bool IsObjectiveVisible(HermesRaidPlanObjective objective)
    {
        if (!Plugin.Settings.ShowCompletedQuestObjectives.Value && objective.IsCompleted)
        {
            return false;
        }

        if (!IsHandoverObjective(objective))
        {
            return true;
        }

        if (!Plugin.Settings.RaidPlannerShowHandoverObjectives.Value)
        {
            return false;
        }

        return Plugin.Settings.RaidPlannerShowFirHandoverObjectives.Value
               || !IsFirHandoverObjective(objective);
    }

    private static bool IsHandoverObjective(HermesRaidPlanObjective objective)
    {
        return objective.ConditionType.Contains("Handover", StringComparison.OrdinalIgnoreCase)
               || objective.Description.Contains("hand over", StringComparison.OrdinalIgnoreCase)
               || objective.Description.Contains("handover", StringComparison.OrdinalIgnoreCase)
               || objective.Description.Contains("turn in", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFirHandoverObjective(HermesRaidPlanObjective objective)
    {
        return objective.Description.Contains("found in raid", StringComparison.OrdinalIgnoreCase)
               || objective.Description.Contains("FIR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInferredRouteKey(string requirementKind, string note)
    {
        return requirementKind.Contains("Inferred route key", StringComparison.OrdinalIgnoreCase)
               || note.Contains("Inferred from", StringComparison.OrdinalIgnoreCase)
               || note.Contains("inferred route key", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyRaidRequirementGroup(HermesRaidPlanRequirement requirement)
    {
        if (requirement.AcquireInRaid)
        {
            return "Acquire during raid";
        }

        if (requirement.RequirementKind.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return "Route keys";
        }

        if (requirement.RequirementKind.Contains("equipment", StringComparison.OrdinalIgnoreCase)
            || requirement.RequirementKind.Contains("weapon", StringComparison.OrdinalIgnoreCase)
            || requirement.RequirementKind.Contains("wear", StringComparison.OrdinalIgnoreCase))
        {
            return "Equip before raid";
        }

        return "Bring from stash";
    }

    private static int RaidRequirementGroupRank(string group)
    {
        return group switch
        {
            "Equip before raid" => 0,
            "Bring from stash" => 1,
            "Route keys" => 2,
            "Acquire during raid" => 3,
            _ => 4
        };
    }

    private static void DrawRaidRequirement(HermesRaidPlanRequirement requirement)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        var icon = requirement.AcquireInRaid
            ? requirement.CarriedQuantity >= requirement.RequiredQuantity ? "✓" : "↳"
            : requirement.IsSatisfied ? "✓" : "✗";
        GUILayout.Label($"{icon} {requirement.RequirementKind}: {requirement.RequiredEquipment}");
        GUILayout.FlexibleSpace();
        GUILayout.Label(requirement.AcquireInRaid
            ? requirement.CarriedQuantity >= requirement.RequiredQuantity ? "ACQUIRED" : "ACQUIRE IN RAID"
            : requirement.IsSatisfied ? "COVERED" : $"MISSING {FormatNumber(requirement.MissingQuantity)}");
        GUILayout.EndHorizontal();
        GUILayout.Label($"Required: {FormatNumber(requirement.RequiredQuantity)} • carried: {FormatNumber(requirement.CarriedQuantity)}");
        if (requirement.FoundInRaidRequired || requirement.FoundInRaidCarriedQuantity > 0d)
        {
            GUILayout.Label($"Found in raid carried: {FormatNumber(requirement.FoundInRaidCarriedQuantity)}{(requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)}");
        }
        GUILayout.Label($"Quests: {string.Join(", ", requirement.QuestNames)}");
        GUILayout.Label(requirement.Note);
        GUILayout.EndVertical();
    }

    private static void DrawRaidReadinessContext(IReadOnlyList<HermesLoadoutWarning> warnings)
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Plugin.Settings.RaidPlannerIncludeMedicalReadiness.Value)
        {
            categories.Add("Medical");
            categories.Add("Sustainment");
        }

        if (Plugin.Settings.RaidPlannerIncludeAmmunitionReadiness.Value)
        {
            categories.Add("Weapons");
            categories.Add("Ammunition");
        }

        if (Plugin.Settings.RaidPlannerIncludeInsuranceWarnings.Value)
        {
            categories.Add("Insurance");
        }

        if (categories.Count == 0)
        {
            return;
        }

        var contextWarnings = warnings
            .Where(warning => categories.Contains(warning.Category))
            .OrderBy(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(warning => warning.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(warning => warning.Message, StringComparer.OrdinalIgnoreCase)
            .ToList();

        GUILayout.Label("CURRENT LOADOUT CONTEXT");
        if (contextWarnings.Count == 0)
        {
            DrawNotice("✓", "No enabled medical, ammunition, weapon, sustainment, or insurance context warnings are active.");
        }
        else
        {
            var visibleWarnings = HermesUi.LimitRows(contextWarnings, out var hiddenWarnings);
            foreach (var warning in visibleWarnings)
            {
                DrawNotice(
                    warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? "✗" : "!",
                    $"{warning.Category}: {warning.Message}");
            }
            HermesUi.DrawHiddenRowsNotice(hiddenWarnings);
        }
        GUILayout.Space(8f);
    }

    private static void DrawToggleButton(string label, ref bool value, float width)
    {
        if (GUILayout.Button($"{(value ? "☑" : "☐")} {label}", GUILayout.Width(width)))
        {
            value = !value;
        }
    }

    private static void DrawCoverageRow(string label, bool covered, bool optional, string detail)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(covered ? "✓" : optional ? "○" : "✗", GUILayout.Width(20f));
        GUILayout.Label(label, GUILayout.Width(190f));
        GUILayout.Label(covered ? "Covered" : optional ? "Optional" : "Missing", GUILayout.Width(80f));
        GUILayout.Label(detail);
        GUILayout.EndHorizontal();
    }

    private static void DrawProgressBar(float value, string label)
    {
        var normalized = Mathf.Clamp01(value);
        var rect = GUILayoutUtility.GetRect(120f, 22f, GUILayout.ExpandWidth(true));
        GUI.Box(rect, string.Empty);
        if (normalized > 0f)
        {
            var fill = new Rect(rect.x + 2f, rect.y + 2f, Math.Max(0f, (rect.width - 4f) * normalized), rect.height - 4f);
            var previous = GUI.color;
            GUI.color = normalized >= 0.75f
                ? new Color(0.45f, 0.75f, 0.45f, 0.8f)
                : normalized >= 0.5f
                    ? new Color(0.85f, 0.72f, 0.35f, 0.8f)
                    : new Color(0.82f, 0.35f, 0.35f, 0.8f);
            GUI.Box(fill, string.Empty);
            GUI.color = previous;
        }

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold
        };
        GUI.Label(rect, label, style);
    }

    private static LoadoutView ParseLoadoutView(string value)
    {
        return value switch
        {
            "Weapons & Ammo" => LoadoutView.Weapons,
            "Armor" => LoadoutView.Armor,
            "Medical" => LoadoutView.Medical,
            "Quest Gear" => LoadoutView.Quests,
            "Raid Planner" => LoadoutView.RaidPlanner,
            "Value & Insurance" => LoadoutView.ValueInsurance,
            _ => LoadoutView.Overview
        };
    }

    private static string Enabled(bool value) => value ? "on" : "off";

    private void InitializeWarningGroups(IReadOnlyList<HermesLoadoutWarning> warnings)
    {
        var defaultExpanded = !Plugin.Settings.CollapseSectionsByDefault.Value
                              && !Plugin.Settings.CollapseWarningGroupsByDefault.Value;
        foreach (var category in warnings.Select(warning => warning.Category).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_warningGroupExpanded.ContainsKey(category))
            {
                _warningGroupExpanded[category] = defaultExpanded;
            }
        }
    }

    private void DrawWarningGroups(IReadOnlyList<HermesLoadoutWarning> warnings)
    {
        var filteredWarnings = warnings
            .Where(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
                ? _showCriticalWarnings
                : _showAdvisoryWarnings)
            .ToList();
        if (filteredWarnings.Count == 0)
        {
            DrawNotice("✓", "No critical or warning-level problems were detected in the current loadout.");
            return;
        }

        foreach (var group in filteredWarnings
                     .GroupBy(warning => warning.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Min(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var expanded = _warningGroupExpanded.GetValueOrDefault(
                group.Key,
                !Plugin.Settings.CollapseSectionsByDefault.Value
                && !Plugin.Settings.CollapseWarningGroupsByDefault.Value);
            var critical = group.Count(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            var warningCount = group.Count() - critical;
            var marker = expanded ? "▼" : "▶";
            if (GUILayout.Button(
                    $"{marker} {group.Key.ToUpperInvariant()} — {critical:N0} critical / {warningCount:N0} warning(s)",
                    GUILayout.Height(28f),
                    GUILayout.ExpandWidth(true)))
            {
                expanded = !expanded;
                _warningGroupExpanded[group.Key] = expanded;
            }

            if (!expanded)
            {
                continue;
            }

            var orderedWarnings = group
                .OrderBy(warning => warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(warning => warning.Message, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var visibleWarnings = HermesUi.LimitRows(orderedWarnings, out var hiddenWarnings);
            foreach (var warning in visibleWarnings)
            {
                DrawNotice(
                    warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? "✗" : "!",
                    $"{warning.Severity}\n{warning.Message}");
            }
            HermesUi.DrawHiddenRowsNotice(hiddenWarnings);
        }
    }

    private void DrawActiveSettings()
    {
        GUILayout.Label("ACTIVE READINESS SETTINGS");
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(
            $"Weapon durability ≥ {_lastRequestSettings.MinimumWeaponDurabilityPercent}% • "
            + $"armor durability ≥ {_lastRequestSettings.MinimumArmorDurabilityPercent}% • "
            + $"loaded rounds ≥ {_lastRequestSettings.MinimumLoadedRounds:N0} • "
            + $"spare magazines ≥ {_lastRequestSettings.MinimumSpareMagazines:N0} • "
            + $"spare rounds ≥ {_lastRequestSettings.MinimumSpareRounds:N0}");
        GUILayout.Label(
            $"Healing resource ≥ {_lastRequestSettings.MinimumHealingResource:N0} • "
            + $"hydration warning below {_lastRequestSettings.HydrationWarningPercent}% • "
            + $"energy warning below {_lastRequestSettings.EnergyWarningPercent}%");
        GUILayout.Label(
            $"Required treatment: heavy bleed {Enabled(_lastRequestSettings.RequireHeavyBleedTreatment)} • "
            + $"light bleed {Enabled(_lastRequestSettings.RequireLightBleedTreatment)} • "
            + $"fracture {Enabled(_lastRequestSettings.RequireFractureTreatment)} • "
            + $"pain {Enabled(_lastRequestSettings.RequirePainTreatment)}");
        GUILayout.Label(
            $"Required sustainment: hydration {Enabled(_lastRequestSettings.RequireHydrationProvision)} • "
            + $"energy {Enabled(_lastRequestSettings.RequireEnergyProvision)}");
        GUILayout.Label(
            $"Value analysis: {(_lastRequestSettings.IncludeValueAnalysis ? "Enabled" : "Disabled")} • "
            + $"insurance warnings: {(_lastRequestSettings.EnableInsuranceWarnings ? "Enabled" : "Disabled")} • "
            + $"uninsured threshold: ₽{_lastRequestSettings.HighValueUninsuredThreshold:N0}");
        GUILayout.Label(
            $"Automatic refresh: {(Plugin.Settings.GetAutomaticRefreshSeconds() > 0 ? $"{Plugin.Settings.GetAutomaticRefreshSeconds()}s" : "Disabled")} • "
            + "Change these options in the HERMES BepInEx/F12 configuration.");
        GUILayout.EndVertical();
    }

    private static string Coverage(bool first, bool second)
    {
        return first && second ? "Covered" : first || second ? "Partial" : "Missing";
    }

    private static void DrawNotice(string icon, string text)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"{icon} {text}");
        GUILayout.EndVertical();
    }

    private static void DrawMetric(string label, string value, string note)
    {
        HermesUi.DrawMetric(label, value, note, 150f);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }
}
