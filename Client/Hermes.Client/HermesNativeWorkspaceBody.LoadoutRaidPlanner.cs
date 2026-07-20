using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

internal sealed partial class HermesNativeWorkspaceBody
{
    #region Loadout and raid planner

    private void RenderLoadout(RectTransform parent, bool forceRaidPlanner)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.LoadoutStatus, _state.LoadoutLoading, _state.RefreshActive);
        var summary = _state.LoadoutSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.LoadoutLoading ? "Analyzing the active PMC equipment tree..." : "No loadout snapshot loaded.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Loadout data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        if (forceRaidPlanner)
        {
            _loadoutView = "RAID PLANNER";
        }
        else if (string.Equals(_loadoutView, "RAID PLANNER", StringComparison.Ordinal))
        {
            // Raid Planner is a separate top-level workspace. Never carry its internal
            // view state back into the Loadout workspace when the user switches tabs.
            _loadoutView = "OVERVIEW";
        }

        AddMetricGrid(root,
            ("READINESS", summary.Readiness),
            ("SCORE", $"{summary.ReadinessScore}/100"),
            ("CRITICAL", summary.CriticalCount.ToString("N0")),
            ("WARNINGS", summary.WarningCount.ToString("N0")),
            ("AT RISK", Money(summary.ValueSummary.AtRiskReplacementValue)),
            ("UNINSURED", summary.ValueSummary.UninsuredItemCount.ToString("N0")));

        if (!forceRaidPlanner)
        {
            var tabs = CreateToolbar(root);
            foreach (var view in new[] { "OVERVIEW", "WEAPONS", "ARMOR", "MEDICAL", "QUESTS", "VALUE" })
            {
                var captured = view;
                AddButton(tabs, view, () =>
                {
                    _loadoutView = captured;
                    Invalidate();
                }, view.Length * 8f + 34f, true, string.Equals(_loadoutView, view, StringComparison.Ordinal));
            }
            AddFlexibleSpace(tabs);
        }

        var scroll = CreateScroll(root, forceRaidPlanner ? "raid-planner" : "loadout-content", true);
        switch (_loadoutView)
        {
            case "WEAPONS":
                RenderWeapons(scroll.Content, summary);
                break;
            case "ARMOR":
                RenderArmor(scroll.Content, summary);
                break;
            case "MEDICAL":
                RenderMedical(scroll.Content, summary);
                break;
            case "QUESTS":
                RenderLoadoutQuests(scroll.Content, summary);
                break;
            case "VALUE":
                RenderLoadoutValue(scroll.Content, summary.ValueSummary);
                break;
            case "RAID PLANNER":
                RenderRaidPlans(scroll.Content, summary);
                break;
            default:
                RenderLoadoutOverview(scroll.Content, summary);
                break;
        }
    }

    private void RenderLoadoutOverview(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "VITALS");
        AddMetricGrid(parent,
            ("HEALTH", $"{summary.Vitals.CurrentHealth:0}/{summary.Vitals.MaximumHealth:0} • {summary.Vitals.HealthPercent}%"),
            ("HYDRATION", $"{summary.Vitals.CurrentHydration:0}/{summary.Vitals.MaximumHydration:0} • {summary.Vitals.HydrationPercent}%"),
            ("ENERGY", $"{summary.Vitals.CurrentEnergy:0}/{summary.Vitals.MaximumEnergy:0} • {summary.Vitals.EnergyPercent}%"));

        AddSectionHeader(parent, "WARNINGS");
        if (summary.Warnings.Count == 0)
        {
            AddEmptyState(parent, "No loadout warnings.", string.Empty);
        }
        foreach (var warning in summary.Warnings)
        {
            AddCard(parent, warning.Category, warning.Message, warning.Severity);
        }

        AddSectionHeader(parent, "EQUIPPED SLOTS");
        foreach (var slot in summary.EquippedSlots)
        {
            AddCard(parent,
                slot.SlotName,
                $"{slot.ItemName} • {slot.ConditionPercent}% {slot.ConditionDescription} • {slot.ChildItemCount:N0} child item(s)",
                slot.Status,
                () => _state!.OpenNamedItem(slot.ItemName, "equipped loadout"));
        }
    }

    private void RenderWeapons(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "WEAPONS");
        foreach (var weapon in summary.Weapons)
        {
            var body = $"{weapon.SlotName} • {weapon.DurabilityPercent}% {weapon.DurabilityDescription} • {weapon.Caliber} • magazine {weapon.MagazineName ?? "none"} {FormatCount(weapon.LoadedRounds)}/{weapon.MagazineCapacity}"
                       + $" • spare mags {weapon.CompatibleSpareMagazineCount:N0} • spare rounds {FormatCount(weapon.SpareMagazineRounds)} • loose {FormatCount(weapon.LooseCompatibleRounds)}";
            var card = AddCard(
                parent,
                weapon.Name,
                body,
                weapon.Status,
                () => _state!.OpenNamedItem(weapon.Name, "equipped weapon"));
            foreach (var warning in weapon.Warnings)
            {
                AddText(card, $"• {warning}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }
    }

    private void RenderArmor(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "ARMOR");
        foreach (var armor in summary.Armor)
        {
            var card = AddCard(parent,
                armor.Name,
                $"{armor.SlotName} • class {armor.MaximumArmorClass} • {armor.ConditionPercent}% {armor.ConditionDescription} • inserts {armor.InstalledArmorInsertCount}/{armor.ArmorInsertSlotCount} • missing required {armor.MissingRequiredArmorInsertCount}",
                armor.Status,
                () => _state!.OpenNamedItem(armor.Name, "equipped armor"));
            foreach (var warning in armor.Warnings)
            {
                AddText(card, $"• {warning}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }
    }

    private void RenderMedical(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        var medical = summary.Medical;
        AddSectionHeader(parent, "MEDICAL COVERAGE");
        AddMetricGrid(parent,
            ("MED ITEMS", medical.MedicalItemCount.ToString("N0")),
            ("HEALING RESOURCE", FormatCount(medical.TotalHealingResource)),
            ("LIGHT BLEED", YesNo(medical.HasLightBleedTreatment)),
            ("HEAVY BLEED", YesNo(medical.HasHeavyBleedTreatment)),
            ("FRACTURE", YesNo(medical.HasFractureTreatment)),
            ("PAIN", YesNo(medical.HasPainTreatment)),
            ("SURGERY", YesNo(medical.HasSurgeryKit)),
            ("FOOD / WATER", $"{medical.EnergyProvisionCount}/{medical.HydrationProvisionCount}"));
        foreach (var item in medical.Items)
        {
            AddCard(
                parent,
                item.Name,
                $"{item.CurrentResource:0.##}/{item.MaximumResource:0.##}",
                item.Coverage,
                () => _state!.OpenNamedItem(item.Name, "carried medical"));
        }
    }

    private void RenderLoadoutQuests(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        AddSectionHeader(parent, "QUEST LOADOUT REQUIREMENTS");
        foreach (var requirement in summary.QuestRequirements)
        {
            AddCard(parent,
                requirement.QuestName,
                $"{requirement.MapName} • {requirement.RequiredEquipment} • carried {FormatCount(requirement.CarriedQuantity)}/{FormatCount(requirement.RequiredQuantity)}"
                + (requirement.FoundInRaidRequired ? $" • FIR carried {FormatCount(requirement.FoundInRaidCarriedQuantity)}" : string.Empty)
                + $" • {requirement.Note}",
                requirement.IsSatisfied ? "SATISFIED" : requirement.IsRaidCritical ? "CRITICAL" : "MISSING");
        }
    }

    private void RenderLoadoutValue(Transform parent, HermesLoadoutValueSummary value)
    {
        AddSectionHeader(parent, "VALUE & INSURANCE");
        AddMetricGrid(parent,
            ("TRADER LIQUIDATION", Money(value.TraderLiquidationValue)),
            ("MARKET REPLACEMENT", Money(value.MarketReplacementValue)),
            ("BEST REPLACEMENT", Money(value.BestReplacementValue)),
            ("AT RISK", Money(value.AtRiskReplacementValue)),
            ("PROTECTED", Money(value.ProtectedReplacementValue)),
            ("UNINSURED", Money(value.UninsuredReplacementValue)),
            ("INSURANCE COST", Money(value.EstimatedInsuranceCost)),
            ("STATUS", value.InsuranceStatus));

        foreach (var category in value.Categories)
        {
            AddCard(parent,
                category.Category,
                $"{category.ItemCount:N0} item(s) • liquidation {Money(category.TraderLiquidationValue)} • replacement {Money(category.BestReplacementValue)}",
                $"AT RISK {Money(category.AtRiskReplacementValue)}");
        }

        AddSectionHeader(parent, "EQUIPPED VALUE ITEMS");
        foreach (var item in value.Items.OrderByDescending(item => item.BestReplacementValue ?? 0L))
        {
            AddCard(parent,
                item.Name,
                $"{item.SlotName} • {item.Category} • {item.ConditionPercent}% {item.ConditionDescription} • liquidation {Money(item.TraderLiquidationValue)} • replacement {Money(item.BestReplacementValue)}",
                $"{item.InsuranceStatus}{(item.IsHighValueUninsured ? " • HIGH VALUE" : string.Empty)}",
                () => _state!.OpenLoadoutItem(item.ProfileItemId, item.Name));
        }
    }

    private void RenderRaidPlans(Transform parent, HermesLoadoutSummaryResponse summary)
    {
        var toolbar = CreateToolbar(parent);
        AddToolbarLabel(toolbar, "RAID PLANS");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER MAPS / QUESTS", _raidSearch, 260f);
        search.onEndEdit.AddListener(value =>
        {
            _raidSearch = value.Trim();
            Invalidate();
        });

        var plans = summary.RaidPlans
            .Where(plan => _raidSearch.Length == 0
                           || plan.MapName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)
                           || plan.Quests.Any(quest => quest.QuestName.Contains(_raidSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(plan => plan.MissingRequirementCount)
            .ThenByDescending(plan => plan.ActiveQuestCount)
            .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var plan in plans)
        {
            var expanded = _expandedRaidMaps.Contains(plan.MapName);
            var card = AddCard(parent,
                plan.MapName,
                $"{plan.ActiveQuestCount:N0} active quest(s) • objectives {plan.CompletedObjectiveCount}/{plan.ObjectiveCount} • missing requirements {plan.MissingRequirementCount}/{plan.RaidRequirementCount}",
                plan.Status,
                () =>
                {
                    if (!_expandedRaidMaps.Add(plan.MapName))
                    {
                        _expandedRaidMaps.Remove(plan.MapName);
                    }
                    Invalidate();
                },
                expanded ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            if (!expanded)
            {
                continue;
            }

            AddText(card, "REQUIRED GEAR", 13f, true, HermesNativeUiFramework.AccentTextColor);
            foreach (var requirement in plan.CombinedRequirements)
            {
                AddText(card,
                    $"• {(requirement.IsSatisfied ? "✓" : "!")} {requirement.RequiredEquipment}: carried {FormatCount(requirement.CarriedQuantity)}/{FormatCount(requirement.RequiredQuantity)} • missing {FormatCount(requirement.MissingQuantity)} • {requirement.Note}",
                    12f,
                    false,
                    requirement.IsSatisfied ? HermesNativeUiFramework.MutedTextColor : HermesNativeUiFramework.NormalTextColor);
            }

            AddText(card, "QUESTS", 13f, true, HermesNativeUiFramework.AccentTextColor);
            foreach (var quest in plan.Quests)
            {
                AddText(card, $"• {quest.QuestName} • {quest.TraderName} • {quest.Status}", 12f, true, HermesNativeUiFramework.NormalTextColor);
                foreach (var objective in quest.Objectives)
                {
                    AddText(card,
                        $"   {(objective.IsCompleted ? "✓" : "•")} {objective.Description} • {objective.Status}",
                        12f,
                        false,
                        HermesNativeUiFramework.MutedTextColor);
                }
            }
            foreach (var note in plan.Notes)
            {
                AddText(card, $"NOTE • {note}", 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }

        if (plans.Count == 0)
        {
            AddEmptyState(parent, "No raid plans match the filter.", string.Empty);
        }
    }

    #endregion
}
