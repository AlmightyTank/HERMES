using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Actions

    private void DrawActionsTab()
    {
        HermesUi.DrawPanelTitle(
            "CONFIRMED ACTIONS",
            "Permission-gated action proposals, confirmation tokens, results, and history.",
            _actionStatus,
            _actionLoading);

        GUILayout.BeginHorizontal();
        GUI.enabled = !_actionLoading
                      && Plugin.Settings.EnableConfirmedActions.Value
                      && Plugin.Settings.AllowHarmlessTestActions.Value;
        if (GUILayout.Button("Create harmless test proposal", GUILayout.Height(30f)))
        {
            _ = ProposeTestActionAsync();
        }
        GUI.enabled = !_actionLoading;
        if (GUILayout.Button("Refresh history", GUILayout.Width(130f), GUILayout.Height(30f)))
        {
            _ = RefreshActionHistoryAsync();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(HermesUi.StandardSpace);
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("INVENTORY TAG ACTION");
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_tagActionMode == "apply", "Apply", GUI.skin.button, GUILayout.Width(90f)))
        {
            TagActionMode = "apply";
        }
        if (GUILayout.Toggle(_tagActionMode == "change", "Change", GUI.skin.button, GUILayout.Width(90f)))
        {
            TagActionMode = "change";
        }
        if (GUILayout.Toggle(_tagActionMode == "remove", "Remove", GUI.skin.button, GUILayout.Width(90f)))
        {
            TagActionMode = "remove";
        }
        GUILayout.EndHorizontal();
        GUI.enabled = _tagActionMode != "remove";
        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(52f));
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
        GUI.enabled = true;

        GUILayout.Label($"Selected matching copies: {_selectedTagActionInstanceKeys.Count:N0}");
        GUILayout.BeginHorizontal();
        GUI.enabled = _stashInstances.Count > 0;
        if (GUILayout.Button("Select shown matching copies", GUILayout.Height(28f)))
        {
            SelectAllMatchingTagActionInstances();
        }
        GUI.enabled = _selectedTagActionInstanceKeys.Count > 0;
        if (GUILayout.Button("Clear selected copies", GUILayout.Width(150f), GUILayout.Height(28f)))
        {
            ClearTagActionSelection();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        foreach (var instance in _stashInstances.Take(8))
        {
            var selected = _selectedTagActionInstanceKeys.Contains(instance.InstanceKey);
            if (GUILayout.Toggle(selected, $"{instance.Label} • {instance.Location} • Qty {instance.Quantity:N0}", GUI.skin.button))
            {
                if (!selected)
                {
                    ToggleTagActionInstance(instance.InstanceKey);
                }
            }
            else if (selected)
            {
                ToggleTagActionInstance(instance.InstanceKey);
            }
        }

        GUI.enabled = !_actionLoading
                      && Plugin.Settings.EnableConfirmedActions.Value
                      && Plugin.Settings.AllowInventoryTagActions.Value
                      && _selectedTagActionInstanceKeys.Count > 0
                      && (_tagActionMode == "remove" || !string.IsNullOrWhiteSpace(_tagDraftName));
        if (GUILayout.Button("Create inventory tag proposal", GUILayout.Height(30f)))
        {
            _ = ProposeInventoryTagActionAsync();
        }
        GUI.enabled = true;
        GUILayout.EndVertical();

        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            HermesUi.DrawWarning("Confirmed actions are disabled by the master toggle.");
        }
        else if (!Plugin.Settings.AllowHarmlessTestActions.Value)
        {
            HermesUi.DrawWarning("The harmless test action is disabled in the Actions settings.");
        }
        else if (!Plugin.Settings.AllowInventoryTagActions.Value)
        {
            HermesUi.DrawWarning("Inventory tag actions are disabled in the Actions settings.");
        }

        if (_actionProposal is not null)
        {
            GUILayout.Space(HermesUi.StandardSpace);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("CONFIRMATION WINDOW");
            GUILayout.Label($"Action: {_actionProposal.Preview.ActionName}");
            GUILayout.Label($"Item(s): {string.Join(", ", _actionProposal.Preview.AffectedItems)}");
            GUILayout.Label($"Quantity: {_actionProposal.Preview.Quantity}");
            GUILayout.Label($"Price / cost: {_actionProposal.Preview.PriceOrCost}");
            GUILayout.Label($"Trader / station / destination: {_actionProposal.Preview.TraderStationOrDestination}");
            GUILayout.Label($"Expected result: {_actionProposal.Preview.ExpectedResult}");
            foreach (var warning in _actionProposal.Preview.Warnings)
            {
                GUILayout.Label("Warning: " + warning);
            }
            if (!string.IsNullOrWhiteSpace(_actionProposal.Preview.CannotExecuteReason))
            {
                HermesUi.DrawWarning(_actionProposal.Preview.CannotExecuteReason);
            }
            GUILayout.Label($"Token expires in {ActionSecondsRemaining(_actionProposal):N0}s.");
            GUILayout.BeginHorizontal();
            GUI.enabled = !_actionLoading;
            if (GUILayout.Button("Cancel", GUILayout.Width(120f), GUILayout.Height(30f)))
            {
                _ = CancelActionAsync();
            }
            GUI.enabled = !_actionLoading && _actionProposal.CanExecute;
            if (GUILayout.Button("Confirm", GUILayout.Width(120f), GUILayout.Height(30f)))
            {
                _ = ConfirmActionAsync();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        if (_actionResult is not null)
        {
            GUILayout.Space(HermesUi.StandardSpace);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"LAST RESULT: {_actionResult.Status}");
            GUILayout.Label(_actionResult.Message);
            GUILayout.EndVertical();
        }

        GUILayout.Space(HermesUi.StandardSpace);
        GUILayout.Label("BASIC ACTION HISTORY");
        var entries = _actionHistory?.Entries ?? [];
        if (entries.Count == 0)
        {
            GUILayout.Label(_actionHistory?.Message ?? "No actions have been resolved in this session.");
            return;
        }

        foreach (var entry in entries.Take(10))
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"{entry.Status}: {entry.DisplayName}");
            GUILayout.Label(entry.Message);
            GUILayout.Label(entry.HarmlessTestAction ? "Harmless test action" : "Inventory action");
            GUILayout.EndVertical();
        }
    }

    private void DrawActionConfirmationPopout(Rect parentRect)
    {
        if (_actionProposal is null)
        {
            return;
        }

        var width = Mathf.Min(720f, parentRect.width - 80f);
        var height = Mathf.Min(560f, parentRect.height - 70f);
        var rect = new Rect(
            (parentRect.width - width) * 0.5f,
            (parentRect.height - height) * 0.5f,
            width,
            height);

        GUI.Box(new Rect(0f, 0f, parentRect.width, parentRect.height), GUIContent.none);
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label("CONFIRM ACTION");
        GUILayout.Label($"Action: {_actionProposal.Preview.ActionName}");
        GUILayout.Label($"Item(s): {string.Join(", ", _actionProposal.Preview.AffectedItems)}");
        GUILayout.Label($"Quantity: {_actionProposal.Preview.Quantity}");
        GUILayout.Label($"Price / cost: {_actionProposal.Preview.PriceOrCost}");
        GUILayout.Label($"Target: {_actionProposal.Preview.TraderStationOrDestination}");
        GUILayout.Label($"Expected result: {_actionProposal.Preview.ExpectedResult}");
        foreach (var warning in _actionProposal.Preview.Warnings)
        {
            GUILayout.Label("Warning: " + warning);
        }
        if (!string.IsNullOrWhiteSpace(_actionProposal.Preview.CannotExecuteReason))
        {
            HermesUi.DrawWarning(_actionProposal.Preview.CannotExecuteReason);
        }
        GUILayout.FlexibleSpace();
        GUILayout.Label($"Token expires in {ActionSecondsRemaining(_actionProposal):N0}s.");
        GUILayout.BeginHorizontal();
        GUI.enabled = !_actionLoading;
        if (GUILayout.Button("Cancel", GUILayout.Width(120f), GUILayout.Height(30f)))
        {
            _ = CancelActionAsync();
        }
        GUI.enabled = !_actionLoading && _actionProposal.CanExecute;
        if (GUILayout.Button("Confirm", GUILayout.Width(120f), GUILayout.Height(30f)))
        {
            _ = ConfirmActionAsync();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    internal async Task ProposeTestActionAsync()
    {
        if (_actionLoading)
        {
            return;
        }

        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            _actionStatus = "Confirmed actions are disabled by the master toggle.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        if (!Plugin.Settings.AllowHarmlessTestActions.Value)
        {
            _actionStatus = "The harmless test action is disabled.";
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        _actionLoading = true;
        _actionStatus = "Requesting a harmless test action proposal...";
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        try
        {
            var response = await HermesApiClient.ProposeTestActionAsync();
            _actionProposal = response.Proposal;
            _actionResult = null;
            _actionStatus = response.Found && response.Proposal is not null
                ? response.Message ?? "Confirmation window ready."
                : response.Message ?? "HERMES could not create a test action proposal.";
            await RefreshActionHistoryAsync(setLoading: false);
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Test action proposal");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _actionLoading = false;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    internal async Task ProposeInventoryTagActionAsync()
        => await ProposeInventoryTagActionAsync(
            _tagActionMode,
            _tagDraftName,
            _tagDraftColor,
            _selectedTagActionInstanceKeys);

    internal async Task ProposeInventoryTagActionAsync(
        string mode,
        string tagName,
        string tagColor,
        IEnumerable<string> instanceKeys)
    {
        if (_actionLoading)
        {
            return;
        }

        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            _actionStatus = "Confirmed actions are disabled by the master toggle.";
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        if (!Plugin.Settings.AllowInventoryTagActions.Value)
        {
            _actionStatus = "Inventory tag actions are disabled in the Actions settings.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        var normalizedMode = NormalizeTagActionMode(mode);
        var selectedKeys = instanceKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedKeys.Length == 0)
        {
            _actionStatus = "Choose an owned copy before proposing an inventory tag action.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        if (!normalizedMode.Equals("remove", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(tagName))
        {
            _actionStatus = "Enter a tag name before proposing an inventory tag action.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        var selectedInstances = _stashInstances
            .Where(instance => selectedKeys.Contains(instance.InstanceKey, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (!IsTagSelectionReady(normalizedMode, selectedInstances))
        {
            _actionStatus = normalizedMode switch
            {
                "apply" => "Apply tag is only available on untagged owned copies.",
                "change" => "Change tag is only available on owned copies that already have a tag.",
                "remove" => "Reset tag is only available on owned copies that already have a tag.",
                _ => "Choose a tag action."
            };
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        _tagActionMode = normalizedMode;
        _tagDraftName = tagName ?? string.Empty;
        _tagDraftColor = NormalizeTagColor(tagColor);
        _selectedTagActionInstanceKeys.Clear();
        foreach (var key in selectedKeys)
        {
            _selectedTagActionInstanceKeys.Add(key);
        }

        _actionLoading = true;
        _actionStatus = "Requesting an inventory tag action proposal...";
        _detailStatus = _actionStatus;
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        try
        {
            var response = await HermesApiClient.ProposeInventoryTagActionAsync(
                normalizedMode,
                _tagDraftName.Trim(),
                _tagDraftColor,
                selectedKeys);
            _actionProposal = response.Proposal;
            _actionResult = null;
            _actionStatus = response.Found && response.Proposal is not null
                ? response.Message ?? "Inventory tag confirmation window ready."
                : response.Message ?? "HERMES could not create an inventory tag action proposal.";
            _detailStatus = _actionStatus;
            await RefreshActionHistoryAsync(setLoading: false);
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Inventory tag action proposal");
            _detailStatus = _actionStatus;
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _actionLoading = false;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    internal async Task ProposeCraftCollectActionAsync(
        IEnumerable<string> productionKeys,
        bool collectAllCompleted = false)
    {
        if (_actionLoading)
        {
            return;
        }

        if (!Plugin.Settings.EnableConfirmedActions.Value)
        {
            _actionStatus = "Confirmed actions are disabled by the master toggle.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        if (!Plugin.Settings.AllowCraftActions.Value)
        {
            _actionStatus = "Craft collection actions are disabled in the Actions settings.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        var keys = productionKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!collectAllCompleted && keys.Length == 0)
        {
            _actionStatus = "Select a completed craft before proposing collection.";
            _detailStatus = _actionStatus;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
            return;
        }

        _actionLoading = true;
        _actionStatus = collectAllCompleted
            ? "Requesting a collect-all completed crafts proposal..."
            : "Requesting a completed craft collection proposal...";
        _detailStatus = _actionStatus;
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        try
        {
            var response = await HermesApiClient.ProposeCraftCollectActionAsync(keys, collectAllCompleted);
            _actionProposal = response.Proposal;
            _actionResult = null;
            _actionStatus = response.Found && response.Proposal is not null
                ? response.Message ?? "Craft collection confirmation window ready."
                : response.Message ?? "HERMES could not create a craft collection proposal.";
            _detailStatus = _actionStatus;
            await RefreshActionHistoryAsync(setLoading: false);
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Craft collection action proposal");
            _detailStatus = _actionStatus;
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _actionLoading = false;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    internal async Task ConfirmActionAsync()
    {
        if (_actionLoading || _actionProposal is null)
        {
            return;
        }

        _actionLoading = true;
        _actionStatus = "Confirming action with one-time token...";
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        var pendingTagMutation = BuildPendingClientTagMutation();
        try
        {
            var result = await HermesApiClient.ConfirmActionAsync(
                _actionProposal.ProposalId,
                _actionProposal.ConfirmationToken);
            _actionResult = result;
            _actionStatus = result.Message;
            _detailStatus = _actionStatus;
            _actionProposal = result.Found && !result.Executed && !result.Cancelled && !result.Expired
                ? result.Proposal
                : null;
            if (result.Executed)
            {
                ApplyConfirmedTagMutationToClient(pendingTagMutation);
            }

            if (result.Executed && _activeTab == HermesTab.ItemSearch)
            {
                await RefreshItemSearchDataAsync();
            }
            if (result.Executed && _activeTab == HermesTab.Crafts)
            {
                await _craftPanel.RefreshFromServerAsync(invalidateMarketCache: false, force: true);
            }
            await RefreshActionHistoryAsync(setLoading: false);
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Action confirmation");
            _detailStatus = _actionStatus;
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _actionLoading = false;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    internal async Task CancelActionAsync()
    {
        if (_actionLoading || _actionProposal is null)
        {
            return;
        }

        _actionLoading = true;
        _actionStatus = "Cancelling action proposal...";
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        try
        {
            var result = await HermesApiClient.CancelActionAsync(
                _actionProposal.ProposalId,
                _actionProposal.ConfirmationToken);
            _actionResult = result;
            _actionStatus = result.Message;
            _detailStatus = _actionStatus;
            _actionProposal = null;
            await RefreshActionHistoryAsync(setLoading: false);
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Action cancellation");
            _detailStatus = _actionStatus;
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _actionLoading = false;
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }
    }

    internal Task RefreshActionHistoryAsync()
        => RefreshActionHistoryAsync(setLoading: true);

    private async Task RefreshActionHistoryAsync(bool setLoading)
    {
        if (setLoading)
        {
            _actionLoading = true;
            _actionStatus = "Loading action history...";
            HermesNativeWorkspaceRuntime.RequestClientRefresh();
        }

        try
        {
            _actionHistory = await HermesApiClient.GetActionHistoryAsync();
            if (setLoading)
            {
                _actionStatus = _actionHistory.Message ?? $"Loaded {_actionHistory.TotalActions:N0} action history row(s).";
            }
        }
        catch (Exception ex)
        {
            _actionStatus = HermesApiClient.DescribeFailure(ex, "Action history");
            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (setLoading)
            {
                _actionLoading = false;
                HermesNativeWorkspaceRuntime.RequestClientRefresh();
            }
        }
    }

    private void ClearActionState()
    {
        _actionProposal = null;
        _actionResult = null;
        _actionHistory = null;
        _selectedTagActionInstanceKeys.Clear();
        _actionStatus = "Action confirmation pipeline ready. HERMES 1.2 supports harmless tests, confirmed inventory tag actions, and confirmed completed craft collection.";
        _actionLoading = false;
        HermesNativeWorkspaceRuntime.RequestClientRefresh();
    }

    private static int ActionSecondsRemaining(HermesActionProposal proposal)
        => (int)Math.Max(0L, proposal.ExpiresUnixTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    #endregion
}
