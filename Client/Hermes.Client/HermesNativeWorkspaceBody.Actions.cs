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
    #region Actions

    private void RenderActions(RectTransform parent)
    {
        var state = _state!;
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(
            root,
            state.ActionStatus,
            state.ActionLoading,
            state.RefreshActionHistory,
            "HISTORY");

        AddMetricGrid(root,
            ("MASTER TOGGLE", Plugin.Settings.EnableConfirmedActions.Value ? "ON" : "OFF"),
            ("TEST ACTION", Plugin.Settings.AllowHarmlessTestActions.Value ? "ALLOWED" : "BLOCKED"),
            ("INVENTORY TAGS", Plugin.Settings.AllowInventoryTagActions.Value ? "ALLOWED" : "BLOCKED"),
            ("HISTORY", (state.ActionHistory?.TotalActions ?? 0).ToString("N0")));

        var toolbar = CreateToolbar(root);
        AddButton(
            toolbar,
            "TEST PROPOSAL",
            () =>
            {
                state.ProposeTestAction();
                Invalidate(0.20f);
            },
            126f,
            !state.ActionLoading
            && Plugin.Settings.EnableConfirmedActions.Value
            && Plugin.Settings.AllowHarmlessTestActions.Value,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            toolbar,
            "REFRESH HISTORY",
            () =>
            {
                state.RefreshActionHistory();
                Invalidate(0.20f);
            },
            132f,
            !state.ActionLoading,
            height: 28f,
            fontSize: 11.5f);
        AddFlexibleSpace(toolbar);

        RenderInventoryTagActionControls(root, state);

        var split = HermesNativeUiFramework.CreateSplitView(root, 620f);
        var splitElement = split.Root.gameObject.AddComponent<LayoutElement>();
        splitElement.minHeight = 180f;
        splitElement.flexibleHeight = 1f;
        splitElement.flexibleWidth = 1f;

        AddVerticalLayout(split.Left, 6, 6, 6, 6, 4f);
        var confirmation = CreateScroll(split.Left, "action-confirmation", true);
        confirmation.Scroll.verticalNormalizedPosition = 1f;
        RenderActionConfirmation(confirmation.Content, state);
        RenderActionHistory(split.Right, state);
    }

    private void RenderInventoryTagActionControls(Transform parent, HermesNativeWorkspaceState state)
    {
        AddSectionHeader(parent, "INVENTORY TAGS");
        var controls = CreateToolbar(parent);
        AddButton(controls, "APPLY", () =>
        {
            state.TagActionMode = "apply";
            Invalidate(0.05f);
        }, 76f, selected: state.TagActionMode == "apply");
        AddButton(controls, "CHANGE", () =>
        {
            state.TagActionMode = "change";
            Invalidate(0.05f);
        }, 86f, selected: state.TagActionMode == "change");
        AddButton(controls, "RESET", () =>
        {
            state.TagActionMode = "remove";
            _tagColorDropdownOpen = false;
            Invalidate(0.05f);
        }, 86f, selected: state.TagActionMode == "remove");

        Button? proposeTagButton = null;
        var nameInput = AddInput(controls, "Tag name", state.TagDraftName, 160f);
        nameInput.interactable = state.TagActionMode != "remove";
        nameInput.onValueChanged.AddListener(value =>
        {
            state.TagDraftName = value;
            if (proposeTagButton != null)
            {
                proposeTagButton.interactable = CanProposeInventoryTagAction(state);
            }
        });

        AddTagColorDropdown(controls, state, state.TagActionMode != "remove");
        AddFlexibleSpace(controls);

        if (_tagColorDropdownOpen && state.TagActionMode != "remove")
        {
            RenderTagColorOptions(parent, state);
        }

        var selectedInstances = GetSelectedTagActionInstances(state);
        var selection = CreateToolbar(parent);
        AddToolbarLabel(selection, $"SELECTED {selectedInstances.Count:N0}");
        AddButton(
            selection,
            "SELECT MATCHING",
            () =>
            {
                state.SelectAllMatchingTagActionInstances();
                Invalidate(0.05f);
            },
            132f,
            state.StashInstances.Count > 0,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            selection,
            "CLEAR",
            () =>
            {
                state.ClearTagActionSelection();
                Invalidate(0.05f);
            },
            72f,
            state.SelectedTagActionInstanceKeys.Count > 0,
            height: 28f,
            fontSize: 11.5f);
        proposeTagButton = AddButton(
            selection,
            "PROPOSE TAG ACTION",
            () =>
            {
                state.ProposeInventoryTagAction();
                Invalidate(0.20f);
            },
            156f,
            CanProposeInventoryTagAction(state),
            height: 28f,
            fontSize: 11.5f,
            selected: selectedInstances.Count > 0);
        AddFlexibleSpace(selection);

        if (selectedInstances.Count > 0 && !IsTagSelectionReady(state.TagActionMode, selectedInstances))
        {
            var taggedCount = selectedInstances.Count(instance => !string.IsNullOrWhiteSpace(instance.TagName));
            var untaggedCount = selectedInstances.Count - taggedCount;
            var reason = state.TagActionMode switch
            {
                "apply" => "Apply is only available when every selected copy is currently untagged.",
                "change" => "Change is only available when every selected copy already has a tag.",
                "remove" => "Reset is only available when every selected copy already has a tag.",
                _ => "Choose a tag action mode."
            };
            AddCard(parent, "SELECTION NOT READY", $"{reason} Tagged {taggedCount:N0}, untagged {untaggedCount:N0}.", "ITEM ACTION");
        }

        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            AddEmptyState(parent, "Confirmed actions are disabled.", "Enable the Actions master toggle before proposing inventory tag changes.");
            return;
        }

        if (!Plugin.Settings.AllowInventoryTagActions.Value)
        {
            AddEmptyState(parent, "Inventory tag actions are disabled.", "Enable the individual inventory tag action setting before proposing tag changes.");
            return;
        }

        if (state.SelectedItem is null)
        {
            AddEmptyState(parent, "No item selected.", "Open Items & Market and select an owned item with matching stash copies.");
            return;
        }

        if (state.StashInstances.Count == 0)
        {
            AddEmptyState(parent, "No matching owned copies.", "The selected item has no visible matching PMC inventory copies to select.");
            return;
        }

        foreach (var instance in state.StashInstances.Take(10))
        {
            var selected = state.SelectedTagActionInstanceKeys.Contains(instance.InstanceKey);
            AddCard(
                parent,
                selected ? $"SELECTED: {instance.Label}" : instance.Label,
                $"{instance.Location} | Qty {FormatCount(instance.Quantity)} | {instance.ConditionDescription} | {FormatInstanceTag(instance)}",
                selected ? "CLICK TO REMOVE FROM TAG SELECTION" : "CLICK TO INCLUDE IN TAG SELECTION",
                () =>
                {
                    state.ToggleTagActionInstance(instance.InstanceKey);
                    Invalidate(0.05f);
                },
                selected ? new Color(0.11f, 0.16f, 0.13f, 0.88f) : HermesNativeUiFramework.RowColor);
        }
    }

    private static bool CanProposeInventoryTagAction(HermesNativeWorkspaceState state)
    {
        var selectedInstances = GetSelectedTagActionInstances(state);
        return !state.ActionLoading
               && Plugin.Settings.EnableConfirmedActions.Value
               && Plugin.Settings.AllowInventoryTagActions.Value
               && IsTagSelectionReady(state.TagActionMode, selectedInstances)
               && (state.TagActionMode == "remove" || !string.IsNullOrWhiteSpace(state.TagDraftName));
    }

    private static bool CanProposeInventoryTagActionForInstance(
        HermesNativeWorkspaceState state,
        HermesStashInstanceSummary instance,
        string mode,
        string tagName)
    {
        if (state.ActionLoading
            || !Plugin.Settings.EnableConfirmedActions.Value
            || !Plugin.Settings.AllowInventoryTagActions.Value
            || string.IsNullOrWhiteSpace(instance.InstanceKey))
        {
            return false;
        }

        var normalizedMode = NormalizeTagActionMode(mode);
        if (normalizedMode != "remove" && string.IsNullOrWhiteSpace(tagName))
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

    private static List<HermesStashInstanceSummary> GetSelectedTagActionInstances(HermesNativeWorkspaceState state)
        => state.StashInstances
            .Where(instance => state.SelectedTagActionInstanceKeys.Contains(instance.InstanceKey))
            .ToList();

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

    private static string FormatInstanceTag(HermesStashInstanceSummary instance)
        => string.IsNullOrWhiteSpace(instance.TagName)
            ? "Tag none"
            : string.IsNullOrWhiteSpace(instance.TagColor)
                ? $"Tag {instance.TagName}"
                : $"Tag {instance.TagName} / {TagColorLabel(NormalizeTagColor(instance.TagColor))}";

    private void AddTagColorDropdown(Transform parent, HermesNativeWorkspaceState state, bool interactable)
    {
        var selectedColor = NormalizeTagColor(state.TagDraftColor);
        if (!string.Equals(state.TagDraftColor, selectedColor, StringComparison.OrdinalIgnoreCase))
        {
            state.TagDraftColor = selectedColor;
        }

        AddButton(
            parent,
            $"COLOR: {TagColorLabel(selectedColor)}",
            () =>
            {
                _tagColorDropdownOpen = !_tagColorDropdownOpen;
                Invalidate(0.05f);
            },
            128f,
            interactable,
            height: 28f,
            fontSize: 11.5f,
            selected: _tagColorDropdownOpen);
    }

    private void RenderTagColorOptions(Transform parent, HermesNativeWorkspaceState state)
    {
        var colors = CreateToolbar(parent);
        AddToolbarLabel(colors, "COLOR");

        var current = NormalizeTagColor(state.TagDraftColor);
        foreach (var option in TagColorOptions)
        {
            AddButton(
                colors,
                option.Label,
                () =>
                {
                    state.TagDraftColor = option.Value;
                    _tagColorDropdownOpen = false;
                    Invalidate(0.05f);
                },
                76f,
                selected: string.Equals(current, option.Value, StringComparison.OrdinalIgnoreCase),
                height: 28f,
                fontSize: 11.5f);
        }

        AddFlexibleSpace(colors);
    }

    private void AddRowTagColorDropdown(Transform parent, bool interactable)
    {
        _tagEditorDraftColor = NormalizeTagColor(_tagEditorDraftColor);
        AddButton(
            parent,
            $"COLOR: {TagColorLabel(_tagEditorDraftColor)}",
            () =>
            {
                _tagColorDropdownOpen = !_tagColorDropdownOpen;
                Invalidate(0.05f);
            },
            128f,
            interactable,
            height: 28f,
            fontSize: 11.5f,
            selected: _tagColorDropdownOpen);
    }

    private void RenderRowTagColorOptions(Transform parent)
    {
        var colors = CreateToolbar(parent);
        AddToolbarLabel(colors, "COLOR");

        var current = NormalizeTagColor(_tagEditorDraftColor);
        foreach (var option in TagColorOptions)
        {
            AddButton(
                colors,
                option.Label,
                () =>
                {
                    _tagEditorDraftColor = option.Value;
                    _tagColorDropdownOpen = false;
                    Invalidate(0.05f);
                },
                76f,
                selected: string.Equals(current, option.Value, StringComparison.OrdinalIgnoreCase),
                height: 28f,
                fontSize: 11.5f);
        }

        AddFlexibleSpace(colors);
    }

    private void RenderActionConfirmation(RectTransform parent, HermesNativeWorkspaceState state)
    {
        AddVerticalLayout(parent, 6, 6, 6, 6, 4f);
        AddSectionHeader(parent, "CONFIRMATION WINDOW");

        var proposal = state.ActionProposal;
        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            AddEmptyState(parent, "Confirmed actions are disabled.", "Enable the master Actions setting before requesting proposals.");
            return;
        }

        if (proposal is null)
        {
            if (state.ActionResult is not null)
            {
                AddCard(
                    parent,
                    $"LAST RESULT: {state.ActionResult.Status}",
                    state.ActionResult.Message,
                    state.ActionResult.Executed ? "CONFIRMED" : state.ActionResult.Cancelled ? "CANCELLED" : "RESULT");
                return;
            }

            AddEmptyState(parent, "No pending action proposal.", "Propose a test action, inventory tag action, or craft collection to open the confirmation window.");
            return;
        }

        var preview = proposal.Preview;
        var previewLines = new[]
        {
            PreviewLine("ACTION", preview.ActionName),
            PreviewLine("ITEMS", preview.AffectedItems.Count == 0 ? "None" : string.Join(", ", preview.AffectedItems)),
            PreviewLine("QUANTITY", preview.Quantity),
            PreviewLine("PRICE / COST", preview.PriceOrCost),
            PreviewLine("DESTINATION", preview.TraderStationOrDestination),
            PreviewLine("EXPECTED RESULT", preview.ExpectedResult),
            PreviewLine("BLOCKED REASON", string.IsNullOrWhiteSpace(preview.CannotExecuteReason) ? "None" : preview.CannotExecuteReason!)
        };

        AddCard(
            parent,
            proposal.DisplayName,
            (proposal.IsHarmlessTestAction
                ? "Harmless verification action. No real inventory action will run."
                : "Inventory action proposal.")
            + "\n\n"
            + string.Join("\n", previewLines),
            proposal.CanExecute ? "TOKEN ACTIVE" : "BLOCKED",
            null,
            proposal.CanExecute
                ? new Color(0.08f, 0.12f, 0.11f, 0.82f)
                : new Color(0.20f, 0.08f, 0.07f, 0.82f));

        AddSectionHeader(parent, "WARNINGS");
        if (preview.Warnings.Count == 0)
        {
            AddText(parent, "None", 12f, false, HermesNativeUiFramework.MutedTextColor);
        }
        else
        {
            foreach (var warning in preview.Warnings)
            {
                AddCard(
                    parent,
                    "WARNING",
                    warning,
                    "CONFIRMATION PREVIEW",
                    null,
                    new Color(0.22f, 0.145f, 0.055f, 0.82f));
            }
        }

        var actions = CreateToolbar(parent);
        AddButton(
            actions,
            "CANCEL",
            () =>
            {
                state.CancelAction();
                Invalidate(0.20f);
            },
            96f,
            !state.ActionLoading,
            height: 30f);
        AddButton(
            actions,
            "CONFIRM",
            () =>
            {
                state.ConfirmAction();
                Invalidate(0.20f);
            },
            104f,
            !state.ActionLoading && proposal.CanExecute,
            height: 30f,
            selected: true);
        AddFlexibleSpace(actions);

        if (state.ActionResult is not null)
        {
            AddCard(
                parent,
                $"LAST RESULT: {state.ActionResult.Status}",
                state.ActionResult.Message,
                state.ActionResult.Executed ? "CONFIRMED" : state.ActionResult.Cancelled ? "CANCELLED" : "RESULT");
        }
    }

    private void RenderActionConfirmationPopout(RectTransform parent)
    {
        var state = _state;
        if (state?.ActionProposal is null)
        {
            return;
        }

        var overlay = new GameObject("ActionConfirmationPopout", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        overlay.transform.SetParent(parent, false);
        var overlayRect = (RectTransform)overlay.transform;
        HermesNativeUiFramework.Stretch(overlayRect, 0f, 0f, 0f, 0f);
        var overlayImage = overlay.GetComponent<Image>();
        overlayImage.color = new Color(0f, 0f, 0f, 0.58f);
        overlayImage.raycastTarget = true;

        var frame = CreatePanel(overlay.transform, "ActionConfirmationFrame", new Color(0.035f, 0.042f, 0.043f, 0.98f));
        frame.anchorMin = new Vector2(0.16f, 0.08f);
        frame.anchorMax = new Vector2(0.84f, 0.92f);
        frame.offsetMin = Vector2.zero;
        frame.offsetMax = Vector2.zero;
        AddVerticalLayout(frame, 8, 8, 8, 8, 5f);

        var scroll = CreateScroll(frame, "action-confirmation-popout", true);
        RenderActionConfirmation(scroll.Content, state);
    }

    private static string PreviewLine(string label, string value)
        => $"{label}: {value}";

    private void RenderActionHistory(RectTransform parent, HermesNativeWorkspaceState state)
    {
        AddVerticalLayout(parent, 6, 6, 6, 6, 4f);
        var header = CreateToolbar(parent);
        AddToolbarLabel(header, "BASIC ACTION HISTORY");
        AddFlexibleSpace(header);

        var scroll = CreateScroll(parent, "action-history", true);
        var history = state.ActionHistory;
        var entries = history?.Entries ?? [];
        if (entries.Count == 0)
        {
            AddEmptyState(
                scroll.Content,
                "No resolved actions yet.",
                history?.Message ?? "Confirmed, cancelled, expired, and blocked actions will appear here.");
            return;
        }

        foreach (var entry in entries.Take(MaximumRowsPerSection))
        {
            var preview = entry.Preview;
            var body = $"{entry.Message}\n"
                       + $"Action: {preview.ActionName}\n"
                       + $"Items: {(preview.AffectedItems.Count == 0 ? "None" : string.Join(", ", preview.AffectedItems))}\n"
                       + $"Quantity: {preview.Quantity} | Cost: {preview.PriceOrCost} | Destination: {preview.TraderStationOrDestination}";
            AddCard(
                scroll.Content,
                $"{entry.Status}: {entry.DisplayName}",
                body,
                $"{(entry.HarmlessTestAction ? "HARMLESS TEST" : "INVENTORY ACTION")} | {FormatUnixTime(entry.ResolvedUnixTime)}",
                null,
                entry.Executed
                    ? new Color(0.08f, 0.13f, 0.10f, 0.82f)
                    : HermesNativeUiFramework.RowColor);
        }
    }

    #endregion
}
