using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Stash Instances And Tags

    private void DrawStashInstanceSection()
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _stashInstancesExpanded ? "▼" : "▶";
        var selectedLabel = _selectedStashInstanceKey is null
            ? "Base item estimate"
            : _stashInstances.FirstOrDefault(instance => instance.InstanceKey == _selectedStashInstanceKey)?.Label
              ?? "Selected owned copy";

        if (GUILayout.Button(
                $"{arrow}  OWNED COPY FOR TRADER SALE — {selectedLabel}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _stashInstancesExpanded = !_stashInstancesExpanded;
        }

        if (_loadingInstancePrice)
        {
            GUILayout.Label("Recalculating trader prices for the selected owned copy...");
        }

        if (_stashInstancesExpanded)
        {
            GUILayout.Space(4f);
            GUILayout.Label("Select the exact owned copy HERMES should value. Root and child-item value are included in trader sale estimates.");

            GUI.enabled = !_loadingInstancePrice && !_loadingDetails;
            var baseSelected = _selectedStashInstanceKey is null;
            if (GUILayout.Button(
                    (baseSelected ? "● " : string.Empty) + "Base item estimate — full condition, quantity 1, no installed items",
                    GUILayout.MinHeight(36f),
                    GUILayout.ExpandWidth(true)))
            {
                _ = SelectStashInstanceAsync(null);
            }

            foreach (var instance in _stashInstances)
            {
                var selected = string.Equals(
                    instance.InstanceKey,
                    _selectedStashInstanceKey,
                    StringComparison.OrdinalIgnoreCase);
                var valueText = instance.ConditionAdjustedReferenceValue > 0
                    ? $" - {instance.Location} - root RUB {instance.RootConditionAdjustedReferenceValue:N0} + child items RUB {instance.InstalledComponentReferenceValue:N0}"
                    : string.Empty;

                GUILayout.BeginVertical(GUI.skin.box);
                if (GUILayout.Button(
                        (selected ? "● " : string.Empty) + instance.Label + valueText,
                        GUILayout.MinHeight(42f),
                        GUILayout.ExpandWidth(true)))
                {
                    _ = SelectStashInstanceAsync(instance.InstanceKey);
                }
                DrawOwnedCopyTagQuickEdit(instance);
                GUILayout.EndVertical();
            }

            GUI.enabled = true;

            if (_stashInstances.Count == 0)
            {
                GUILayout.Label(_loadingDetails
                    ? "Loading matching owned copies..."
                    : "No matching owned copy is currently in the active PMC inventory. The base-item estimate is being used.");
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawOwnedCopyTagQuickEdit(HermesStashInstanceSummary instance)
    {
        var hasTag = !string.IsNullOrWhiteSpace(instance.TagName);
        GUILayout.Label(FormatStashInstanceTag(instance));

        if (!Plugin.Settings.EnableConfirmedActions.Value || !Plugin.Settings.AllowInventoryTagActions.Value)
        {
            GUILayout.Label("Inventory tag edits are disabled.");
            return;
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = !_actionLoading;
        if (!hasTag)
        {
            if (GUILayout.Button(IsRowTagEditorOpen(instance, "apply") ? "Editing tag..." : "+ Tag", GUILayout.Width(110f), GUILayout.Height(26f)))
            {
                OpenRowTagEditor(instance, "apply");
            }
        }
        else
        {
            if (GUILayout.Button(IsRowTagEditorOpen(instance, "change") ? "Editing change..." : "Change tag", GUILayout.Width(110f), GUILayout.Height(26f)))
            {
                OpenRowTagEditor(instance, "change");
            }
            if (GUILayout.Button("Reset tag", GUILayout.Width(110f), GUILayout.Height(26f)))
            {
                _ = ProposeInventoryTagActionAsync("remove", string.Empty, "blue", [instance.InstanceKey]);
            }
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (!IsRowTagEditorOpen(instance, _tagEditorMode))
        {
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label(_tagEditorMode == "change" ? "New tag" : "Tag", GUILayout.Width(60f));
        TagDraftName = GUILayout.TextField(_tagDraftName, GUILayout.Width(160f));
        GUILayout.Label("Color", GUILayout.Width(48f));
        var currentColorIndex = Math.Max(0, Array.FindIndex(
            TagColorOptions,
            option => string.Equals(option.Value, NormalizeTagColor(_tagDraftColor), StringComparison.OrdinalIgnoreCase)));
        var selectedColorIndex = GUILayout.SelectionGrid(
            currentColorIndex,
            TagColorOptions.Select(option => option.Label).ToArray(),
            4,
            GUILayout.Width(300f));
        TagDraftColor = TagColorOptions[Mathf.Clamp(selectedColorIndex, 0, TagColorOptions.Length - 1)].Value;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUI.enabled = CanProposeInventoryTagActionForInstance(instance, _tagEditorMode);
        if (GUILayout.Button("Preview tag change", GUILayout.Width(160f), GUILayout.Height(28f)))
        {
            _ = ProposeInventoryTagActionAsync(_tagEditorMode, _tagDraftName, _tagDraftColor, [instance.InstanceKey]);
        }
        GUI.enabled = true;
        if (GUILayout.Button("Cancel", GUILayout.Width(90f), GUILayout.Height(28f)))
        {
            _tagEditorInstanceKey = string.Empty;
        }
        GUILayout.EndHorizontal();
    }

    private void OpenRowTagEditor(HermesStashInstanceSummary instance, string mode)
    {
        _tagEditorInstanceKey = instance.InstanceKey;
        _tagEditorMode = NormalizeTagActionMode(mode);
        TagActionMode = _tagEditorMode;
        if (_tagEditorMode == "change")
        {
            TagDraftName = instance.TagName ?? string.Empty;
            TagDraftColor = string.IsNullOrWhiteSpace(instance.TagColor) ? "blue" : instance.TagColor;
        }
        else
        {
            TagDraftName = string.Empty;
            TagDraftColor = "blue";
        }
    }

    private bool IsRowTagEditorOpen(HermesStashInstanceSummary instance, string mode)
        => string.Equals(_tagEditorInstanceKey, instance.InstanceKey, StringComparison.OrdinalIgnoreCase)
           && string.Equals(_tagEditorMode, NormalizeTagActionMode(mode), StringComparison.OrdinalIgnoreCase);

    private bool CanProposeInventoryTagActionForInstance(HermesStashInstanceSummary instance, string mode)
    {
        if (_actionLoading
            || !Plugin.Settings.EnableConfirmedActions.Value
            || !Plugin.Settings.AllowInventoryTagActions.Value
            || string.IsNullOrWhiteSpace(instance.InstanceKey))
        {
            return false;
        }

        var normalizedMode = NormalizeTagActionMode(mode);
        if (normalizedMode != "remove" && string.IsNullOrWhiteSpace(_tagDraftName))
        {
            return false;
        }

        var hasTag = !string.IsNullOrWhiteSpace(instance.TagName);
        return normalizedMode switch
        {
            "apply" => !hasTag,
            "change" or "remove" => hasTag,
            _ => false
        };
    }

    internal IReadOnlyCollection<string> SelectedTagActionInstanceKeys => _selectedTagActionInstanceKeys;

    internal string TagActionMode
    {
        get => _tagActionMode;
        set => _tagActionMode = NormalizeTagActionMode(value);
    }

    internal string TagDraftName
    {
        get => _tagDraftName;
        set => _tagDraftName = value ?? string.Empty;
    }

    internal string TagDraftColor
    {
        get => _tagDraftColor;
        set => _tagDraftColor = NormalizeTagColor(value);
    }

    internal void ToggleTagActionInstance(string instanceKey)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
        {
            return;
        }

        if (!_selectedTagActionInstanceKeys.Add(instanceKey.Trim()))
        {
            _selectedTagActionInstanceKeys.Remove(instanceKey.Trim());
        }

        HermesNativeWorkspaceRuntime.RequestClientRefresh();
    }

    internal void ClearTagActionSelection()
    {
        _selectedTagActionInstanceKeys.Clear();
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
    }

    internal void SelectAllMatchingTagActionInstances()
    {
        foreach (var instance in _stashInstances)
        {
            if (!string.IsNullOrWhiteSpace(instance.InstanceKey))
            {
                _selectedTagActionInstanceKeys.Add(instance.InstanceKey);
            }
        }

        HermesNativeWorkspaceRuntime.RequestClientRefresh();
    }

    private static string NormalizeTagActionMode(string? mode)
        => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "change" => "change",
            "remove" => "remove",
            _ => "apply"
        };

    private static string NormalizeTagColor(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "blue" : value.Trim();
        return TagColorOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized.ToLowerInvariant()
            : "blue";
    }

    private List<HermesStashInstanceSummary> GetSelectedTagActionInstances()
        => _stashInstances
            .Where(instance => _selectedTagActionInstanceKeys.Contains(instance.InstanceKey))
            .ToList();

    private PendingClientTagMutation? BuildPendingClientTagMutation()
    {
        if (_actionProposal?.ActionKind != "HERMES_INVENTORY_TAG")
        {
            return null;
        }

        var instances = GetSelectedTagActionInstances()
            .Where(instance => !string.IsNullOrWhiteSpace(instance.ProfileItemId))
            .ToList();
        return instances.Count == 0
            ? null
            : new PendingClientTagMutation(_tagActionMode, _tagDraftName, _tagDraftColor, instances);
    }

    private void ApplyConfirmedTagMutationToClient(PendingClientTagMutation? mutation)
    {
        if (mutation is null)
        {
            return;
        }

        var liveUpdated = HermesLiveInventoryTagBridge.ApplyTagMutation(
            mutation.Instances,
            mutation.Mode,
            mutation.TagName,
            mutation.TagColor);
        ApplyConfirmedTagMutationToLocalSummaries(mutation);

        if (liveUpdated > 0)
        {
            Plugin.Log?.LogDebug($"HERMES updated {liveUpdated:N0} live EFT inventory tag component(s).");
        }
    }

    private void ApplyConfirmedTagMutationToLocalSummaries(PendingClientTagMutation mutation)
    {
        var selectedIds = mutation.Instances
            .Select(instance => instance.ProfileItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0)
        {
            return;
        }

        var reset = string.Equals(mutation.Mode, "remove", StringComparison.OrdinalIgnoreCase);
        foreach (var instance in _stashInstances.Where(instance => selectedIds.Contains(instance.ProfileItemId)))
        {
            instance.TagName = reset ? string.Empty : mutation.TagName.Trim();
            instance.TagColor = reset ? string.Empty : NormalizeTagColor(mutation.TagColor);
        }
    }

    private bool CanProposeInventoryTagActionFromItem()
    {
        var selectedInstances = GetSelectedTagActionInstances();
        return !_actionLoading
               && Plugin.Settings.EnableConfirmedActions.Value
               && Plugin.Settings.AllowInventoryTagActions.Value
               && IsTagSelectionReady(_tagActionMode, selectedInstances)
               && (_tagActionMode == "remove" || !string.IsNullOrWhiteSpace(_tagDraftName));
    }

    private static bool IsTagSelectionReady(
        string mode,
        IReadOnlyCollection<HermesStashInstanceSummary> selectedInstances)
    {
        if (selectedInstances.Count == 0)
        {
            return false;
        }

        return mode switch
        {
            "apply" => selectedInstances.All(instance => string.IsNullOrWhiteSpace(instance.TagName)),
            "change" or "remove" => selectedInstances.All(instance => !string.IsNullOrWhiteSpace(instance.TagName)),
            _ => false
        };
    }

    private static string FormatStashInstanceTag(HermesStashInstanceSummary instance)
        => string.IsNullOrWhiteSpace(instance.TagName)
            ? "Tag none"
            : string.IsNullOrWhiteSpace(instance.TagColor)
                ? $"Tag {instance.TagName}"
                : $"Tag {instance.TagName} / {NormalizeTagColor(instance.TagColor)}";

    #endregion
}
