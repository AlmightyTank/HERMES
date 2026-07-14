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
    private string _status = "Open this tab to inspect the active PMC raid loadout.";

    public void Draw()
    {
        if (!_requested && !_loading)
        {
            _requested = true;
            _ = RefreshFromServerAsync(false);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("LOADOUT READINESS, RAID PLANNING, VALUE, AND INSURANCE — ALPHA11.3");
        GUILayout.FlexibleSpace();
        GUI.enabled = !_loading;
        if (GUILayout.Button(_loading ? "Refreshing..." : "Refresh", GUILayout.Width(110f)))
        {
            _ = RefreshFromServerAsync(true);
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label("Exact readiness and raid-planning analysis with per-instance replacement value, trader liquidation, protected-slot risk, and insurance coverage.");
        GUILayout.Space(4f);
        GUILayout.Label(_status);
        GUILayout.Space(6f);

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
        _status = "Analyzing equipment, active objectives, ammunition, armor, treatment coverage, exact replacement value, raid risk, and insurance state...";

        try
        {
            var response = await HermesApiClient.GetLoadoutSummaryAsync();
            if (version != _requestVersion)
            {
                return;
            }

            _summary = response;
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
        _status = "Open this tab to inspect the active PMC raid loadout.";
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
        DrawViewButton("Value & Insurance", LoadoutView.ValueInsurance, 125f);
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
                DrawWeapons(summary.Weapons);
                break;
            case LoadoutView.Armor:
                DrawArmor(summary.Armor);
                break;
            case LoadoutView.Medical:
                DrawMedical(summary.Medical);
                break;
            case LoadoutView.Quests:
                DrawQuestRequirements(summary.QuestRequirements);
                break;
            case LoadoutView.RaidPlanner:
                DrawRaidPlans(summary.RaidPlans);
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

    private static void DrawReadinessHeader(HermesLoadoutSummaryResponse summary)
    {
        GUILayout.BeginHorizontal();
        DrawMetric("READINESS", summary.Readiness, $"Score: {summary.ReadinessScore}/100");
        DrawMetric("CRITICAL", summary.CriticalCount.ToString("N0"), "Must-fix loadout problems.");
        DrawMetric("WARNINGS", summary.WarningCount.ToString("N0"), "Recommended review before raid.");
        DrawMetric(
            "AT-RISK VALUE",
            summary.ValueSummary.Found ? $"₽{summary.ValueSummary.AtRiskReplacementValue:N0}" : "Unavailable",
            summary.ValueSummary.Found
                ? $"{summary.ValueSummary.UninsuredItemCount:N0} uninsured insurable item(s)."
                : "Open Value & Insurance for details.");
        GUILayout.EndHorizontal();
    }

    private static void DrawOverview(HermesLoadoutSummaryResponse summary)
    {
        DrawVitals(summary.Vitals);
        GUILayout.Space(8f);
        GUILayout.Label("CURRENT WARNINGS");
        if (summary.Warnings.Count == 0)
        {
            DrawNotice("✓", "No critical or warning-level problems were detected in the current loadout.");
        }
        else
        {
            foreach (var warning in summary.Warnings)
            {
                DrawNotice(
                    warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? "✗" : "!",
                    $"{warning.Severity} — {warning.Category}\n{warning.Message}");
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("LOADOUT VALUE AND INSURANCE");
        if (!summary.ValueSummary.Found)
        {
            GUILayout.Label(summary.ValueSummary.Message ?? "Loadout valuation is unavailable.");
        }
        else
        {
            GUILayout.BeginHorizontal();
            DrawMetric("RAID RISK", $"₽{summary.ValueSummary.AtRiskReplacementValue:N0}", "Best replacement value currently at risk.");
            DrawMetric("UNINSURED", $"₽{summary.ValueSummary.UninsuredReplacementValue:N0}", $"{summary.ValueSummary.UninsuredItemCount:N0} insurable item(s).");
            DrawMetric("PROTECTED", $"₽{summary.ValueSummary.ProtectedReplacementValue:N0}", "Secure and protected equipment slots.");
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8f);
        GUILayout.Label("EQUIPPED ITEMS");
        if (summary.EquippedSlots.Count == 0)
        {
            GUILayout.Label("No top-level equipment items were found.");
        }
        else
        {
            foreach (var slot in summary.EquippedSlots)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{slot.SlotName}: {slot.ItemName}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(slot.Status);
                GUILayout.EndHorizontal();
                GUILayout.Label(slot.ConditionDescription);
                if (slot.ChildItemCount > 0)
                {
                    GUILayout.Label($"Installed/contained child items: {slot.ChildItemCount:N0}");
                }
                GUILayout.EndVertical();
            }
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

    private static void DrawWeapons(IReadOnlyList<HermesWeaponReadiness> weapons)
    {
        GUILayout.Label("WEAPONS AND AMMUNITION");
        if (weapons.Count == 0)
        {
            GUILayout.Label("No equipped firearm was detected.");
            return;
        }

        foreach (var weapon in weapons)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{weapon.SlotName}: {weapon.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label(weapon.Status);
            GUILayout.EndHorizontal();
            GUILayout.Label($"{weapon.DurabilityDescription} • {weapon.Caliber}");
            GUILayout.Label(weapon.MagazineName is null
                ? "Magazine: none detected"
                : $"Magazine: {weapon.MagazineName} • capacity {weapon.MagazineCapacity:N0} • loaded in weapon {FormatNumber(weapon.LoadedRounds)} rounds");
            if (!string.IsNullOrWhiteSpace(weapon.LoadedAmmoName))
            {
                GUILayout.Label($"Loaded ammunition: {weapon.LoadedAmmoName}");
            }
            GUILayout.Label($"Compatible spare magazines: {weapon.CompatibleSpareMagazineCount:N0} • rounds in spares: {FormatNumber(weapon.SpareMagazineRounds)}");
            GUILayout.Label($"Loose compatible rounds carried: {FormatNumber(weapon.LooseCompatibleRounds)}");
            if (weapon.Warnings.Count > 0)
            {
                GUILayout.Space(3f);
                foreach (var warning in weapon.Warnings)
                {
                    GUILayout.Label($"! {warning}");
                }
            }
            GUILayout.EndVertical();
            GUILayout.Space(3f);
        }
    }

    private static void DrawArmor(IReadOnlyList<HermesArmorReadiness> armorItems)
    {
        GUILayout.Label("ARMOR AND INSERTS");
        if (armorItems.Count == 0)
        {
            GUILayout.Label("No equipped armor was detected.");
            return;
        }

        foreach (var armor in armorItems)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{armor.SlotName}: {armor.Name}");
            GUILayout.FlexibleSpace();
            GUILayout.Label(armor.Status);
            GUILayout.EndHorizontal();
            GUILayout.Label($"{armor.ConditionDescription} • highest installed armor class: {armor.MaximumArmorClass:N0}");
            if (armor.ArmorInsertSlotCount > 0)
            {
                GUILayout.Label($"Armor insert slots: {armor.InstalledArmorInsertCount:N0}/{armor.ArmorInsertSlotCount:N0} filled");
                GUILayout.Label($"Missing required: {armor.MissingRequiredArmorInsertCount:N0} • empty optional: {armor.EmptyOptionalArmorInsertCount:N0}");
            }
            else
            {
                GUILayout.Label("No removable armor-insert slots were detected.");
            }

            foreach (var warning in armor.Warnings)
            {
                GUILayout.Label($"! {warning}");
            }
            GUILayout.EndVertical();
            GUILayout.Space(3f);
        }
    }

    private static void DrawMedical(HermesMedicalReadiness medical)
    {
        GUILayout.Label("MEDICAL AND SUSTAINMENT COVERAGE");
        GUILayout.BeginHorizontal();
        DrawMetric("MED ITEMS", medical.MedicalItemCount.ToString("N0"), $"Healing resource: {FormatNumber(medical.TotalHealingResource)}");
        DrawMetric("BLEEDING", Coverage(medical.HasLightBleedTreatment, medical.HasHeavyBleedTreatment), "Light / heavy bleed treatment");
        DrawMetric("TRAUMA", Coverage(medical.HasFractureTreatment, medical.HasSurgeryKit), "Fracture / surgery coverage");
        DrawMetric("PAIN", medical.HasPainTreatment ? "Covered" : "Missing", "Pain treatment");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        DrawMetric("HYDRATION ITEMS", medical.HydrationProvisionCount.ToString("N0"), "Carried consumables with hydration effects.");
        DrawMetric("ENERGY ITEMS", medical.EnergyProvisionCount.ToString("N0"), "Carried consumables with energy effects.");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("CARRIED MEDICAL ITEMS");
        if (medical.Items.Count == 0)
        {
            GUILayout.Label("No medical items were detected in equipped containers.");
            return;
        }

        foreach (var item in medical.Items)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(item.Name);
            if (item.MaximumResource > 0d)
            {
                GUILayout.Label($"Resource: {FormatNumber(item.CurrentResource)} / {FormatNumber(item.MaximumResource)}");
            }
            GUILayout.Label($"Coverage: {item.Coverage}");
            GUILayout.EndVertical();
        }
    }

    private static void DrawValueAndInsurance(HermesLoadoutValueSummary value)
    {
        GUILayout.Label("LOADOUT VALUE AND INSURANCE");
        if (!value.Found)
        {
            GUILayout.Label(value.Message ?? "Loadout valuation is unavailable.");
            return;
        }

        GUILayout.BeginHorizontal();
        DrawMetric("AT-RISK VALUE", $"₽{value.AtRiskReplacementValue:N0}", $"{value.AtRiskItemCount:N0} carried item instance(s) at risk.");
        DrawMetric("BEST REPLACEMENT", $"₽{value.BestReplacementValue:N0}", "Cheapest supported trader or market source per item.");
        DrawMetric("TRADER LIQUIDATION", $"₽{value.TraderLiquidationValue:N0}", "Condition-adjusted current trader sale value.");
        DrawMetric("PROTECTED VALUE", $"₽{value.ProtectedReplacementValue:N0}", $"{value.ProtectedItemCount:N0} protected item instance(s).");
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        DrawMetric("INSURANCE", value.InsuranceStatus, $"{value.InsuredItemCount:N0} insured • {value.UninsuredItemCount:N0} uninsured.");
        DrawMetric("INSURED VALUE", $"₽{value.InsuredReplacementValue:N0}", "At-risk replacement value currently insured.");
        DrawMetric("UNINSURED VALUE", $"₽{value.UninsuredReplacementValue:N0}", "Insurable at-risk value without coverage.");
        DrawMetric(
            "EST. INSURANCE COST",
            value.EstimatedInsuranceCost.HasValue ? $"₽{value.EstimatedInsuranceCost.Value:N0}" : "Unavailable",
            value.InsuranceCostSource);
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("VALUE BY CATEGORY");
        if (value.Categories.Count == 0)
        {
            GUILayout.Label("No supported carried-item values were available.");
        }
        else
        {
            foreach (var category in value.Categories)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(category.Category);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{category.ItemCount:N0} item(s)");
                GUILayout.EndHorizontal();
                GUILayout.Label($"At risk: ₽{category.AtRiskReplacementValue:N0} • best replacement: ₽{category.BestReplacementValue:N0} • trader liquidation: ₽{category.TraderLiquidationValue:N0}");
                GUILayout.Label($"Market replacement: ₽{category.MarketReplacementValue:N0} • uninsured: ₽{category.UninsuredReplacementValue:N0}");
                GUILayout.EndVertical();
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("CARRIED ITEMS — ASK HERMES OR REVIEW VALUE");
        if (value.Items.Count == 0)
        {
            GUILayout.Label("No supported carried items were available.");
        }
        else
        {
            foreach (var item in value.Items)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{(item.IsHighValueUninsured ? "! " : string.Empty)}{item.Name}");
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrWhiteSpace(item.ProfileItemId)
                    && GUILayout.Button("Ask HERMES", GUILayout.Width(105f)))
                {
                    Plugin.Instance?.OpenForInventoryItem(item.ProfileItemId);
                }
                GUILayout.Label(item.InsuranceStatus, GUILayout.Width(90f));
                GUILayout.EndHorizontal();
                GUILayout.Label($"{item.Category} • {item.SlotName} • quantity {FormatNumber(item.Quantity)} • {item.ConditionDescription}");
                GUILayout.Label(item.BestReplacementValue.HasValue
                    ? $"Best replacement: ₽{item.BestReplacementValue.Value:N0} via {item.BestReplacementSource}"
                    : "Best replacement: unavailable");
                GUILayout.Label(item.TraderLiquidationValue.HasValue
                    ? $"Trader liquidation: ₽{item.TraderLiquidationValue.Value:N0} via {item.BestTraderName ?? "supported trader"}"
                    : "Trader liquidation: unavailable");
                GUILayout.Label(item.MarketReplacementValue.HasValue
                    ? $"Market replacement: ₽{item.MarketReplacementValue.Value:N0} ({item.MarketPriceSource})"
                    : "Market replacement: unavailable");
                if (item.TraderReplacementValue.HasValue)
                {
                    GUILayout.Label($"Trader replacement: ₽{item.TraderReplacementValue.Value:N0} ({item.TraderReplacementSource})");
                }

                if (item.IsProtected)
                {
                    GUILayout.Label("Protected slot — excluded from estimated raid-loss value.");
                }
                else if (!item.IsInsurable)
                {
                    GUILayout.Label("This carried item is treated as non-insurable.");
                }
                else if (item.IsHighValueUninsured)
                {
                    GUILayout.Label("High-value uninsured item — review insurance before raid.");
                }

                GUILayout.EndVertical();
                GUILayout.Space(3f);
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("VALUATION NOTES");
        foreach (var note in value.Notes)
        {
            GUILayout.Label($"• {note}");
        }
    }

    private static void DrawQuestRequirements(IReadOnlyList<HermesQuestLoadoutRequirement> requirements)
    {
        GUILayout.Label("ACTIVE QUEST RAID GEAR");
        if (requirements.Count == 0)
        {
            GUILayout.Label("No explicit active-quest equipment, raid-item, marker, plant-item, or carried turn-in requirements were identified.");
            GUILayout.Label("HERMES only warns about requirements encoded in the active quest data; it does not invent door-key requirements that the quest does not declare.");
            return;
        }

        var criticalMissing = requirements.Count(requirement => requirement.IsRaidCritical && !requirement.IsSatisfied);
        var criticalReady = requirements.Count(requirement => requirement.IsRaidCritical && requirement.IsSatisfied);
        var informational = requirements.Count(requirement => !requirement.IsRaidCritical);
        var maps = requirements
            .Select(requirement => requirement.MapName)
            .Where(map => !string.IsNullOrWhiteSpace(map))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        GUILayout.BeginHorizontal();
        DrawMetric("MISSING RAID GEAR", criticalMissing.ToString("N0"), "Missing equipment or carried items needed for raid objectives.");
        DrawMetric("READY", criticalReady.ToString("N0"), "Raid-critical quest requirements currently satisfied.");
        DrawMetric("TURN-IN / INFO", informational.ToString("N0"), "Tracked quest items that are not mandatory for this raid loadout.");
        DrawMetric("MAPS", maps.ToString("N0"), "Maps represented by active quest requirements.");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        foreach (var mapGroup in requirements
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
                var missingForQuest = questGroup.Count(requirement => requirement.IsRaidCritical && !requirement.IsSatisfied);
                GUILayout.Label(missingForQuest > 0 ? $"{missingForQuest:N0} missing" : "Checked");
                GUILayout.EndHorizontal();

                foreach (var requirement in questGroup
                             .OrderByDescending(value => value.IsRaidCritical && !value.IsSatisfied)
                             .ThenBy(value => value.RequirementKind, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(value => value.RequiredEquipment, StringComparer.OrdinalIgnoreCase))
                {
                    GUILayout.Space(3f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{(requirement.IsSatisfied ? "✓" : requirement.IsRaidCritical ? "✗" : "•")} {requirement.RequirementKind}: {requirement.RequiredEquipment}");
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(requirement.ConditionType);
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"Required: {FormatNumber(requirement.RequiredQuantity)} • carried: {FormatNumber(requirement.CarriedQuantity)}");
                    if (requirement.FoundInRaidRequired || requirement.FoundInRaidCarriedQuantity > 0d)
                    {
                        GUILayout.Label($"Found in raid carried: {FormatNumber(requirement.FoundInRaidCarriedQuantity)}{(requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)}");
                    }

                    if (!requirement.IsRaidCritical && !requirement.IsCompleted)
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

    private static void DrawRaidPlans(IReadOnlyList<HermesRaidPlanSummary> plans)
    {
        GUILayout.Label("MAP-BASED RAID PLANNER");
        if (plans.Count == 0)
        {
            GUILayout.Label("No active quest objectives were available for raid planning.");
            return;
        }

        var prepared = plans.Count(plan => plan.MissingRequirementCount == 0);
        var missing = plans.Sum(plan => plan.MissingRequirementCount);
        var objectives = plans.Sum(plan => plan.ObjectiveCount);
        var activeQuests = plans.Sum(plan => plan.ActiveQuestCount);

        GUILayout.BeginHorizontal();
        DrawMetric("MAPS", plans.Count.ToString("N0"), "Locations with active quest objectives.");
        DrawMetric("PREPARED", prepared.ToString("N0"), "Maps with all detectable raid gear covered.");
        DrawMetric("MISSING GEAR", missing.ToString("N0"), "Combined unsatisfied raid requirements.");
        DrawMetric("OBJECTIVES", objectives.ToString("N0"), $"Across {activeQuests:N0} active quests.");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        foreach (var plan in plans)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(plan.MapName.ToUpperInvariant());
            GUILayout.FlexibleSpace();
            GUILayout.Label(plan.Status);
            GUILayout.EndHorizontal();
            GUILayout.Label($"Active quests: {plan.ActiveQuestCount:N0} • objectives: {plan.CompletedObjectiveCount:N0}/{plan.ObjectiveCount:N0} complete • raid requirements: {plan.RaidRequirementCount:N0}");

            GUILayout.Space(5f);
            GUILayout.Label("COMBINED BRING / EQUIP CHECKLIST");
            if (plan.CombinedRequirements.Count == 0)
            {
                GUILayout.Label("No explicit raid-critical item or equipment requirements were encoded for this map.");
            }
            else
            {
                foreach (var requirement in plan.CombinedRequirements)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{(requirement.IsSatisfied ? "✓" : "✗")} {requirement.RequirementKind}: {requirement.RequiredEquipment}");
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(requirement.IsSatisfied ? "Covered" : $"Missing {FormatNumber(requirement.MissingQuantity)}");
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"Required: {FormatNumber(requirement.RequiredQuantity)} • carried: {FormatNumber(requirement.CarriedQuantity)}");
                    if (requirement.FoundInRaidRequired || requirement.FoundInRaidCarriedQuantity > 0d)
                    {
                        GUILayout.Label($"FIR carried: {FormatNumber(requirement.FoundInRaidCarriedQuantity)}{(requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)}");
                    }
                    GUILayout.Label($"Quests: {string.Join(", ", requirement.QuestNames)}");
                    GUILayout.Label(requirement.Note);
                    GUILayout.EndVertical();
                }
            }

            GUILayout.Space(5f);
            GUILayout.Label("QUEST OBJECTIVES");
            foreach (var quest in plan.Quests)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{quest.QuestName} — {quest.TraderName}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(quest.Status);
                GUILayout.EndHorizontal();
                GUILayout.Label($"Objectives: {quest.CompletedObjectiveCount:N0}/{quest.ObjectiveCount:N0} complete • missing raid requirements: {quest.MissingRequirementCount:N0}");

                if (quest.Objectives.Count == 0)
                {
                    GUILayout.Label("No finish-condition objective text was available.");
                }
                else
                {
                    foreach (var objective in quest.Objectives)
                    {
                        GUILayout.Label($"{(objective.IsCompleted ? "✓" : objective.IsRaidObjective ? "•" : "○")} {objective.ConditionType}: {objective.Description}");
                    }
                }

                GUILayout.EndVertical();
            }

            if (plan.Notes.Count > 0)
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
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(150f), GUILayout.ExpandWidth(true));
        GUILayout.Label(label);
        GUILayout.Label(value);
        GUILayout.Label(note);
        GUILayout.EndVertical();
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }
}
