using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Independent Raid Planner workspace with a flat EFT list presentation.
/// It owns its own request, filter, scroll, sorting, and expansion state and
/// does not borrow Loadout state or rendering.
/// </summary>
internal sealed class HermesRaidPlannerPanel
{
    private HermesLoadoutSummaryResponse? _summary;
    private Vector2 _scroll;
    private bool _loading;
    private bool _requested;
    private int _requestVersion;
    private float _nextAutomaticRefresh;
    private string _search = string.Empty;
    private string _statusFilter = "All maps";
    private string _sorting = "Best prepared";
    private string _status = "Open Raid Planner to analyze active quest routes and pre-raid requirements.";
    private readonly Dictionary<string, bool> _expandedMaps = new(StringComparer.OrdinalIgnoreCase);

    public void Draw()
    {
        if (!_requested && !_loading)
        {
            _requested = true;
            _sorting = Plugin.Settings.GetRaidPlannerSorting();
            _ = RefreshFromServerAsync(false);
        }

        var refreshSeconds = Plugin.Settings.GetAutomaticRefreshSeconds();
        if (refreshSeconds > 0
            && _summary is not null
            && !_loading
            && Time.realtimeSinceStartup >= _nextAutomaticRefresh)
        {
            _nextAutomaticRefresh = Time.realtimeSinceStartup + refreshSeconds;
            _ = RefreshFromServerAsync(false);
        }

        HermesUi.DrawPanelHeader(
            "RAID PLANNER",
            "MAP ROUTES  •  ACTIVE QUESTS  •  REQUIRED GEAR  •  ACCESS KEYS",
            _status,
            _loading,
            () => _ = RefreshFromServerAsync(true));

        DrawToolbar();

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

        if (_summary is { Found: true })
        {
            DrawPlans(_summary);
        }
        else if (_summary is not null)
        {
            HermesUi.DrawEmptyState(_summary.Message ?? "Raid Planner is unavailable.");
        }
        else if (_loading)
        {
            HermesUi.DrawEmptyState(
                "READING ACTIVE RAID PLANS",
                "HERMES is resolving map objectives, route access, carried quest gear, and loadout warnings.");
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
        _status = "Building map plans from the active PMC profile...";

        try
        {
            var response = await HermesApiClient.GetLoadoutSummaryAsync(
                Plugin.Settings.CreateLoadoutRequestSettings());
            if (version != _requestVersion)
            {
                return;
            }

            _summary = response;
            _nextAutomaticRefresh = Time.realtimeSinceStartup
                                    + Plugin.Settings.GetAutomaticRefreshSeconds();
            _status = response.Found
                ? $"{response.RaidPlans.Count:N0} map(s)  •  {response.RaidPlans.Sum(plan => plan.ActiveQuestCount):N0} active quest stage(s)"
                : response.Message ?? "HERMES could not build the current raid plans.";
        }
        catch (Exception ex)
        {
            if (version == _requestVersion)
            {
                _status = HermesApiClient.DescribeFailure(ex, "Raid Planner");
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
        _search = string.Empty;
        _statusFilter = "All maps";
        _sorting = Plugin.Settings.GetRaidPlannerSorting();
        _expandedMaps.Clear();
        _status = "Open Raid Planner to analyze active quest routes and pre-raid requirements.";
    }

    private void DrawToolbar()
    {
        GUILayout.BeginVertical(HermesEftTheme.Toolbar);
        GUILayout.BeginHorizontal();

        GUI.SetNextControlName("HermesRaidPlannerSearch");
        _search = GUILayout.TextField(
            _search,
            HermesEftTheme.SearchField,
            GUILayout.MinWidth(180f),
            GUILayout.ExpandWidth(true),
            GUILayout.Height(28f));

        DrawFilterButton("All maps", "ALL", 58f);
        DrawFilterButton("Prepared", "READY", 68f);
        DrawFilterButton("Missing gear", "MISSING", 78f);
        DrawFilterButton("Incomplete", "INCOMPLETE", 92f);

        if (GUILayout.Button(
                $"SORT: {_sorting.ToUpperInvariant()}",
                HermesEftTheme.Filter(false),
                GUILayout.Width(190f),
                GUILayout.Height(28f)))
        {
            _sorting = NextSorting(_sorting);
            _scroll = Vector2.zero;
        }

        if (GUILayout.Button("CLEAR", HermesEftTheme.Filter(false), GUILayout.Width(58f), GUILayout.Height(28f)))
        {
            _search = string.Empty;
            _statusFilter = "All maps";
            _scroll = Vector2.zero;
        }

        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void DrawFilterButton(string filter, string label, float width)
    {
        var selected = _statusFilter.Equals(filter, StringComparison.OrdinalIgnoreCase);
        if (GUILayout.Button(
                label,
                HermesEftTheme.Filter(selected),
                GUILayout.Width(width),
                GUILayout.Height(28f)))
        {
            _statusFilter = filter;
            _scroll = Vector2.zero;
        }
    }

    private void DrawPlans(HermesLoadoutSummaryResponse summary)
    {
        var plans = summary.RaidPlans
            .Where(plan => string.IsNullOrWhiteSpace(_search)
                           || plan.MapName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                           || plan.Quests.Any(quest =>
                               quest.QuestName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                               || quest.TraderName.Contains(_search, StringComparison.OrdinalIgnoreCase)))
            .Where(MatchesStatusFilter)
            .ToList();
        plans = SortPlans(plans, _sorting).ToList();

        if (plans.Count == 0)
        {
            HermesUi.DrawEmptyState(
                "NO RAID PLANS MATCH THE CURRENT FILTERS",
                "Change the map/quest search or select a different preparation filter.");
            return;
        }

        var prepared = plans.Count(plan => GetMissingRequirementCount(plan) == 0);
        var missing = plans.Sum(GetMissingRequirementCount);
        var objectives = plans.Sum(plan => VisibleObjectives(plan).Count());
        var quests = plans.Sum(plan => plan.ActiveQuestCount);

        GUILayout.BeginHorizontal(HermesEftTheme.SummaryBar);
        DrawMetric("MAPS", plans.Count.ToString("N0"), "matching filters");
        DrawMetric("PREPARED", prepared.ToString("N0"), "ready locations");
        DrawMetric("MISSING", missing.ToString("N0"), "gear or access");
        DrawMetric("OBJECTIVES", objectives.ToString("N0"), $"across {quests:N0} quest stage(s)");
        GUILayout.EndHorizontal();

        DrawReadinessContext(summary.Warnings);

        var visiblePlans = HermesUi.LimitRows(plans, out var hiddenPlans);
        foreach (var plan in visiblePlans)
        {
            DrawPlan(plan);
        }
        HermesUi.DrawHiddenRowsNotice(hiddenPlans);
    }

    private void DrawPlan(HermesRaidPlanSummary plan)
    {
        var requirements = VisibleRequirements(plan).ToList();
        var objectives = VisibleObjectives(plan).ToList();
        var missing = requirements.Count(requirement => !requirement.AcquireInRaid && !requirement.IsSatisfied);
        var expanded = _expandedMaps.GetValueOrDefault(plan.MapName, true);
        var status = missing > 0 ? $"MISSING {missing:N0}" : "PREPARED";

        GUILayout.BeginVertical(HermesEftTheme.ContentPanel);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(
                $"{(expanded ? "▼" : "▶")}   {plan.MapName.ToUpperInvariant()}",
                HermesEftTheme.MapHeader,
                GUILayout.Height(32f),
                GUILayout.ExpandWidth(true)))
        {
            expanded = !expanded;
            _expandedMaps[plan.MapName] = expanded;
        }
        GUILayout.Label(status, HermesEftTheme.StatusBadge(status), GUILayout.Width(112f), GUILayout.Height(32f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal(HermesEftTheme.DataRow(false));
        GUILayout.Label(
            $"{plan.ActiveQuestCount:N0} active quest stage(s)  •  {objectives.Count:N0} visible objective(s)  •  {requirements.Count:N0} requirement(s)",
            HermesEftTheme.RowMeta);
        GUILayout.EndHorizontal();

        if (!expanded)
        {
            GUILayout.EndVertical();
            return;
        }

        var rowIndex = 0;
        if (Plugin.Settings.RaidPlannerIncludeQuestGearRestrictions.Value)
        {
            GUILayout.Label("COMBINED BRING / EQUIP CHECKLIST", HermesEftTheme.SectionHeader);
            if (requirements.Count == 0)
            {
                HermesUi.DrawEmptyState(
                    "NO PRE-RAID GEAR REQUIREMENTS",
                    "This map has no visible equipment, access-key, marker, or carried-item requirement.");
            }
            else
            {
                foreach (var group in requirements
                             .GroupBy(ClassifyRequirement, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(group => RequirementGroupRank(group.Key)))
                {
                    GUILayout.Label(group.Key.ToUpperInvariant(), HermesEftTheme.RowMeta);
                    foreach (var requirement in group
                                 .OrderBy(value => value.IsSatisfied)
                                 .ThenBy(value => value.RequiredEquipment, StringComparer.OrdinalIgnoreCase))
                    {
                        DrawRequirement(requirement, rowIndex++);
                    }
                }
            }
        }

        GUILayout.Label("ACTIVE QUEST OBJECTIVES", HermesEftTheme.SectionHeader);
        foreach (var quest in plan.Quests)
        {
            var questObjectives = quest.Objectives.Where(IsObjectiveVisible).ToList();
            if (questObjectives.Count == 0 && Plugin.Settings.HideEmptyLoadoutSections.Value)
            {
                continue;
            }

            GUILayout.BeginVertical(HermesEftTheme.DataRow(rowIndex++ % 2 == 1));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label($"{quest.QuestName}  —  {quest.TraderName}", HermesEftTheme.RowTitle);
            GUILayout.Label(
                $"{quest.CompletedObjectiveCount:N0}/{quest.ObjectiveCount:N0} complete  •  {quest.MissingRequirementCount:N0} missing raid requirement(s)",
                HermesEftTheme.RowMeta);
            GUILayout.EndVertical();
            GUILayout.Label(
                quest.Status.ToUpperInvariant(),
                HermesEftTheme.StatusBadge(quest.Status),
                GUILayout.Width(104f),
                GUILayout.Height(25f));
            GUILayout.EndHorizontal();

            if (questObjectives.Count == 0)
            {
                GUILayout.Label("All objective rows are hidden by the current HERMES settings.", HermesEftTheme.RowMeta);
            }
            else
            {
                foreach (var objective in questObjectives)
                {
                    var marker = objective.IsCompleted ? "✓" : objective.IsRaidObjective ? "•" : "○";
                    GUILayout.Label($"{marker}  {objective.Description}", HermesEftTheme.RowDescription);
                    if (Plugin.Settings.ShowSectionDescriptions.Value)
                    {
                        GUILayout.Label($"     {objective.ConditionType}  •  {objective.Status}", HermesEftTheme.RowMeta);
                    }
                }
            }
            GUILayout.EndVertical();
        }

        if (Plugin.Settings.RaidPlannerShowPlanNotes.Value && plan.Notes.Count > 0)
        {
            GUILayout.Label("PLAN NOTES", HermesEftTheme.SectionHeader);
            foreach (var note in plan.Notes)
            {
                GUILayout.BeginHorizontal(HermesEftTheme.DataRow(rowIndex++ % 2 == 1));
                GUILayout.Label("•  " + note, HermesEftTheme.RowDescription);
                GUILayout.EndHorizontal();
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawRequirement(HermesRaidPlanRequirement requirement, int rowIndex)
    {
        var status = requirement.AcquireInRaid
            ? "ACQUIRE IN RAID"
            : requirement.IsSatisfied
                ? "READY"
                : $"MISSING {FormatCount(requirement.MissingQuantity)}";

        GUILayout.BeginVertical(HermesEftTheme.DataRow(rowIndex % 2 == 1));
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(requirement.RequiredEquipment, HermesEftTheme.RowTitle);
        GUILayout.Label(
            $"{requirement.RequirementKind}  •  required {FormatCount(requirement.RequiredQuantity)}  •  carried {FormatCount(requirement.CarriedQuantity)}",
            HermesEftTheme.RowMeta);
        GUILayout.EndVertical();
        GUILayout.Label(status, HermesEftTheme.StatusBadge(status), GUILayout.Width(122f), GUILayout.Height(25f));
        GUILayout.EndHorizontal();

        if (requirement.FoundInRaidRequired || requirement.FoundInRaidCarriedQuantity > 0d)
        {
            GUILayout.Label(
                $"Found in raid carried: {FormatCount(requirement.FoundInRaidCarriedQuantity)}{(requirement.FoundInRaidRequired ? "  •  FIR required" : string.Empty)}",
                HermesEftTheme.RowMeta);
        }
        if (requirement.QuestNames.Count > 0)
        {
            GUILayout.Label("Quests: " + string.Join(", ", requirement.QuestNames), HermesEftTheme.RowMeta);
        }
        if (!string.IsNullOrWhiteSpace(requirement.Note))
        {
            GUILayout.Label(requirement.Note, HermesEftTheme.RowDescription);
        }
        GUILayout.EndVertical();
    }

    private static void DrawReadinessContext(IReadOnlyList<HermesLoadoutWarning> warnings)
    {
        var visible = warnings
            .Where(warning =>
                (Plugin.Settings.RaidPlannerIncludeMedicalReadiness.Value
                 && warning.Category.Contains("medical", StringComparison.OrdinalIgnoreCase))
                || (Plugin.Settings.RaidPlannerIncludeAmmunitionReadiness.Value
                    && (warning.Category.Contains("weapon", StringComparison.OrdinalIgnoreCase)
                        || warning.Category.Contains("ammo", StringComparison.OrdinalIgnoreCase)))
                || (Plugin.Settings.RaidPlannerIncludeInsuranceWarnings.Value
                    && warning.Category.Contains("insurance", StringComparison.OrdinalIgnoreCase)))
            .Take(6)
            .ToList();
        if (visible.Count == 0)
        {
            return;
        }

        GUILayout.Label("LOADOUT CONTEXT", HermesEftTheme.SectionHeader);
        var index = 0;
        foreach (var warning in visible)
        {
            GUILayout.BeginHorizontal(HermesEftTheme.DataRow(index++ % 2 == 1));
            GUILayout.Label(warning.Message, HermesEftTheme.RowDescription, GUILayout.ExpandWidth(true));
            GUILayout.Label(
                warning.Category.ToUpperInvariant(),
                HermesEftTheme.StatusBadge(warning.Severity),
                GUILayout.Width(108f),
                GUILayout.Height(25f));
            GUILayout.EndHorizontal();
        }
    }

    private static IEnumerable<HermesRaidPlanRequirement> VisibleRequirements(HermesRaidPlanSummary plan)
    {
        if (!Plugin.Settings.RaidPlannerIncludeQuestGearRestrictions.Value)
        {
            return [];
        }

        return plan.CombinedRequirements
            .Where(requirement => Plugin.Settings.RaidPlannerShowAcquireInRaidItems.Value || !requirement.AcquireInRaid)
            .Where(requirement => Plugin.Settings.RaidPlannerShowInferredRouteKeys.Value || !IsInferredRouteKey(requirement));
    }

    private static IEnumerable<HermesRaidPlanObjective> VisibleObjectives(HermesRaidPlanSummary plan)
    {
        return plan.Quests.SelectMany(quest => quest.Objectives).Where(IsObjectiveVisible);
    }

    private static bool IsObjectiveVisible(HermesRaidPlanObjective objective)
    {
        if (objective.IsCompleted && !Plugin.Settings.ShowCompletedQuestObjectives.Value)
        {
            return false;
        }

        var text = (objective.ConditionType + " " + objective.Description).ToLowerInvariant();
        var handover = text.Contains("handover") || text.Contains("hand over") || text.Contains("turn in");
        if (handover && !Plugin.Settings.RaidPlannerShowHandoverObjectives.Value)
        {
            return false;
        }

        var fir = text.Contains("found in raid") || text.Contains("fir");
        return !handover || !fir || Plugin.Settings.RaidPlannerShowFirHandoverObjectives.Value;
    }

    private static bool IsInferredRouteKey(HermesRaidPlanRequirement requirement)
    {
        var text = (requirement.RequirementKind + " " + requirement.Note).ToLowerInvariant();
        return text.Contains("inferred") || text.Contains("route key") || text.Contains("locked door");
    }

    private static string ClassifyRequirement(HermesRaidPlanRequirement requirement)
    {
        if (requirement.AcquireInRaid)
        {
            return "Acquire during raid";
        }

        var kind = requirement.RequirementKind.ToLowerInvariant();
        if (kind.Contains("key") || kind.Contains("access"))
        {
            return "Keys and access";
        }
        if (kind.Contains("equip") || kind.Contains("wear") || kind.Contains("weapon") || kind.Contains("armor"))
        {
            return "Equip before raid";
        }
        if (kind.Contains("marker") || kind.Contains("plant") || kind.Contains("bring") || kind.Contains("carry"))
        {
            return "Bring into raid";
        }
        return "Other requirements";
    }

    private static int RequirementGroupRank(string group)
    {
        return group switch
        {
            "Equip before raid" => 0,
            "Bring into raid" => 1,
            "Keys and access" => 2,
            "Acquire during raid" => 3,
            _ => 4
        };
    }

    private bool MatchesStatusFilter(HermesRaidPlanSummary plan)
    {
        return _statusFilter switch
        {
            "Prepared" => GetMissingRequirementCount(plan) == 0,
            "Missing gear" => GetMissingRequirementCount(plan) > 0,
            "Incomplete" => plan.CompletedObjectiveCount < plan.ObjectiveCount,
            _ => true
        };
    }

    private static int GetMissingRequirementCount(HermesRaidPlanSummary plan)
    {
        return VisibleRequirements(plan).Count(requirement => !requirement.AcquireInRaid && !requirement.IsSatisfied);
    }

    private static IEnumerable<HermesRaidPlanSummary> SortPlans(
        IEnumerable<HermesRaidPlanSummary> plans,
        string sorting)
    {
        return sorting switch
        {
            "Most active quests" => plans
                .OrderByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(GetMissingRequirementCount)
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Most incomplete objectives" => plans
                .OrderByDescending(plan => Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount))
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Fewest missing requirements" => plans
                .OrderBy(GetMissingRequirementCount)
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            "Alphabetical" => plans.OrderBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase),
            _ => plans
                .OrderBy(plan => GetMissingRequirementCount(plan) > 0 ? 1 : 0)
                .ThenBy(GetMissingRequirementCount)
                .ThenByDescending(plan => plan.ActiveQuestCount)
                .ThenByDescending(plan => Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount))
                .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string NextSorting(string current)
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

    private static void DrawMetric(string title, string value, string detail)
    {
        GUILayout.BeginVertical(HermesEftTheme.SummaryCell, GUILayout.ExpandWidth(true), GUILayout.MinHeight(66f));
        GUILayout.Label(title, HermesEftTheme.SummaryTitle);
        GUILayout.Label(value, HermesEftTheme.SummaryValue);
        GUILayout.Label(detail, HermesEftTheme.SummaryDetail);
        GUILayout.EndVertical();
    }

    private static string FormatCount(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.0001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("0.##");
    }
}
