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
    #region Hideout

    private void RenderHideout(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.HideoutStatus, _state.HideoutLoading, _state.RefreshActive);
        var summary = _state.HideoutSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.HideoutLoading ? "Reading the active PMC hideout..." : "No hideout snapshot loaded.", "Refresh after the active profile is available.");
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Hideout data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("READY", summary.ReadyAreaCount.ToString("N0")),
            ("MATERIAL BLOCKED", summary.MaterialBlockedAreaCount.ToString("N0")),
            ("PROGRESSION BLOCKED", summary.ProgressionBlockedAreaCount.ToString("N0")),
            ("ACTIVE PRODUCTIONS", summary.Resources.ActiveProductionCount.ToString("N0")),
            ("READY TO COLLECT", summary.Resources.CompletedProductionCount.ToString("N0")),
            ("GENERATOR", summary.Resources.GeneratorActive ? "RUNNING" : "OFF"));

        var toolbar = CreateToolbar(root);
        AddToolbarLabel(toolbar, "AREAS");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER AREAS", _hideoutSearch, 220f);
        search.onEndEdit.AddListener(value =>
        {
            _hideoutSearch = value.Trim();
            Invalidate();
        });
        AddButton(toolbar, $"FILTER: {_hideoutFilter}", CycleHideoutFilter, 156f);

        var split = HermesNativeUiFramework.CreateSplitView(root, 340f);
        var splitLayout = split.Root.gameObject.AddComponent<LayoutElement>();
        splitLayout.flexibleHeight = 1f;
        splitLayout.flexibleWidth = 1f;
        AddVerticalLayout(split.Left, 5, 5, 5, 5, 2f);
        AddVerticalLayout(split.Right, 5, 5, 5, 5, 2f);
        var areas = CreateScroll(split.Left, "hideout-areas", true);
        var details = CreateScroll(split.Right, "hideout-details", true);

        var filtered = FilterHideoutAreas(summary.Areas).Take(MaximumRowsPerSection).ToList();
        AddSectionHeader(areas.Content, $"AREAS  {filtered.Count:N0}");
        foreach (var area in filtered)
        {
            var selected = string.Equals(_state.SelectedHideoutArea?.AreaKey, area.AreaKey, StringComparison.OrdinalIgnoreCase);
            var requirementPreview = BuildHideoutRequirementPreview(area);
            AddCard(
                areas.Content,
                area.Name,
                $"Current L{area.CurrentLevel} / L{area.MaximumLevel} • target {(area.TargetLevel.HasValue ? $"L{area.TargetLevel}" : "complete")}\n{requirementPreview}",
                area.Status,
                () =>
                {
                    _state.SelectHideoutArea(area);
                    Invalidate(0.20f);
                },
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }
        if (filtered.Count == 0)
        {
            AddEmptyState(areas.Content, "No areas match the filter.", string.Empty);
        }

        RenderHideoutDetails(details.Content, summary);
    }

    private IEnumerable<HermesHideoutAreaSummary> FilterHideoutAreas(IEnumerable<HermesHideoutAreaSummary> areas)
    {
        var query = _hideoutSearch.Trim();
        return areas
            .Where(area => query.Length == 0
                           || area.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || area.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(area => _hideoutFilter switch
            {
                "READY" => area.Status.Contains("ready", StringComparison.OrdinalIgnoreCase),
                "BLOCKED" => area.Status.Contains("blocked", StringComparison.OrdinalIgnoreCase) || area.MissingItemTypes > 0,
                "BUILDING" => area.IsConstructing,
                "COMPLETE" => !area.TargetLevel.HasValue,
                _ => true
            })
            .OrderByDescending(area => area.IsConstructing)
            .ThenByDescending(area => area.MissingItemTypes == 0 && area.TargetLevel.HasValue)
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void CycleHideoutFilter()
    {
        _hideoutFilter = _hideoutFilter switch
        {
            "ALL" => "READY",
            "READY" => "BLOCKED",
            "BLOCKED" => "BUILDING",
            "BUILDING" => "COMPLETE",
            _ => "ALL"
        };
        Invalidate();
    }

    private void RenderHideoutDetails(Transform parent, HermesHideoutSummaryResponse summary)
    {
        var area = _state!.SelectedHideoutArea;
        var detail = _state.HideoutDetail;
        if (area is null)
        {
            AddSectionHeader(parent, "RESOURCES");
            var resources = summary.Resources;
            AddMetricGrid(parent,
                ("FUEL CONTAINERS", resources.FuelContainerCount.ToString("N0")),
                ("FUEL REMAINING", resources.FuelResourceRemaining.ToString("0.##")),
                ("GENERATOR RUNTIME", FormatDuration(resources.EstimatedGeneratorRuntimeSeconds ?? 0L)),
                ("FUEL COUNTER", resources.FuelCounter?.ToString("0.##") ?? "N/A"),
                ("AIR FILTER", resources.AirFilterCounter?.ToString("0.##") ?? "N/A"),
                ("WATER FILTER", resources.WaterFilterCounter?.ToString("0.##") ?? "N/A"));

            AddSectionHeader(parent, "ACTIVE PRODUCTIONS");
            if (summary.ActiveProductions.Count == 0)
            {
                AddEmptyState(parent, "No active productions.", string.Empty);
            }
            foreach (var production in summary.ActiveProductions.Take(40))
            {
                AddCard(parent,
                    production.OutputName,
                    $"{production.StationName} • output {production.OutputQuantity:N0} • {FormatDuration(production.SecondsRemaining)}",
                    production.Status,
                    string.IsNullOrWhiteSpace(production.OutputTemplateId)
                        ? null
                        : () => _state.Window.OpenForPreviewItem(production.OutputTemplateId!, "Hideout production"));
            }
            return;
        }

        AddSectionHeader(parent, area.Name);
        AddMetricGrid(parent,
            ("CURRENT", $"L{area.CurrentLevel}"),
            ("TARGET", area.TargetLevel.HasValue ? $"L{area.TargetLevel}" : "COMPLETE"),
            ("STATUS", area.Status),
            ("MISSING TYPES", area.MissingItemTypes.ToString("N0")),
            ("MISSING COST", Money(detail?.EstimatedMissingAcquisitionCost ?? area.EstimatedMissingHandbookCost)),
            ("CONSTRUCTION", detail is null ? "N/A" : FormatDuration(detail.ConstructionSeconds)));

        if (_state.HideoutLoading && detail is null)
        {
            AddEmptyState(parent, "Loading area requirements...", string.Empty);
            return;
        }
        if (detail is null || !detail.Found)
        {
            AddEmptyState(parent, "Area detail unavailable.", detail?.Message ?? string.Empty);
            return;
        }

        AddSectionHeader(parent, "REQUIREMENTS");
        foreach (var requirement in detail.Requirements)
        {
            var description = $"Owned {FormatCount(requirement.Owned)} / {FormatCount(requirement.Required)} • missing {FormatCount(requirement.Missing)}"
                              + (requirement.FoundInRaidRequired ? " • FIR required" : string.Empty)
                              + (!string.IsNullOrWhiteSpace(requirement.AcquisitionSource) ? $" • {requirement.AcquisitionSource}" : string.Empty);
            AddCard(
                parent,
                requirement.Name,
                description,
                requirement.IsMet ? "MET" : $"MISSING • {Money(requirement.EstimatedMissingCost)}",
                GetHideoutRequirementAction(summary, requirement));
        }
    }

    private Action? GetHideoutRequirementAction(
        HermesHideoutSummaryResponse summary,
        HermesHideoutRequirement requirement)
    {
        var linkedArea = FindHideoutArea(summary, requirement);
        if (linkedArea is not null)
        {
            return () =>
            {
                _state!.SelectHideoutArea(linkedArea);
                Invalidate(0.20f);
            };
        }

        return string.IsNullOrWhiteSpace(requirement.ItemTemplateId)
            ? null
            : () => _state!.Window.OpenForPreviewItem(requirement.ItemTemplateId!, "Hideout requirement");
    }

    private static HermesHideoutAreaSummary? FindHideoutArea(
        HermesHideoutSummaryResponse summary,
        HermesHideoutRequirement requirement)
    {
        if (!requirement.Type.Equals("Area", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requirement.AreaKey))
        {
            var keyed = summary.Areas.FirstOrDefault(area =>
                string.Equals(area.AreaKey, requirement.AreaKey, StringComparison.OrdinalIgnoreCase));
            if (keyed is not null)
            {
                return keyed;
            }
        }

        return summary.Areas.FirstOrDefault(area =>
            string.Equals(area.Name, requirement.Name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
