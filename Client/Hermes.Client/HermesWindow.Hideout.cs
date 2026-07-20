using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Hideout And Quest Usage

    private void DrawHideoutUsageSection(HermesItemHideoutUsageResponse usage)
    {
        GUILayout.Space(8f);
        if (!usage.Found)
        {
            GUILayout.Label(usage.Message ?? "Quest, key, Hideout, and crafting usage is unavailable.");
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label($"PROFILE OWNERSHIP — {usage.OwnedQuantity:N0} total • {usage.OwnedFoundInRaidQuantity:N0} FIR");
        GUILayout.EndVertical();

        DrawQuestRequirementsSection(usage);
        DrawQuestKeysSection(usage);
        DrawHideoutCraftUsesSection(usage);
    }

    private void DrawQuestRequirementsSection(HermesItemHideoutUsageResponse usage)
    {
        var active = usage.QuestUses
            .Where(quest => quest.IsActive && !quest.ConditionCompleted && !quest.QuestCompleted)
            .OrderByDescending(quest => quest.Missing > 0d)
            .ThenBy(quest => quest.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remaining = usage.QuestUses.Count(quest => !quest.ConditionCompleted && !quest.QuestCompleted);
        var completed = usage.QuestUses.Count - remaining;
        var first = active.FirstOrDefault()
                    ?? usage.QuestUses.FirstOrDefault(quest => !quest.ConditionCompleted && !quest.QuestCompleted)
                    ?? usage.QuestUses.FirstOrDefault();

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _questRequirementsExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  QUEST REQUIREMENTS — {active.Count:N0} ACTIVE • {remaining:N0} REMAINING",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _questRequirementsExpanded = !_questRequirementsExpanded;
        }

        GUILayout.Label(first is null
            ? "No standard quest item requirement uses this item."
            : first.ConditionCompleted || first.QuestCompleted
                ? $"Completed use: {first.QuestName}."
                : $"{(first.IsActive ? "ACTIVE" : "FUTURE")}: {first.QuestName} — {first.ProgressText}");
        GUILayout.Label($"Owned: {usage.OwnedQuantity:N0} total • {usage.OwnedFoundInRaidQuantity:N0} FIR • Completed uses: {completed:N0}");

        if (_questRequirementsExpanded)
        {
            GUILayout.Space(6f);
            if (usage.QuestUses.Count == 0)
            {
                GUILayout.Label("No player-facing item requirement was found in standard quest completion conditions.");
            }
            else
            {
                foreach (var quest in usage.QuestUses)
                {
                    var marker = quest.ConditionCompleted ? "✓" : quest.IsActive ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {quest.QuestName} — {quest.TraderName}");
                    GUILayout.Label($"Status: {quest.QuestStatus} • Action: {quest.ConditionType}");
                    GUILayout.Label($"Required: {quest.Required:N0}{(quest.FoundInRaidRequired ? " FIR" : string.Empty)} • Owned matching targets: {quest.OwnedMatchingTargets:N0} • This item: {quest.OwnedSelectedItem:N0}");
                    GUILayout.Label(quest.ProgressText);
                    if (!quest.ConditionCompleted && quest.Missing > 0d)
                    {
                        GUILayout.Label($"Missing: {quest.Missing:N0}");
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawQuestKeysSection(HermesItemHideoutUsageResponse usage)
    {
        var active = usage.QuestKeyUses
            .Where(key => key.IsActive && !key.QuestCompleted)
            .OrderBy(key => key.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remaining = usage.QuestKeyUses.Count(key => !key.QuestCompleted);
        var completed = usage.QuestKeyUses.Count - remaining;
        var first = active.FirstOrDefault()
                    ?? usage.QuestKeyUses.FirstOrDefault(key => !key.QuestCompleted)
                    ?? usage.QuestKeyUses.FirstOrDefault();

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _questKeysExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  QUEST KEY KNOWLEDGE — {active.Count:N0} ACTIVE • {remaining:N0} REMAINING",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _questKeysExpanded = !_questKeysExpanded;
        }

        GUILayout.Label(first is null
            ? "This item is not linked to a known quest-key requirement."
            : $"{(first.IsActive && !first.QuestCompleted ? "ACTIVE" : first.QuestCompleted ? "COMPLETED" : "KNOWN")}: {first.QuestName} — {first.MapName} — opens {first.Opens}");
        GUILayout.Label($"Completed key uses: {completed:N0}");

        if (_questKeysExpanded)
        {
            GUILayout.Space(6f);
            if (usage.QuestKeyUses.Count == 0)
            {
                GUILayout.Label("The installed key and quest databases do not associate this item with a known quest lock.");
            }
            else
            {
                foreach (var keyUse in usage.QuestKeyUses)
                {
                    var marker = keyUse.QuestCompleted ? "✓" : keyUse.IsActive ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {keyUse.QuestName} — {keyUse.MapName}");
                    GUILayout.Label($"Status: {keyUse.QuestStatus}{(keyUse.AcquireInRaid ? " • Acquire during raid" : string.Empty)}");
                    if (!string.IsNullOrWhiteSpace(keyUse.Opens))
                    {
                        GUILayout.Label($"Opens: {keyUse.Opens}");
                    }
                    if (!string.IsNullOrWhiteSpace(keyUse.Purpose))
                    {
                        GUILayout.Label(keyUse.Purpose);
                    }
                    if (!string.IsNullOrWhiteSpace(keyUse.Acquisition))
                    {
                        GUILayout.Label($"Acquisition: {keyUse.Acquisition}");
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawHideoutCraftUsesSection(HermesItemHideoutUsageResponse usage)
    {
        var nextUpgrade = usage.UpgradeUses
            .Where(upgrade => !upgrade.IsMet && upgrade.TargetLevel > upgrade.CurrentLevel)
            .OrderByDescending(upgrade => upgrade.IsNextUpgrade)
            .ThenBy(upgrade => upgrade.TargetLevel)
            .ThenBy(upgrade => upgrade.AreaName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var readyCraft = usage.UsedBy
            .Concat(usage.ProducedBy)
            .OrderByDescending(craft => craft.CanStartNow || craft.IsComplete)
            .ThenBy(craft => craft.StationName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var totalUses = usage.UpgradeUses.Count + usage.ProducedBy.Count + usage.UsedBy.Count;

        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);
        var arrow = _hideoutCraftUsesExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  HIDEOUT & CRAFT USES — {totalUses:N0}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _hideoutCraftUsesExpanded = !_hideoutCraftUsesExpanded;
        }

        GUILayout.Label(nextUpgrade is not null
            ? $"Next upgrade: {nextUpgrade.AreaName} L{nextUpgrade.TargetLevel} • Owned {nextUpgrade.Owned:N0}/{nextUpgrade.Required:N0} • Missing {nextUpgrade.Missing:N0}"
            : readyCraft is not null
                ? $"Craft use: {readyCraft.StationName} L{readyCraft.RequiredStationLevel} • {readyCraft.Status}"
                : "No Hideout upgrade or player-facing recipe currently uses this item.");
        GUILayout.Label($"Upgrades: {usage.UpgradeUses.Count:N0} • Produced by: {usage.ProducedBy.Count:N0} • Ingredient for: {usage.UsedBy.Count:N0}");

        if (_hideoutCraftUsesExpanded)
        {
            GUILayout.Space(8f);
            GUILayout.Label("HIDEOUT UPGRADES");
            if (usage.UpgradeUses.Count == 0)
            {
                GUILayout.Label("Not required by a player-facing Hideout upgrade.");
            }
            else
            {
                foreach (var upgrade in usage.UpgradeUses)
                {
                    var marker = upgrade.TargetLevel <= upgrade.CurrentLevel || upgrade.IsMet ? "✓" : upgrade.IsNextUpgrade ? "▶" : "•";
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{marker} {upgrade.AreaName} Level {upgrade.TargetLevel} — {upgrade.Status}");
                    GUILayout.Label($"Current area level: {upgrade.CurrentLevel} • Required: {upgrade.Required:N0}{(upgrade.FoundInRaidRequired ? " FIR" : string.Empty)} • Owned: {upgrade.Owned:N0} • Missing: {upgrade.Missing:N0}");
                    if (upgrade.EstimatedMissingCost.HasValue)
                    {
                        GUILayout.Label($"Estimated missing cost: ₽{upgrade.EstimatedMissingCost.Value:N0}{(string.IsNullOrWhiteSpace(upgrade.AcquisitionSource) ? string.Empty : $" via {upgrade.AcquisitionSource}")}");
                    }
                    GUILayout.EndVertical();
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("PRODUCED BY");
            if (usage.ProducedBy.Count == 0)
            {
                GUILayout.Label("No player-facing Hideout recipe produces this item.");
            }
            else
            {
                foreach (var craft in usage.ProducedBy)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"• {craft.StationName} L{craft.RequiredStationLevel} — produces {craft.OutputQuantity:N0} × {craft.OutputName}");
                    GUILayout.Label($"Current station: L{craft.CurrentStationLevel} • {craft.Status} • {FormatDuration(craft.DurationSeconds)}");
                    if (craft.IsActive || craft.IsComplete)
                    {
                        GUILayout.Label(craft.IsComplete ? "Production complete — ready to collect" : "Production currently active");
                    }
                    GUILayout.EndVertical();
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("USED AS AN INGREDIENT");
            if (usage.UsedBy.Count == 0)
            {
                GUILayout.Label("Not used by a player-facing Hideout recipe.");
            }
            else
            {
                foreach (var craft in usage.UsedBy)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"• {craft.ItemCount:N0} × for {craft.OutputName} at {craft.StationName} L{craft.RequiredStationLevel}");
                    GUILayout.Label($"Current station: L{craft.CurrentStationLevel} • Owned: {craft.Owned:N0} • Missing: {craft.Missing:N0} • {craft.Status}");
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.EndVertical();
    }

    #endregion
}
