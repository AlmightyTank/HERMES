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
    #region Assistant

    private void RenderAssistant(RectTransform parent)
    {
        var state = _state!;
        var root = CreateVerticalRoot(parent);
        var assistantBusy = state.AssistantLoading
                            || state.NoticesLoading
                            || state.WorkspaceInitialLoading;

        AddStatusStrip(
            root,
            state.AssistantOverviewStatus,
            assistantBusy,
            state.RefreshAssistantFromCache,
            "REFRESH");

        AddAssistantSummary(root, state);

        var split = HermesNativeUiFramework.CreateSplitView(root, 334f);
        var splitElement = split.Root.gameObject.AddComponent<LayoutElement>();
        splitElement.minHeight = 210f;
        splitElement.flexibleHeight = 1f;
        splitElement.flexibleWidth = 1f;

        RenderAssistantNotices(split.Left, state);
        RenderAssistantConversation(split.Right, state);
        RenderAssistantComposer(root, state);
    }

    private void AddAssistantSummary(Transform parent, HermesNativeWorkspaceState state)
    {
        AddMetricGrid(
            parent,
            ("PROFILE DATA", state.WorkspaceReady ? "READY" : state.WorkspaceInitialLoading ? "PREPARING" : "WAITING"),
            ("ACTIVE ALERTS", state.ActiveNoticeCount.ToString("N0")),
            ("CONVERSATION", state.AssistantMessages.Count.ToString("N0")),
            ("CONTEXT", state.AssistantContextLabel));

        var quickActions = CreateToolbar(parent);
        AddToolbarLabel(quickActions, "QUICK OPEN");
        AddButton(
            quickActions,
            "LOADOUT",
            () => state.Navigate("Loadout"),
            82f,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            quickActions,
            "RAID PLAN",
            () => state.Navigate("RaidPlanner"),
            88f,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            quickActions,
            "CRAFTS",
            () => state.Navigate("Crafts"),
            76f,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            quickActions,
            "HIDEOUT",
            () => state.Navigate("Hideout"),
            82f,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            quickActions,
            "STASH",
            () => state.Navigate("Stash"),
            70f,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            quickActions,
            "CLEAR CONTEXT",
            state.ClearAssistantContext,
            122f,
            height: 28f,
            fontSize: 11.5f);
        AddFlexibleSpace(quickActions);
    }

    private void RenderAssistantNotices(RectTransform parent, HermesNativeWorkspaceState state)
    {
        AddVerticalLayout(parent, 6, 6, 6, 6, 4f);

        var activeNotices = state.Notices
            .Where(notice => !notice.Dismissed)
            .Take(6)
            .ToList();

        var header = CreateToolbar(parent);
        AddToolbarLabel(
            header,
            activeNotices.Count == 0
                ? "ACTIONABLE ALERTS"
                : $"ACTIONABLE ALERTS  {state.ActiveNoticeCount:N0}");
        AddFlexibleSpace(header);
        AddButton(
            header,
            state.NoticesLoading ? "CHECKING" : "CHECK",
            state.CheckPreparedAssistantFeed,
            76f,
            !state.NoticesLoading,
            height: 28f,
            fontSize: 11.5f);
        AddButton(
            header,
            "CLEAR",
            state.ClearNotices,
            58f,
            state.ActiveNoticeCount > 0,
            height: 28f,
            fontSize: 11.5f);

        var scroll = CreateScroll(parent, "assistant-notices", true);
        scroll.Root.GetComponent<LayoutElement>().minHeight = 150f;

        if (activeNotices.Count == 0)
        {
            AddEmptyState(
                scroll.Content,
                state.NoticesLoading ? "Checking the active PMC profile..." : "No actionable alerts.",
                string.IsNullOrWhiteSpace(state.NoticeStatus)
                    ? "HERMES will surface profile conditions that need attention here."
                    : state.NoticeStatus);
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.NoticeStatus))
        {
            AddText(
                scroll.Content,
                state.NoticeStatus,
                12f,
                false,
                HermesNativeUiFramework.MutedTextColor);
        }

        foreach (var notice in activeNotices)
        {
            var card = AddCard(
                scroll.Content,
                notice.Title,
                AssistantPreview(notice.Message, 210),
                $"{notice.Severity.ToUpperInvariant()} • {notice.Category.ToUpperInvariant()}",
                () => state.OpenNotice(notice),
                AssistantSeverityCardColor(notice.Severity),
                () =>
                {
                    state.DismissNotice(notice);
                    Invalidate();
                });

            var actions = CreateToolbar(card);
            AddFlexibleSpace(actions);
            AddButton(
                actions,
                "OPEN",
                () => state.OpenNotice(notice),
                58f,
                height: 28f,
                fontSize: 11.5f);
            AddButton(
                actions,
                "DISMISS",
                () =>
                {
                    state.DismissNotice(notice);
                    Invalidate();
                },
                72f,
                height: 28f,
                fontSize: 11.5f);
        }

        var hiddenCount = Math.Max(0, state.ActiveNoticeCount - activeNotices.Count);
        if (hiddenCount > 0)
        {
            AddText(
                scroll.Content,
                $"+{hiddenCount:N0} more active alert(s). Use the Assistant page after dismissing or opening the highest-priority items.",
                11.5f,
                false,
                HermesNativeUiFramework.MutedTextColor);
        }
    }

    private void RenderAssistantConversation(RectTransform parent, HermesNativeWorkspaceState state)
    {
        AddVerticalLayout(parent, 6, 6, 6, 6, 4f);

        var header = CreateToolbar(parent);
        AddToolbarLabel(header, "CONVERSATION");
        AddFlexibleSpace(header);
        AddButton(
            header,
            "CLEAR CHAT",
            () =>
            {
                state.ClearAssistant();
                _assistantDraft = string.Empty;
                Invalidate();
            },
            88f,
            state.AssistantMessages.Count > 0,
            height: 28f,
            fontSize: 11.5f);

        var conversation = CreateScroll(parent, "assistant-conversation", true);
        conversation.Root.GetComponent<LayoutElement>().minHeight = 150f;

        if (state.AssistantLoading)
        {
            AddCard(
                conversation.Content,
                "HERMES IS WORKING",
                string.IsNullOrWhiteSpace(state.AssistantStatus)
                    ? "Preparing a response from the current profile context..."
                    : state.AssistantStatus,
                "CURRENT REQUEST",
                null,
                new Color(0.12f, 0.14f, 0.13f, 0.86f));
        }
        else if (LooksLikeAssistantFailure(state.AssistantStatus))
        {
            AddCard(
                conversation.Content,
                "ASSISTANT NEEDS ATTENTION",
                state.AssistantStatus,
                "THE EXISTING PROFILE SNAPSHOT REMAINS AVAILABLE",
                null,
                new Color(0.20f, 0.08f, 0.07f, 0.80f));
        }

        foreach (var message in state.AssistantMessages)
        {
            var card = AddCard(
                conversation.Content,
                message.IsUser ? "YOU" : "HERMES",
                message.Text,
                message.Source,
                null,
                message.IsUser
                    ? new Color(0.08f, 0.10f, 0.10f, 0.78f)
                    : HermesNativeUiFramework.RowColor);

            AddAssistantActionRows(card, state, message.Actions);
        }

        if (state.AssistantMessages.Count == 0 && !state.AssistantLoading)
        {
            AddEmptyState(
                conversation.Content,
                "No conversation yet.",
                "Ask about the active PMC profile, selected item, loadout, raid plan, hideout, crafts, or stash.");
        }
    }

    private void AddAssistantActionRows(
        Transform parent,
        HermesNativeWorkspaceState state,
        IReadOnlyList<HermesNativeAssistantActionData> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        const int actionsPerRow = 3;
        for (var offset = 0; offset < actions.Count; offset += actionsPerRow)
        {
            var row = CreateToolbar(parent);
            var count = Math.Min(actionsPerRow, actions.Count - offset);
            for (var index = 0; index < count; index++)
            {
                var action = actions[offset + index];
                var label = NormalizeAssistantActionLabel(action.Label);
                AddButton(
                    row,
                    label,
                    () => state.Navigate(action.TabName),
                    AssistantActionButtonWidth(label),
                    height: 28f,
                    fontSize: 11.5f);
            }

            AddFlexibleSpace(row);
        }
    }

    private void RenderAssistantComposer(Transform parent, HermesNativeWorkspaceState state)
    {
        if (Plugin.Settings.ShowAssistantSuggestedPrompts.Value)
        {
            var suggestions = state.AssistantSuggestedPrompts.Take(3).ToList();
            if (suggestions.Count > 0)
            {
                var suggestionRow = CreateToolbar(parent);
                AddToolbarLabel(suggestionRow, "FOLLOW-UP");
                foreach (var prompt in suggestions)
                {
                    AddButton(
                        suggestionRow,
                        prompt,
                        () =>
                        {
                            _assistantDraft = string.Empty;
                            state.SubmitAssistant(prompt);
                            Invalidate(0.25f);
                        },
                        246f,
                        !state.AssistantLoading,
                        height: 28f,
                        fontSize: 11f);
                }

                AddFlexibleSpace(suggestionRow);
            }
        }

        var composer = CreatePanel(parent, "AssistantComposer", HermesNativeUiFramework.HeaderColor);
        var composerLayout = composer.gameObject.AddComponent<HorizontalLayoutGroup>();
        composerLayout.padding = new RectOffset(7, 7, 4, 4);
        composerLayout.spacing = 6f;
        composerLayout.childControlWidth = true;
        composerLayout.childControlHeight = true;
        composerLayout.childForceExpandWidth = false;
        composerLayout.childForceExpandHeight = false;

        var composerElement = composer.gameObject.AddComponent<LayoutElement>();
        composerElement.minHeight = 40f;
        composerElement.preferredHeight = 40f;
        composerElement.flexibleHeight = 0f;

        var placeholder = state.SelectedItem is null
            ? "ASK HERMES ABOUT THE CURRENT PROFILE"
            : $"ASK ABOUT {state.SelectedItem.Name.ToUpperInvariant()}";

        var input = AddInput(composer, placeholder, _assistantDraft, 120f);
        input.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        input.onValueChanged.AddListener(value => _assistantDraft = value);
        input.onSubmit.AddListener(_ => SubmitAssistant(input));

        AddButton(
            composer,
            state.AssistantLoading ? "WORKING" : "ASK",
            () => SubmitAssistant(input),
            66f,
            !state.AssistantLoading,
            height: 28f,
            fontSize: 11.5f);
    }

    private void SubmitAssistant(TMP_InputField input)
    {
        var prompt = input.text.Trim();
        if (prompt.Length == 0 || _state is null)
        {
            return;
        }

        _assistantDraft = string.Empty;
        input.SetTextWithoutNotify(string.Empty);
        _state.SubmitAssistant(prompt);
        Invalidate(0.25f);
    }

    private static string AssistantPreview(string value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No additional detail was supplied.";
        }

        var compact = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maximumCharacters
            ? compact
            : compact[..Math.Max(1, maximumCharacters - 1)].TrimEnd() + "…";
    }

    private static string NormalizeAssistantActionLabel(string label)
    {
        var normalized = string.IsNullOrWhiteSpace(label)
            ? "OPEN"
            : string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Trim()
                .TrimEnd('.', '…')
                .ToUpperInvariant();

        return normalized switch
        {
            "OPEN RAIDPLANNER" => "OPEN RAID PLAN",
            "OPEN RAID PLANNER" => "OPEN RAID PLAN",
            _ => normalized
        };
    }

    private static bool LooksLikeAssistantFailure(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
               || status.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
               || status.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
               || status.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
               || status.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Color AssistantSeverityCardColor(string severity)
    {
        if (severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.23f, 0.075f, 0.065f, 0.84f);
        }

        if (severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.22f, 0.145f, 0.055f, 0.82f);
        }

        return HermesNativeUiFramework.RowColor;
    }

    #endregion
}
