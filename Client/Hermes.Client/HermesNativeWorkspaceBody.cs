using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Fully native uGUI renderer for every HERMES workspace. No workspace content is drawn
/// through IMGUI, so EFT messenger, confirmation dialogs, notifications, and modal windows
/// share one predictable Canvas ordering path with the HERMES workspace rail.
/// </summary>
internal sealed class HermesNativeWorkspaceBody : MonoBehaviour
{
    private const float SyncInterval = 0.20f;
    private static int MaximumRowsPerSection => Plugin.Settings.GetMaximumRowsPerSection();
    private const float CompactToolbarHeight = 36f;
    private const float CompactControlHeight = 32f;
    private const float CompactMetricHeight = 46f;
    private const float CompactSectionHeight = 30f;

    private readonly Dictionary<string, float> _savedScrollPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScrollRect> _activeScrolls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameObject> _workspaceRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workspaceRootFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ScrollRect>> _workspaceScrolls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedRaidMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _itemDetailSectionExpansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scrollsToForceTop = new(StringComparer.Ordinal);

    private HermesNativeWorkspaceState? _state;
    private RectTransform? _root;
    private GameObject? _contentRoot;
    private string _lastFingerprint = string.Empty;
    private float _nextSyncAt;
    private int _lastClientRefreshRevision;
    private bool _forceRebuild;

    private string _assistantDraft = string.Empty;
    private string _hideoutSearch = string.Empty;
    private string _hideoutFilter = "ALL";
    private string _craftSearch = string.Empty;
    private string _craftFilter = "ALL";
    private bool _craftAvailableOnly;
    private int _craftFocusRevision;
    private readonly HashSet<string> _craftFocusKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _stashSearch = string.Empty;
    private string _stashView = "OVERVIEW";
    private string _loadoutView = "OVERVIEW";
    private string _raidSearch = string.Empty;
    private string _lastItemResultSetKey = string.Empty;
    private string _lastSelectedItemKey = string.Empty;

    private static NativeFleaCheckboxStyle? _nativeFleaCheckboxStyle;
    private static bool _nativeFleaCheckboxProbeLogged;

    internal void Initialize(HermesWindow window)
    {
        _state = new HermesNativeWorkspaceState(window);
        _root = transform as RectTransform
                ?? throw new InvalidOperationException("HERMES native body requires a RectTransform.");

        var image = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
        image.color = HermesNativeUiFramework.PanelColor;
        image.raycastTarget = true;

        _forceRebuild = true;
        _lastClientRefreshRevision = HermesNativeWorkspaceRuntime.ClientRefreshRevision;
        _lastFingerprint = _state.BuildFingerprint();
        Rebuild(force: true);
    }

    private void OnEnable()
    {
        _forceRebuild = true;
        _nextSyncAt = 0f;
    }

    private void Update()
    {
        if (_state is null || !HermesNativeWorkspaceRuntime.Active)
        {
            return;
        }

        var clientRefreshRevision = HermesNativeWorkspaceRuntime.ClientRefreshRevision;
        var clientRefreshRequested = clientRefreshRevision != _lastClientRefreshRevision;
        if (!clientRefreshRequested && Time.unscaledTime < _nextSyncAt)
        {
            return;
        }

        if (clientRefreshRequested)
        {
            _lastClientRefreshRevision = clientRefreshRevision;
            _forceRebuild = true;
        }

        _nextSyncAt = Time.unscaledTime + SyncInterval;
        var fingerprint = _state.BuildFingerprint();
        if (_forceRebuild || !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            var force = _forceRebuild;
            _lastFingerprint = fingerprint;
            _forceRebuild = false;
            Rebuild(force);
        }
    }

    private void Rebuild(bool force)
    {
        if (_state is null || _root is null)
        {
            return;
        }

        var activeTab = _state.ActiveTab;
        if (activeTab == "Crafts")
        {
            ApplyPendingCraftFocus();
        }

        var itemResultSetKey = activeTab == "ItemSearch"
            ? BuildItemResultSetKey()
            : _lastItemResultSetKey;
        var selectedItemKey = activeTab == "ItemSearch"
            ? _state.SelectedItem?.ItemKey ?? string.Empty
            : _lastSelectedItemKey;
        var resetItemResultsToTop = activeTab == "ItemSearch"
                                    && !string.Equals(itemResultSetKey, _lastItemResultSetKey, StringComparison.Ordinal);
        var resetItemDetailsToTop = activeTab == "ItemSearch"
                                    && (resetItemResultsToTop
                                        || !string.Equals(selectedItemKey, _lastSelectedItemKey, StringComparison.OrdinalIgnoreCase));

        CaptureScrollPositions();
        if (resetItemResultsToTop)
        {
            _savedScrollPositions["item-results"] = 1f;
            _scrollsToForceTop.Add("item-results");
        }
        if (resetItemDetailsToTop)
        {
            _savedScrollPositions["item-details"] = 1f;
            _scrollsToForceTop.Add("item-details");
        }
        _lastItemResultSetKey = itemResultSetKey;
        _lastSelectedItemKey = selectedItemKey;

        if (_contentRoot != null)
        {
            _contentRoot.SetActive(false);
        }

        _activeScrolls.Clear();
        if (!force
            && _workspaceRoots.TryGetValue(activeTab, out var cachedRoot)
            && cachedRoot != null
            && _workspaceRootFingerprints.TryGetValue(activeTab, out var cachedFingerprint)
            && string.Equals(cachedFingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            _contentRoot = cachedRoot;
            _contentRoot.SetActive(true);
            if (_workspaceScrolls.TryGetValue(activeTab, out var cachedScrolls))
            {
                foreach (var pair in cachedScrolls)
                {
                    if (pair.Value != null)
                    {
                        _activeScrolls[pair.Key] = pair.Value;
                    }
                }
            }

            StartCoroutine(RestoreScrollPositionsNextFrame());
            return;
        }

        if (_workspaceRoots.TryGetValue(activeTab, out var previousRoot) && previousRoot != null)
        {
            previousRoot.SetActive(false);
            Destroy(previousRoot);
        }

        _workspaceRoots.Remove(activeTab);
        _workspaceRootFingerprints.Remove(activeTab);
        _workspaceScrolls.Remove(activeTab);

        _contentRoot = new GameObject($"NativeWorkspaceContent_{activeTab}", typeof(RectTransform));
        _contentRoot.transform.SetParent(_root, false);
        var contentRect = (RectTransform)_contentRoot.transform;
        HermesNativeUiFramework.Stretch(contentRect, 6f, 6f, 6f, 6f);

        try
        {
            switch (_state.ActiveTab)
            {
                case "Assistant":
                    RenderAssistant(contentRect);
                    break;
                case "Hideout":
                    RenderHideout(contentRect);
                    break;
                case "Crafts":
                    RenderCrafts(contentRect);
                    break;
                case "Stash":
                    RenderStash(contentRect);
                    break;
                case "Loadout":
                    RenderLoadout(contentRect, false);
                    break;
                case "RaidPlanner":
                    RenderLoadout(contentRect, true);
                    break;
                default:
                    RenderItemSearch(contentRect);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native workspace body failed to build: {ex}");
            var fallback = CreateVerticalRoot(contentRect);
            AddSectionHeader(fallback, "NATIVE WORKSPACE ERROR");
            AddCard(fallback, "Rendering failed", ex.Message, "ERROR");
        }

        _workspaceRoots[activeTab] = _contentRoot;
        _workspaceRootFingerprints[activeTab] = _lastFingerprint;
        _workspaceScrolls[activeTab] = new Dictionary<string, ScrollRect>(_activeScrolls, StringComparer.Ordinal);

        StartCoroutine(RestoreScrollPositionsNextFrame());
    }

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
                AssistantSeverityCardColor(notice.Severity));

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
                            _assistantDraft = prompt;
                            Invalidate(0.05f);
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

    #region Items and market

    private void RenderItemSearch(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        var combinedStatus = _state!.DetailLoading
            ? _state.DetailStatus
            : _state.SearchLoading
                ? _state.SearchStatus
                : !string.IsNullOrWhiteSpace(_state.DetailStatus)
                    ? _state.DetailStatus
                    : _state.SearchStatus;
        AddStatusStrip(root, combinedStatus, _state.SearchLoading || _state.DetailLoading, _state.RefreshActive);

        var split = HermesNativeUiFramework.CreateSplitView(root, 350f);
        var splitElement = split.Root.gameObject.AddComponent<LayoutElement>();
        splitElement.minHeight = 180f;
        splitElement.flexibleHeight = 1f;
        splitElement.flexibleWidth = 1f;

        AddVerticalLayout(split.Left, 6, 6, 6, 6, 3f);
        var results = CreateScroll(split.Left, "item-results", true);
        AddSectionHeader(results.Content, $"SEARCH RESULTS  {_state.SearchResults.Count:N0}");
        if (_state.SearchResults.Count == 0)
        {
            AddEmptyState(results.Content, _state.SearchLoading ? "Searching item templates..." : "No item results.", "Use the native search field above this panel.");
        }
        else
        {
            foreach (var item in _state.SearchResults.Take(MaximumRowsPerSection))
            {
                var selected = string.Equals(_state.SelectedItem?.ItemKey, item.ItemKey, StringComparison.OrdinalIgnoreCase);
                var selectedInstance = selected ? _state.SelectedStashInstance : null;
                var displayedValue = selectedInstance is { ConditionAdjustedReferenceValue: > 0 }
                    ? selectedInstance.ConditionAdjustedReferenceValue
                    : item.ReferencePrice;
                var displayedLabel = selectedInstance switch
                {
                    { ChildItemCount: > 0 } => "Assembly",
                    not null => "Instance",
                    _ => "Reference"
                };
                AddCard(
                    results.Content,
                    item.Name,
                    $"{item.ShortName} • {displayedLabel} {Money(displayedValue)}",
                    string.Join(" • ", new[]
                    {
                        item.AppearsInTraderData ? "TRADER" : null,
                        item.AppearsInHideoutData ? "HIDEOUT" : null,
                        item.AppearsInQuestData ? "QUEST" : null
                    }.Where(value => value is not null)),
                    () =>
                    {
                        _state.SelectItem(item);
                        Invalidate(0.20f);
                    },
                    selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            }
        }

        AddVerticalLayout(split.Right, 6, 6, 6, 6, 3f);
        var details = CreateScroll(split.Right, "item-details", true);
        RenderSelectedItemDetails(details.Content);
    }

    private void RenderSelectedItemDetails(Transform parent)
    {
        var item = _state!.SelectedItem;
        if (item is null)
        {
            AddEmptyState(parent, "Select an item.", "Trader, flea, stash-instance, quest, hideout, and crafting intelligence will appear here.");
            return;
        }

        var selectedStashInstance = _state.SelectedStashInstance;
        AddSectionHeader(parent, item.Name);
        AddMetricGrid(parent,
            ("SHORT NAME", item.ShortName),
            (_state.DisplayedItemReferenceLabel, Money(_state.DisplayedItemReferenceValue)),
            ("CHILD VALUE", selectedStashInstance is { ChildItemCount: > 0 }
                ? Money(selectedStashInstance.InstalledComponentReferenceValue)
                : "—"),
            ("OWNED COPIES", _state.StashInstances.Count.ToString("N0")),
            ("PROFILE", _state.StashInstances.Count > 0 ? "OWNED" : "NOT OWNED"));

        var stashSummary = _state.StashInstances.Count == 0
            ? "No matching owned copy is in the active PMC inventory. Trader pricing uses the full-condition base-item estimate."
            : selectedStashInstance is not null
                ? $"Selected: {selectedStashInstance.Label} • {selectedStashInstance.Location} • {selectedStashInstance.ConditionPercent}% {selectedStashInstance.ConditionDescription}"
                : $"{_state.StashInstances.Count:N0} matching owned copy/copies available • base-item estimate selected";
        var stashMeta = _state.StashInstances.Count == 0
            ? "NOT OWNED"
            : $"{_state.StashInstances.Count:N0} COPY/COPIES";
        var stashExpanded = AddItemDetailCollapsibleSection(
            parent,
            "stash-pricing",
            "OWNED COPY PRICING",
            stashSummary,
            stashMeta,
            _state.StashInstances.Count > 0 && !Plugin.Settings.CollapseSectionsByDefault.Value);
        if (stashExpanded)
        {
            AddCard(parent, "BASE ITEM", "Full-condition base-item estimate.", _state.SelectedStashInstanceKey is null ? "SELECTED" : "PREVIEW", () =>
            {
                _state.SelectStashInstance(null);
                Invalidate(0.20f);
            }, _state.SelectedStashInstanceKey is null ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            foreach (var instance in _state.StashInstances.Take(30))
            {
                var selected = string.Equals(_state.SelectedStashInstanceKey, instance.InstanceKey, StringComparison.OrdinalIgnoreCase);
                AddCard(
                    parent,
                    instance.Label,
                    $"{instance.Location} • {FormatCount(instance.Quantity)} unit(s) • {instance.ConditionPercent}% {instance.ConditionDescription} • {instance.ChildItemCount} child item(s)",
                    $"REFERENCE {Money(instance.ConditionAdjustedReferenceValue)}{(instance.FoundInRaid ? " • FIR" : string.Empty)}",
                    () =>
                    {
                        _state.SelectStashInstance(instance.InstanceKey);
                        Invalidate(0.20f);
                    },
                    selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
            }
        }

        RenderTraderSummary(parent, _state.TraderSummary);
        RenderMarketSummary(parent, _state.MarketSummary);
        RenderItemUsage(parent, _state.ItemUsage);
    }

    private void RenderTraderSummary(Transform parent, HermesTraderSummaryResponse? summary)
    {
        if (summary is null)
        {
            AddSectionHeader(parent, "TRADERS");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading trader data..." : "Trader data unavailable.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddSectionHeader(parent, "TRADERS");
            AddEmptyState(parent, "No trader analysis.", summary.Message ?? string.Empty);
            return;
        }

        var bestSale = summary.BestSellOffer;
        var bestPurchase = summary.PurchaseOffers
            .Where(offer => offer.IsAvailable)
            .SelectMany(offer => offer.PaymentOptions
                .Where(payment => payment.EstimateAvailable && payment.EstimatedRoubleValue > 0)
                .Select(payment => new
                {
                    offer.TraderName,
                    payment.DisplayPrice,
                    payment.EstimatedRoubleValue
                }))
            .OrderBy(option => option.EstimatedRoubleValue)
            .FirstOrDefault();

        var saleSummary = bestSale is null
            ? "Sell: no supported trader buyer"
            : $"Sell: {bestSale.TraderName} for {Money(bestSale.RoubleEquivalent)}";
        var purchaseSummary = bestPurchase is null
            ? "Buy: no currently available trader offer"
            : $"Buy: {bestPurchase.TraderName} for {bestPurchase.DisplayPrice} (about {Money(bestPurchase.EstimatedRoubleValue)})";
        var traderMeta = (HasUsefulTraderInfo(summary)
                ? $"{summary.SellOffers.Count:N0} sale option(s) • {summary.PurchaseOffers.Count:N0} purchase offer(s)"
                : "NO CURRENT TRADER OFFER")
                         + (summary.UsesSelectedStashInstance ? " • SELECTED OWNED COPY" : " • BASE ITEM");

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "traders",
            "TRADERS",
            $"{saleSummary}\n{purchaseSummary}",
            traderMeta,
            Plugin.Settings.ExpandTraderComparisonByDefault.Value && HasUsefulTraderInfo(summary));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("BEST SALE", bestSale is null ? "NO BUYER" : $"{bestSale.TraderName} • {Money(bestSale.RoubleEquivalent)}"),
            ("BASE REFERENCE", Money(summary.ReferencePrice)),
            ("PURCHASE OFFERS", summary.PurchaseOffers.Count.ToString("N0")));

        AddCard(
            parent,
            "SALE ESTIMATE",
            summary.SalePriceBasis,
            summary.UsesSelectedStashInstance ? "SELECTED OWNED COPY" : "FULL-CONDITION BASE ITEM");

        AddSectionHeader(parent, "SELL TO TRADERS");
        if (summary.SellOffers.Count == 0)
        {
            AddEmptyState(parent, "No supported trader buyer.", summary.Message ?? string.Empty);
        }
        foreach (var offer in summary.SellOffers.Take(12))
        {
            AddCard(parent,
                offer.TraderName,
                $"LL{offer.PlayerLoyaltyLevel} • {FormatCurrency(offer.Amount, offer.Currency)} • ₽{offer.RoubleEquivalent:N0} equivalent",
                offer.IsBest ? "BEST TRADER SALE" : "TRADER SALE");
        }

        AddSectionHeader(parent, "BUY FROM TRADERS");
        if (summary.PurchaseOffers.Count == 0)
        {
            AddEmptyState(parent, "No trader purchase offer.", "No current vanilla-trader offer was found for this item.");
        }
        foreach (var offer in summary.PurchaseOffers.Take(20))
        {
            var payment = offer.PaymentOptions
                .Where(option => option.EstimateAvailable && option.EstimatedRoubleValue > 0)
                .OrderBy(option => option.EstimatedRoubleValue)
                .FirstOrDefault()
                ?? offer.PaymentOptions.OrderBy(option => option.EstimatedRoubleValue).FirstOrDefault();
            AddCard(parent,
                offer.TraderName,
                payment is null
                    ? offer.AvailabilityReason
                    : $"{payment.DisplayPrice} • estimated ₽{payment.EstimatedRoubleValue:N0} • {offer.AvailabilityReason}",
                offer.IsAvailable ? $"AVAILABLE • LL{offer.RequiredLoyaltyLevel}" : $"LOCKED • LL{offer.RequiredLoyaltyLevel}");
        }
    }

    private void RenderMarketSummary(Transform parent, HermesMarketSummaryResponse? market)
    {
        if (market is null)
        {
            AddSectionHeader(parent, "FLEA MARKET");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading local flea data..." : "Market data unavailable.", string.Empty);
            return;
        }
        if (!market.Found)
        {
            AddSectionHeader(parent, "FLEA MARKET");
            AddEmptyState(parent, "No market analysis.", market.Message ?? string.Empty);
            return;
        }

        var configuredMinimum = Plugin.Settings.GetMinimumComparableFleaOffers();
        var reliable = market.MarketPriceFromActiveOffers && market.ComparableOfferCount >= configuredMinimum;
        var reliability = reliable
            ? "RELIABLE"
            : market.MarketPriceFromActiveOffers
                ? "LOW SAMPLE"
                : "REFERENCE";
        var saleSummary = !market.FleaUnlocked
            ? $"Locked until player level {market.RequiredPlayerLevel}."
            : !market.CanSellOnFlea
                ? $"Cannot list: {market.SellUnavailableReason ?? "listing unavailable"}."
                : $"Net {(market.UsesSelectedOwnedCopy ? "selected-copy" : "base-item")} sale {Money(market.EstimatedNetSale)} after fee {Money(market.EstimatedListingFee)} - suggested list {Money(market.SuggestedListPrice)}";
        var buySummary = market.LowestPrice.HasValue
            ? $"Lowest comparable offer {Money(market.LowestPrice)} • median {Money(market.MedianPrice)}"
            : "No comparable Flea offer is currently available.";
        var marketMeta = HasUsefulMarketInfo(market)
            ? $"{market.ComparableOfferCount:N0} comparable offer(s) • {reliability} • {market.MarketPriceSource}"
            : "NO USABLE MARKET VALUE";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "flea",
            "FLEA MARKET",
            $"{saleSummary}\n{buySummary}",
            marketMeta,
            Plugin.Settings.ExpandMarketByDefault.Value && HasUsefulMarketInfo(market));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("LOWEST", Money(market.LowestPrice)),
            ("MEDIAN", Money(market.MedianPrice)),
            ("SUGGESTED", Money(market.SuggestedListPrice)),
            ("NET SALE", Money(market.EstimatedNetSale)),
            ("COMPARABLE", market.ComparableOfferCount.ToString("N0")),
            ("SOURCE", market.MarketPriceSource));
        if (market.UsesSelectedOwnedCopy)
        {
            AddCard(
                parent,
                "SELECTED COPY FLEA BASIS",
                $"{market.SelectedOwnedCopyLabel} - {market.SelectedOwnedCopyLocation}\nRoot {Money(market.SelectedOwnedCopyRootValue)} - child items {Money(market.SelectedOwnedCopyChildValue)}",
                "OWNED COPY");
        }
        AddCard(parent, "BUY RECOMMENDATION", market.BuyRecommendation, market.FleaUnlocked ? "FLEA UNLOCKED" : $"UNLOCKS AT LEVEL {market.RequiredPlayerLevel}");
        AddCard(parent, "SELL RECOMMENDATION", market.SellRecommendation, market.CanSellOnFlea ? "CAN LIST" : market.SellUnavailableReason ?? "CANNOT LIST");

        AddSectionHeader(parent, "LOWEST COMPARABLE OFFERS");
        if (market.LowestOffers.Count == 0)
        {
            AddEmptyState(parent, "No comparable Flea offers.", market.MarketPriceSource);
        }
        foreach (var offer in market.LowestOffers.Take(12))
        {
            AddCard(parent,
                offer.IsBarter ? "BARTER OFFER" : "CASH OFFER",
                $"Unit ₽{offer.UnitPrice:N0} • listed ₽{offer.ListedUnitPrice:N0} • qty {offer.Quantity:N0} • {offer.ConditionPercent}% {offer.ConditionLabel}",
                $"{offer.PriceSource} • {FormatDuration(offer.SecondsRemaining)} remaining");
        }
    }

    private void RenderItemUsage(Transform parent, HermesItemHideoutUsageResponse? usage)
    {
        if (usage is null)
        {
            AddSectionHeader(parent, "QUEST, KEY, HIDEOUT & CRAFT USES");
            AddEmptyState(parent, _state!.DetailLoading ? "Loading profile uses..." : "Profile usage unavailable.", string.Empty);
            return;
        }
        if (!usage.Found)
        {
            AddSectionHeader(parent, "QUEST, KEY, HIDEOUT & CRAFT USES");
            AddEmptyState(parent, "No profile usage analysis.", usage.Message ?? string.Empty);
            return;
        }

        RenderQuestRequirementSection(parent, usage);
        RenderQuestKeySection(parent, usage);
        RenderHideoutAndCraftUsageSection(parent, usage);
    }

    private void RenderQuestRequirementSection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = first is null
            ? "No standard quest item requirement uses this item."
            : first.ConditionCompleted || first.QuestCompleted
                ? $"Completed use: {first.QuestName}."
                : $"{(first.IsActive ? "ACTIVE" : "FUTURE")}: {first.QuestName} • {first.ProgressText}";
        var meta = HasUsefulQuestRequirements(usage)
            ? $"{active.Count:N0} active • {remaining:N0} remaining • {completed:N0} completed • owned {FormatCount(usage.OwnedQuantity)} ({FormatCount(usage.OwnedFoundInRaidQuantity)} FIR)"
            : $"NO CURRENT QUEST REQUIREMENT • {completed:N0} completed use(s)";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "quests",
            "QUEST REQUIREMENTS",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulQuestRequirements(usage));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("OWNED", FormatCount(usage.OwnedQuantity)),
            ("OWNED FIR", FormatCount(usage.OwnedFoundInRaidQuantity)),
            ("ACTIVE", active.Count.ToString("N0")),
            ("REMAINING", remaining.ToString("N0")),
            ("COMPLETED", completed.ToString("N0")));

        if (usage.QuestUses.Count == 0)
        {
            AddEmptyState(parent, "No quest requirements.", "No player-facing item requirement was found in standard quest completion conditions.");
        }
        foreach (var quest in usage.QuestUses.Take(30))
        {
            AddCard(parent,
                quest.QuestName,
                $"{quest.ProgressText}\nRequired {FormatCount(quest.Required)}{(quest.FoundInRaidRequired ? " FIR" : string.Empty)} • owned matching {FormatCount(quest.OwnedMatchingTargets)} • this item {FormatCount(quest.OwnedSelectedItem)}",
                $"{quest.TraderName} • {quest.QuestStatus}{(quest.ConditionCompleted ? " • OBJECTIVE COMPLETE" : string.Empty)}");
        }
    }

    private void RenderQuestKeySection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = first is null
            ? "This item is not linked to a known quest-key requirement."
            : $"{(first.IsActive && !first.QuestCompleted ? "ACTIVE" : first.QuestCompleted ? "COMPLETED" : "KNOWN")}: {first.QuestName} • {first.MapName} • {first.Opens}";
        var meta = HasUsefulQuestKeyKnowledge(usage)
            ? $"{active.Count:N0} active • {remaining:N0} remaining • {completed:N0} completed"
            : $"NO REMAINING QUEST-KEY USE • {completed:N0} completed";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "quest-keys",
            "QUEST KEY KNOWLEDGE",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulQuestKeyKnowledge(usage));
        if (!expanded)
        {
            return;
        }

        if (usage.QuestKeyUses.Count == 0)
        {
            AddEmptyState(parent, "No quest-key knowledge.", "The installed key and quest databases do not associate this item with a known quest lock.");
        }
        foreach (var keyUse in usage.QuestKeyUses.Take(30))
        {
            var status = keyUse.QuestCompleted
                ? "COMPLETED"
                : keyUse.IsActive
                    ? "ACTIVE QUEST"
                    : keyUse.QuestStatus.ToUpperInvariant();
            var details = new List<string>
            {
                $"{keyUse.MapName} • opens {keyUse.Opens}"
            };
            if (!string.IsNullOrWhiteSpace(keyUse.Purpose))
            {
                details.Add(keyUse.Purpose);
            }
            if (!string.IsNullOrWhiteSpace(keyUse.Acquisition))
            {
                details.Add($"Acquisition: {keyUse.Acquisition}");
            }

            AddCard(parent,
                keyUse.QuestName,
                string.Join("\n", details),
                $"{status}{(keyUse.AcquireInRaid ? " • ACQUIRE IN RAID" : string.Empty)}");
        }
    }

    private void RenderHideoutAndCraftUsageSection(Transform parent, HermesItemHideoutUsageResponse usage)
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
        var summary = nextUpgrade is not null
            ? $"Next upgrade: {nextUpgrade.AreaName} L{nextUpgrade.TargetLevel} • owned {FormatCount(nextUpgrade.Owned)} / {FormatCount(nextUpgrade.Required)} • missing {FormatCount(nextUpgrade.Missing)}"
            : readyCraft is not null
                ? $"Craft use: {readyCraft.StationName} L{readyCraft.RequiredStationLevel} • {readyCraft.Status}"
                : "No Hideout upgrade or player-facing recipe currently uses this item.";
        var meta = HasUsefulHideoutOrCraftInfo(usage)
            ? $"{usage.UpgradeUses.Count:N0} upgrade(s) • {usage.ProducedBy.Count:N0} produced-by recipe(s) • {usage.UsedBy.Count:N0} ingredient recipe(s)"
            : "NO CURRENT HIDEOUT OR CRAFT USE";

        var expanded = AddItemDetailCollapsibleSection(
            parent,
            "hideout-crafts",
            "HIDEOUT & CRAFT USES",
            summary,
            meta,
            !Plugin.Settings.CollapseSectionsByDefault.Value && HasUsefulHideoutOrCraftInfo(usage));
        if (!expanded)
        {
            return;
        }

        AddMetricGrid(parent,
            ("UPGRADES", usage.UpgradeUses.Count.ToString("N0")),
            ("PRODUCED BY", usage.ProducedBy.Count.ToString("N0")),
            ("USED BY", usage.UsedBy.Count.ToString("N0")));

        AddSectionHeader(parent, "HIDEOUT UPGRADES");
        if (usage.UpgradeUses.Count == 0)
        {
            AddEmptyState(parent, "No Hideout upgrade use.", "This item is not required by a player-facing Hideout upgrade.");
        }
        foreach (var upgrade in usage.UpgradeUses.Take(30))
        {
            AddCard(parent,
                $"{upgrade.AreaName} L{upgrade.TargetLevel}",
                $"Current L{upgrade.CurrentLevel} • owned {FormatCount(upgrade.Owned)} / {FormatCount(upgrade.Required)} • missing {FormatCount(upgrade.Missing)}"
                + (upgrade.EstimatedMissingCost.HasValue ? $" • estimated missing cost {Money(upgrade.EstimatedMissingCost)}" : string.Empty),
                $"{upgrade.Status}{(upgrade.FoundInRaidRequired ? " • FIR" : string.Empty)}");
        }

        AddSectionHeader(parent, "USED AS A CRAFT INGREDIENT");
        if (usage.UsedBy.Count == 0)
        {
            AddEmptyState(parent, "No ingredient use.", "This item is not used by a player-facing Hideout recipe.");
        }
        foreach (var craft in usage.UsedBy.Take(30))
        {
            AddCard(parent,
                craft.OutputName,
                $"{craft.StationName} L{craft.RequiredStationLevel} • requires {FormatCount(craft.ItemCount)} • owned {FormatCount(craft.Owned)} • missing {FormatCount(craft.Missing)}",
                craft.Status,
                () => _state!.Navigate("Crafts"));
        }

        AddSectionHeader(parent, "PRODUCED BY CRAFTS");
        if (usage.ProducedBy.Count == 0)
        {
            AddEmptyState(parent, "No production recipe.", "No player-facing Hideout recipe produces this item.");
        }
        foreach (var craft in usage.ProducedBy.Take(30))
        {
            AddCard(parent,
                craft.OutputName,
                $"{craft.StationName} L{craft.RequiredStationLevel} • output {craft.OutputQuantity:N0} • {FormatDuration(craft.DurationSeconds)}",
                craft.Status,
                () => _state!.Navigate("Crafts"));
        }
    }

    private static bool HasUsefulTraderInfo(HermesTraderSummaryResponse? summary)
    {
        return summary is { Found: true }
               && (summary.BestSellOffer is not null
                   || summary.SellOffers.Count > 0
                   || summary.PurchaseOffers.Any(offer => offer.IsAvailable));
    }

    private static bool HasUsefulMarketInfo(HermesMarketSummaryResponse? market)
    {
        return market is { Found: true }
               && (market.LowestPrice.HasValue
                   || market.MedianPrice.HasValue
                   || market.SuggestedListPrice.HasValue
                   || market.EstimatedNetSale.HasValue
                   || market.ComparableOfferCount > 0);
    }

    private static bool HasUsefulQuestRequirements(HermesItemHideoutUsageResponse usage)
    {
        return usage.QuestUses.Any(quest => !quest.ConditionCompleted && !quest.QuestCompleted);
    }

    private static bool HasUsefulQuestKeyKnowledge(HermesItemHideoutUsageResponse usage)
    {
        return usage.QuestKeyUses.Any(key => !key.QuestCompleted);
    }

    private static bool HasUsefulHideoutOrCraftInfo(HermesItemHideoutUsageResponse usage)
    {
        return usage.UpgradeUses.Any(upgrade => !upgrade.IsMet && upgrade.TargetLevel > upgrade.CurrentLevel)
               || usage.ProducedBy.Count > 0
               || usage.UsedBy.Count > 0;
    }

    private bool AddItemDetailCollapsibleSection(
        Transform parent,
        string sectionId,
        string title,
        string summary,
        string meta,
        bool defaultExpanded)
    {
        var itemKey = _state?.SelectedItem?.ItemKey ?? "none";
        var key = $"{itemKey}|{sectionId}";
        if (!_itemDetailSectionExpansion.TryGetValue(key, out var expanded))
        {
            if (_itemDetailSectionExpansion.Count >= 256)
            {
                // Section state is convenience-only. Bound it so long item-search sessions do
                // not retain expansion flags for every item examined during the process lifetime.
                _itemDetailSectionExpansion.Clear();
            }

            expanded = defaultExpanded;
            _itemDetailSectionExpansion[key] = expanded;
        }

        var arrow = expanded ? "▼" : "▶";
        var actionText = expanded ? "SELECT TO COLLAPSE" : "SELECT TO EXPAND";
        AddCard(
            parent,
            $"{arrow}  {title}",
            summary,
            string.IsNullOrWhiteSpace(meta) ? actionText : $"{meta} • {actionText}",
            () =>
            {
                _itemDetailSectionExpansion[key] = !expanded;
                Invalidate(0.05f);
            },
            expanded ? new Color(0.10f, 0.12f, 0.11f, 0.86f) : null);
        return expanded;
    }

    #endregion

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
                string.IsNullOrWhiteSpace(requirement.ItemTemplateId)
                    ? null
                    : () => _state.Window.OpenForPreviewItem(requirement.ItemTemplateId!, "Hideout requirement"));
        }
    }

    #endregion

    #region Crafts

    private void ApplyPendingCraftFocus()
    {
        var focus = HermesNativeCraftFocus.Read();
        if (focus.Revision == _craftFocusRevision)
        {
            return;
        }

        _craftFocusRevision = focus.Revision;
        _craftSearch = focus.DisplayName;
        _craftFilter = "ALL";
        _craftAvailableOnly = false;
        _craftFocusKeys.Clear();
        foreach (var key in focus.CraftKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _craftFocusKeys.Add(key);
            }
        }

        _savedScrollPositions["craft-list"] = 1f;
        _scrollsToForceTop.Add("craft-list");
        _forceRebuild = true;
    }

    private void RenderCrafts(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.CraftStatus, _state.CraftLoading, _state.RefreshActive);
        var response = _state.CraftSummary;
        if (response is null)
        {
            AddEmptyState(root, _state.CraftLoading ? "Indexing profile-scoped recipes..." : "No craft snapshot loaded.", "Only recipes belonging to installed stations on the active PMC profile are shown.");
            return;
        }
        if (!response.Found)
        {
            AddEmptyState(root, "Craft data unavailable.", response.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("TOTAL", response.Crafts.Count.ToString("N0")),
            ("AVAILABLE", response.Crafts.Count(craft => craft.StationLevelMet).ToString("N0")),
            ("READY NOW", response.Crafts.Count(craft => craft.CanStartNow).ToString("N0")),
            ("PROFITABLE", response.Crafts.Count(IsCraftProfitable).ToString("N0")),
            ("ACTIVE", response.Crafts.Count(craft => craft.IsActive).ToString("N0")),
            ("COMPLETE", response.Crafts.Count(craft => craft.IsComplete).ToString("N0")));

        var toolbar = CreateToolbar(root);
        AddToolbarLabel(toolbar, "RECIPES");
        AddFlexibleSpace(toolbar);
        var search = AddInput(toolbar, "FILTER RECIPES", _craftSearch, 240f);
        search.onEndEdit.AddListener(value =>
        {
            _craftSearch = value.Trim();
            _craftFocusKeys.Clear();
            Invalidate();
        });
        AddButton(toolbar, "READY", () => SetCraftFilter("READY"), 82f, selected: _craftFilter == "READY");
        AddButton(toolbar, "PROFITABLE", () => SetCraftFilter("PROFITABLE"), 104f, selected: _craftFilter == "PROFITABLE");
        AddButton(toolbar, "ACTIVE", () => SetCraftFilter("ACTIVE"), 82f, selected: _craftFilter == "ACTIVE");
        AddButton(toolbar, "ALL", () => SetCraftFilter("ALL"), 68f, selected: _craftFilter == "ALL");
        AddCheckbox(toolbar, "AVAILABLE", _craftAvailableOnly, value =>
        {
            _craftAvailableOnly = value;
            Invalidate();
        }, 118f);

        var split = HermesNativeUiFramework.CreateSplitView(root, 360f);
        var splitLayout = split.Root.gameObject.AddComponent<LayoutElement>();
        splitLayout.flexibleHeight = 1f;
        splitLayout.flexibleWidth = 1f;
        AddVerticalLayout(split.Left, 5, 5, 5, 5, 2f);
        AddVerticalLayout(split.Right, 5, 5, 5, 5, 2f);
        var list = CreateScroll(split.Left, "craft-list", true);
        var details = CreateScroll(split.Right, "craft-details", true);

        var crafts = FilterCrafts(response.Crafts).Take(MaximumRowsPerSection).ToList();
        AddSectionHeader(list.Content, $"RECIPES  {crafts.Count:N0}");
        foreach (var craft in crafts)
        {
            var selected = string.Equals(_state.SelectedCraft?.CraftKey, craft.CraftKey, StringComparison.OrdinalIgnoreCase);
            AddCard(
                list.Content,
                $"{craft.OutputQuantity:N0}× {craft.OutputName}",
                $"{craft.StationName} • YOUR L{craft.CurrentStationLevel} / REQ L{craft.RequiredStationLevel} • {FormatDuration(craft.DurationSeconds)} • input {Money(craft.EstimatedEconomicInputValue)} • best sale {Money(craft.EstimatedBestSaleValue)}",
                $"{craft.Status} • BEST PROFIT {Money(BestCraftProfit(craft))} • {CraftProfitSource(craft)}",
                () =>
                {
                    _state.SelectCraft(craft);
                    Invalidate(0.20f);
                },
                selected ? new Color(0.20f, 0.22f, 0.20f, 0.78f) : null);
        }
        if (crafts.Count == 0)
        {
            AddEmptyState(list.Content, "No recipes match the filter.", "Uninstalled-station recipes are removed server-side before reaching this list.");
        }

        RenderCraftDetails(details.Content);
    }

    private IEnumerable<HermesCraftSummary> FilterCrafts(IEnumerable<HermesCraftSummary> crafts)
    {
        var query = _craftSearch.Trim();
        var filtered = crafts
            .Where(craft => _craftFocusKeys.Count == 0 || _craftFocusKeys.Contains(craft.CraftKey))
            .Where(craft => _craftFocusKeys.Count > 0
                            || query.Length == 0
                            || craft.OutputName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.StationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craft.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
            // The four buttons are the primary status filter. Available is only an
            // additional narrowing condition and never replaces or resets that filter.
            .Where(craft => _craftFilter switch
            {
                "READY" => craft.CanStartNow,
                "PROFITABLE" => IsCraftProfitable(craft),
                "ACTIVE" => craft.IsActive,
                _ => true
            })
            .Where(MatchesCraftAvailabilityFilter);

        if (_craftFilter == "PROFITABLE")
        {
            return filtered
                .OrderByDescending(BestCraftProfit)
                .ThenByDescending(BestCraftProfitPerHour)
                .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
        }

        return filtered
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.IsActive || craft.IsComplete)
            .ThenByDescending(BestCraftProfitPerHour)
            .ThenBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesCraftAvailabilityFilter(HermesCraftSummary craft)
    {
        if (!_craftAvailableOnly)
        {
            return true;
        }

        // AVAILABLE follows the current profile's station level. Ingredients, station
        // occupancy, and start readiness belong to READY/status and must not hide a recipe
        // that the player's current station level can access.
        return craft.StationLevelMet || craft.IsActive || craft.IsComplete;
    }

    private static bool IsCraftProfitable(HermesCraftSummary craft)
    {
        var minimumProfit = Plugin.Settings.GetMinimumCraftProfit();
        var minimumPercent = Plugin.Settings.GetMinimumCraftProfitPercent();
        var percent = craft.EstimatedEconomicInputValue <= 0L
            ? (craft.EstimatedBestSaleProfit > 0L ? 100d : 0d)
            : craft.EstimatedBestSaleProfit * 100d / craft.EstimatedEconomicInputValue;
        return craft.EstimatedBestSaleProfit >= minimumProfit && percent >= minimumPercent;
    }

    private static long BestCraftProfit(HermesCraftSummary craft)
        => craft.EstimatedBestSaleProfit;

    private static long BestCraftProfitPerHour(HermesCraftSummary craft)
        => craft.EstimatedBestSaleProfitPerHour;

    private static string CraftProfitSource(HermesCraftSummary craft)
    {
        if (string.Equals(craft.BestSaleSource, "Flea Market", StringComparison.OrdinalIgnoreCase))
        {
            return "SELL ON FLEA";
        }

        return string.IsNullOrWhiteSpace(craft.BestSaleSource)
               || string.Equals(craft.BestSaleSource, "No available buyer", StringComparison.OrdinalIgnoreCase)
            ? "NO AVAILABLE BUYER"
            : $"SELL TO {craft.BestSaleSource.ToUpperInvariant()}";
    }

    private static string CraftFleaSaleText(HermesCraftSummary craft)
    {
        if (!craft.FleaUnlocked)
        {
            return "LOCKED";
        }

        return craft.CanSellOnFlea && craft.EstimatedFleaNetSaleValue > 0
            ? Money(craft.EstimatedFleaNetSaleValue)
            : "UNAVAILABLE";
    }

    private static string CraftFleaProfitText(HermesCraftSummary craft)
    {
        if (!craft.FleaUnlocked)
        {
            return "LOCKED";
        }

        return craft.CanSellOnFlea && craft.EstimatedFleaNetSaleValue > 0
            ? Money(craft.EstimatedFleaProfit)
            : "UNAVAILABLE";
    }

    private void SetCraftFilter(string filter)
    {
        if (string.Equals(_craftFilter, filter, StringComparison.Ordinal))
        {
            return;
        }

        _craftFilter = filter;
        Invalidate();
    }

    private void RenderCraftDetails(Transform parent)
    {
        var craft = _state!.SelectedCraft;
        var detail = _state.CraftDetail;
        if (craft is null)
        {
            AddEmptyState(parent, "Select a recipe.", "Ingredient readiness, purchase plan, station level, quest lock, and value estimates will appear here.");
            return;
        }

        AddSectionHeader(parent, craft.OutputName);
        AddMetricGrid(parent,
            ("STATION", $"{craft.StationName} • your L{craft.CurrentStationLevel} / required L{craft.RequiredStationLevel}"),
            ("OUTPUT", craft.OutputQuantity.ToString("N0")),
            ("DURATION", FormatDuration(craft.DurationSeconds)),
            ("STATUS", craft.Status),
            ("ECONOMIC INPUT", Money(craft.EstimatedEconomicInputValue)),
            ("TRADER SALE", Money(craft.EstimatedTraderSaleValue)),
            ("TRADER PROFIT", Money(craft.EstimatedTraderProfit)),
            ("FLEA NET", CraftFleaSaleText(craft)),
            ("FLEA PROFIT", CraftFleaProfitText(craft)),
            ("BEST SALE", Money(craft.EstimatedBestSaleValue)),
            ("BEST PROFIT", Money(craft.EstimatedBestSaleProfit)),
            ("BEST SELL PATH", CraftProfitSource(craft)),
            ("BEST PROFIT / HOUR", Money(BestCraftProfitPerHour(craft))),
            ("PLAN", craft.AcquisitionPlanComplete ? "COMPLETE" : "INCOMPLETE"));

        if (!string.IsNullOrWhiteSpace(craft.OutputTemplateId))
        {
            var toolbar = CreateToolbar(parent);
            AddButton(toolbar, "ASK HERMES ABOUT OUTPUT", () => _state.Window.OpenForPreviewItem(craft.OutputTemplateId!, "Craft output"), 210f);
            AddFlexibleSpace(toolbar);
        }

        if (_state.CraftLoading && detail is null)
        {
            AddEmptyState(parent, "Loading recipe details...", string.Empty);
            return;
        }
        if (detail is null || !detail.Found)
        {
            AddEmptyState(parent, "Recipe detail unavailable.", detail?.Message ?? string.Empty);
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail.RequiredQuestName))
        {
            AddCard(parent,
                "QUEST REQUIREMENT",
                detail.RequiredQuestName!,
                detail.RequiredQuestComplete ? "COMPLETE" : "LOCKED");
        }

        AddSectionHeader(parent, "INGREDIENTS");
        foreach (var ingredient in detail.Ingredients)
        {
            var body = $"Owned {FormatCount(ingredient.Owned)} / {FormatCount(ingredient.Required)} • used {FormatCount(ingredient.OwnedUsed)} • missing {FormatCount(ingredient.Missing)}"
                       + (ingredient.FoundInRaidRequired ? " • FIR" : string.Empty)
                       + (ingredient.IsReusableTool ? " • reusable tool" : string.Empty);
            var card = AddCard(
                parent,
                ingredient.Name,
                body,
                ingredient.IsMet ? "READY" : $"PURCHASE {Money(ingredient.EstimatedPurchaseCost)}",
                string.IsNullOrWhiteSpace(ingredient.TemplateId)
                    ? null
                    : () => _state.Window.OpenForPreviewItem(ingredient.TemplateId, "Craft ingredient"));
            foreach (var acquisition in ingredient.AcquisitionPlan.Take(8))
            {
                AddText(card,
                    $"• {acquisition.Source}: {FormatCount(acquisition.Quantity)} × ₽{acquisition.UnitPrice:N0} = ₽{acquisition.TotalCost:N0}{(acquisition.IsFallback ? " (estimate)" : string.Empty)}",
                    12f,
                    false,
                    HermesNativeUiFramework.MutedTextColor);
            }
            if (!string.IsNullOrWhiteSpace(ingredient.CostNote))
            {
                AddText(card, ingredient.CostNote!, 12f, false, HermesNativeUiFramework.MutedTextColor);
            }
        }

        AddSectionHeader(parent, "VALUATION");
        AddCard(parent, "VALUATION BASIS", detail.ValuationBasis, "READ ONLY");
    }

    #endregion

    #region Stash

    private void RenderStash(RectTransform parent)
    {
        var root = CreateVerticalRoot(parent);
        AddStatusStrip(root, _state!.StashStatus, _state.StashLoading, _state.RefreshActive);
        var summary = _state.StashSummary;
        if (summary is null)
        {
            AddEmptyState(root, _state.StashLoading ? "Building the active PMC stash snapshot..." : "No stash snapshot loaded.", string.Empty);
            return;
        }
        if (!summary.Found)
        {
            AddEmptyState(root, "Stash data unavailable.", summary.Message ?? string.Empty);
            return;
        }

        AddMetricGrid(root,
            ("INDEPENDENT ITEMS", summary.IndependentItemCount.ToString("N0")),
            ("OCCUPIED CELLS", summary.OccupiedCells.ToString("N0")),
            ("BEST LIQUIDATION", Money(summary.BestDestinationLiquidationValue)),
            ("SAFE / SURPLUS", (summary.SafeToSellInstanceCount + summary.SellSurplusInstanceCount).ToString("N0")),
            ("RECOVERABLE CELLS", summary.RecoverableCells.ToString("N0")),
            ("DUPLICATES", summary.DuplicateGroupCount.ToString("N0")),
            ("CONDITION WARNINGS", summary.DamagedOrDepletedItemCount.ToString("N0")),
            ("POTENTIAL SALE", Money(summary.PotentialBestSaleValue)));

        var tabs = CreateToolbar(root);
        foreach (var view in new[] { "OVERVIEW", "SELL", "CLEANUP", "KEEP", "REVIEW", "DUPLICATES", "DAMAGED" })
        {
            var captured = view;
            AddButton(tabs, view, () =>
            {
                _stashView = captured;
                Invalidate();
            }, view.Length * 8f + 34f, true, string.Equals(_stashView, view, StringComparison.Ordinal));
        }
        AddFlexibleSpace(tabs);
        if (_stashView is not "OVERVIEW")
        {
            var search = AddInput(tabs, "FILTER ITEMS", _stashSearch, 210f);
            search.onEndEdit.AddListener(value =>
            {
                _stashSearch = value.Trim();
                Invalidate();
            });
        }

        var scroll = CreateScroll(root, "stash-content", true);
        RenderStashView(scroll.Content, summary);
    }

    private void RenderStashView(Transform parent, HermesStashSummaryResponse summary)
    {
        switch (_stashView)
        {
            case "SELL":
                AddSectionHeader(parent, "SAFE TO SELL & SURPLUS");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.PotentiallySellQuantity > 0d));
                break;
            case "CLEANUP":
                AddSectionHeader(parent, $"CLEANUP • {summary.RecoverableCells:N0} RECOVERABLE CELLS • {Money(summary.CleanupBestSaleValue)}");
                RenderStashItems(parent, summary.CleanupCandidates);
                break;
            case "KEEP":
                AddSectionHeader(parent, "KEEP");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.Recommendation.Contains("keep", StringComparison.OrdinalIgnoreCase)));
                break;
            case "REVIEW":
                AddSectionHeader(parent, "REVIEW");
                RenderStashItems(parent, summary.Recommendations.Where(item => item.Recommendation.Contains("review", StringComparison.OrdinalIgnoreCase)));
                break;
            case "DUPLICATES":
                AddSectionHeader(parent, "DUPLICATE GROUPS");
                foreach (var group in summary.DuplicateGroups.Where(MatchesStashSearch).Take(MaximumRowsPerSection))
                {
                    AddCard(parent,
                        group.Name,
                        $"{group.InstanceCount:N0} instance(s) • owned {FormatCount(group.OwnedQuantity)} • reserve {FormatCount(group.SuggestedReserveQuantity)} • excess {FormatCount(group.PotentialExcessQuantity)} • {group.OccupiedCells:N0} cells",
                        $"{group.BestSaleDestination} • {Money(group.PotentialExcessSaleValue)}",
                        () => _state!.Window.OpenForPreviewItem(group.ItemKey, "Stash duplicate"));
                }
                break;
            case "DAMAGED":
                AddSectionHeader(parent, "DAMAGED OR DEPLETED");
                foreach (var item in summary.DamagedOrDepletedItems.Where(MatchesStashSearch).Take(MaximumRowsPerSection))
                {
                    AddCard(parent,
                        item.Name,
                        $"{item.InstanceLabel} • {item.ConditionPercent}% • {item.ConditionCurrent:0.##}/{item.ConditionMaximum:0.##} • threshold {item.ThresholdPercent}%",
                        $"{item.Status} • {item.BestSaleDestination} {Money(item.BestSaleValue)}",
                        () => _state!.Window.OpenForStashItem(item.InstanceKey, item.Name));
                }
                break;
            default:
                AddSectionHeader(parent, "VALUATION");
                AddMetricGrid(parent,
                    ("HANDBOOK", Money(summary.FullHandbookReferenceValue)),
                    ("CONDITION ADJUSTED", Money(summary.ConditionAdjustedHandbookValue)),
                    ("TRADER LIQUIDATION", Money(summary.BestTraderLiquidationValue)),
                    ("FLEA NET", Money(summary.EstimatedFleaNetValue)));
                AddSectionHeader(parent, "SALE DESTINATIONS");
                foreach (var destination in summary.SaleDestinationBreakdown)
                {
                    AddCard(parent, destination.Destination, $"{destination.ItemCount:N0} item(s)", Money(destination.RoubleEquivalent));
                }
                AddSectionHeader(parent, "MOST VALUABLE ITEMS");
                RenderStashItems(parent, summary.MostValuableItems.Take(40));
                break;
        }
    }

    private void RenderStashItems(Transform parent, IEnumerable<HermesStashValuationItem> source)
    {
        var rows = source.Where(MatchesStashSearch).Take(MaximumRowsPerSection).ToList();
        if (rows.Count == 0)
        {
            AddEmptyState(parent, "No items match this view.", string.Empty);
            return;
        }

        foreach (var item in rows)
        {
            AddCard(parent,
                item.Name,
                $"{item.InstanceLabel} • qty {FormatCount(item.Quantity)} • {item.ConditionPercent}% • {item.OccupiedCells:N0} cell(s) • reserve {FormatCount(item.RecommendedKeepQuantity)} • sell {FormatCount(item.PotentiallySellQuantity)}",
                $"{item.Recommendation} • {item.BestSaleDestination} {Money(item.BestSaleValue)}",
                () => _state!.Window.OpenForStashItem(item.InstanceKey, item.Name));
        }
    }

    private bool MatchesStashSearch(HermesStashValuationItem item)
        => _stashSearch.Length == 0
           || item.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Category.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Recommendation.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || (item.BestSaleDestination?.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool MatchesStashSearch(HermesStashDuplicateGroup group)
        => _stashSearch.Length == 0
           || group.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || group.Note.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || (group.BestSaleDestination?.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool MatchesStashSearch(HermesStashConditionItem item)
        => _stashSearch.Length == 0
           || item.Name.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Status.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase)
           || item.Recommendation.Contains(_stashSearch, StringComparison.OrdinalIgnoreCase);

    #endregion

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

    #region Native element helpers

    private RectTransform CreateVerticalRoot(RectTransform parent)
    {
        var root = new GameObject("VerticalWorkspace", typeof(RectTransform), typeof(VerticalLayoutGroup));
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        HermesNativeUiFramework.Stretch(rect, 0f, 0f, 0f, 0f);
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return rect;
    }

    private static RectTransform CreatePanel(Transform parent, string name, Color color)
    {
        var panel = HermesNativeUiFramework.CreatePanel(name, parent, color);
        var image = panel.GetComponent<Image>();
        image.raycastTarget = true;
        return panel;
    }

    private static VerticalLayoutGroup AddVerticalLayout(RectTransform panel, int left, int right, int top, int bottom, float spacing)
    {
        var layout = panel.gameObject.GetComponent<VerticalLayoutGroup>() ?? panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(left, right, top, bottom);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    private void AddStatusStrip(
        Transform parent,
        string status,
        bool loading,
        Action refresh,
        string refreshLabel = "REFRESH")
    {
        var strip = CreatePanel(parent, "StatusStrip", new Color(0f, 0f, 0f, 0.22f));
        var layout = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(9, 6, 3, 3);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var stripElement = strip.gameObject.AddComponent<LayoutElement>();
        stripElement.minHeight = CompactToolbarHeight;
        stripElement.preferredHeight = CompactToolbarHeight;
        stripElement.flexibleHeight = 0f;
        var label = AddText(strip, string.IsNullOrWhiteSpace(status) ? "CURRENT PROFILE DATA" : status, 12.5f, false, HermesNativeUiFramework.MutedTextColor);
        label.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        if (loading)
        {
            AddText(strip, "WORKING", 11.5f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Center, 76f);
        }
        AddButton(strip, refreshLabel, () => refresh(), 76f, !loading);
        AddAnchoredBottomRule(strip);
    }

    private static RectTransform CreateToolbar(Transform parent)
    {
        var toolbar = new GameObject("Toolbar", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        toolbar.transform.SetParent(parent, false);
        var rect = (RectTransform)toolbar.transform;
        var layout = toolbar.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(2, 2, 2, 2);
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var element = toolbar.GetComponent<LayoutElement>();
        element.minHeight = CompactToolbarHeight;
        element.preferredHeight = CompactToolbarHeight;
        element.flexibleHeight = 0f;
        return rect;
    }

    private static void AddToolbarLabel(Transform parent, string text)
    {
        AddText(parent, text, 13f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Left, 145f);
    }

    private static void AddFlexibleSpace(Transform parent)
    {
        var spacer = new GameObject("FlexibleSpace", typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(parent, false);
        var spacerElement = spacer.GetComponent<LayoutElement>();
        spacerElement.flexibleWidth = 1f;
        spacerElement.flexibleHeight = 0f;
    }

    private static TMP_InputField AddInput(Transform parent, string placeholderText, string value, float preferredWidth)
    {
        var root = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.SearchBorderSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        image.raycastTarget = true;
        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = 120f;
        layout.preferredWidth = preferredWidth;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;

        var viewport = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(root.transform, false);
        var viewportRect = (RectTransform)viewport.transform;
        HermesNativeUiFramework.Stretch(viewportRect, 12f, 5f, 12f, 5f);

        var text = HermesNativeUiFramework.CreateText("Text", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.color = HermesNativeUiFramework.NormalTextColor;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Masking;

        var placeholder = HermesNativeUiFramework.CreateText("Placeholder", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.text = placeholderText;
        placeholder.color = HermesNativeUiFramework.MutedTextColor;
        placeholder.enableWordWrapping = false;

        var input = root.GetComponent<TMP_InputField>();
        input.targetGraphic = image;
        input.textViewport = viewportRect;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 160;
        input.SetTextWithoutNotify(value ?? string.Empty);
        return input;
    }

    private static float AssistantActionButtonWidth(string label)
    {
        var normalized = string.IsNullOrWhiteSpace(label) ? "OPEN" : label.Trim();
        return Mathf.Clamp(68f + normalized.Length * 5.8f, 96f, 210f);
    }

    private static string BuildHideoutRequirementPreview(HermesHideoutAreaSummary area)
    {
        if (!area.TargetLevel.HasValue)
        {
            return "No further upgrade requirements.";
        }

        if (area.RequiredItems.Count == 0)
        {
            return area.Status.Contains("progression", StringComparison.OrdinalIgnoreCase)
                ? "No item materials • blocked by a non-item requirement."
                : "No item materials required for the next level.";
        }

        var items = area.RequiredItems
            .OrderBy(requirement => requirement.IsMet)
            .ThenByDescending(requirement => requirement.Missing)
            .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .Select(requirement =>
            {
                var owned = FormatCount(requirement.Owned);
                var required = FormatCount(requirement.Required);
                var fir = requirement.FoundInRaidRequired ? " FIR" : string.Empty;
                return requirement.IsMet
                    ? $"• {requirement.Name}: {owned}/{required}{fir} ✓"
                    : $"• {requirement.Name}: {owned}/{required}{fir} — missing {FormatCount(requirement.Missing)}";
            });

        return "Required items for next upgrade:\n" + string.Join("\n", items);
    }

    private static Toggle AddCheckbox(
        Transform parent,
        string text,
        bool value,
        UnityAction<bool> action,
        float width)
    {
        var root = new GameObject(
            "Checkbox",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Toggle),
            typeof(LayoutElement),
            typeof(HorizontalLayoutGroup));
        root.transform.SetParent(parent, false);

        var hitArea = root.GetComponent<Image>();
        hitArea.color = new Color(0f, 0f, 0f, 0f);
        hitArea.raycastTarget = true;

        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;

        var row = root.GetComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(6, 6, 4, 4);
        row.spacing = 7f;
        row.childAlignment = TextAnchor.MiddleLeft;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = false;

        var style = ResolveNativeFleaCheckboxStyle();
        var box = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        box.transform.SetParent(root.transform, false);
        var boxImage = box.GetComponent<Image>();
        boxImage.sprite = style?.BackgroundSprite;
        boxImage.type = style?.BackgroundType ?? Image.Type.Simple;
        boxImage.preserveAspect = style?.BackgroundPreserveAspect ?? false;
        boxImage.color = style?.BackgroundColor ?? new Color(0.16f, 0.17f, 0.16f, 0.95f);
        boxImage.raycastTarget = false;
        var boxLayout = box.GetComponent<LayoutElement>();
        boxLayout.minWidth = 18f;
        boxLayout.preferredWidth = 18f;
        boxLayout.minHeight = 18f;
        boxLayout.preferredHeight = 18f;
        boxLayout.flexibleWidth = 0f;
        boxLayout.flexibleHeight = 0f;

        Graphic? checkGraphic = null;
        GameObject? fallbackCheckmark = null;
        if (style?.CheckmarkSprite != null)
        {
            var check = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            check.transform.SetParent(box.transform, false);
            var checkImage = check.GetComponent<Image>();
            checkImage.sprite = style.CheckmarkSprite;
            checkImage.type = style.CheckmarkType;
            checkImage.preserveAspect = style.CheckmarkPreserveAspect;
            checkImage.color = style.CheckmarkColor;
            checkImage.raycastTarget = false;
            HermesNativeUiFramework.Stretch((RectTransform)check.transform, 0f, 0f, 0f, 0f);
            checkGraphic = checkImage;
        }
        else
        {
            fallbackCheckmark = CreateFallbackCheckmark(box.transform);
            fallbackCheckmark.SetActive(value);
        }

        var label = HermesNativeUiFramework.CreateText("Label", root.transform, 12.5f, true, TextAlignmentOptions.Left);
        label.text = text;
        label.color = HermesNativeUiFramework.NormalTextColor;
        label.raycastTarget = false;
        var labelLayout = label.gameObject.AddComponent<LayoutElement>();
        labelLayout.flexibleWidth = 1f;
        labelLayout.minHeight = 18f;

        var toggle = root.GetComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkGraphic;
        toggle.transition = Selectable.Transition.ColorTint;
        if (style != null)
        {
            toggle.colors = style.ToggleColors;
        }

        toggle.SetIsOnWithoutNotify(value);
        if (checkGraphic != null)
        {
            checkGraphic.canvasRenderer.SetAlpha(value ? 1f : 0f);
        }

        if (fallbackCheckmark != null)
        {
            toggle.onValueChanged.AddListener(isOn => fallbackCheckmark.SetActive(isOn));
        }

        toggle.onValueChanged.AddListener(action);
        return toggle;
    }

    private static NativeFleaCheckboxStyle? ResolveNativeFleaCheckboxStyle()
    {
        if (_nativeFleaCheckboxStyle != null)
        {
            return _nativeFleaCheckboxStyle;
        }

        Toggle? best = null;
        Image? bestBackground = null;
        Image? bestCheckmark = null;
        var bestScore = int.MinValue;

        foreach (var candidate in Resources.FindObjectsOfTypeAll<Toggle>())
        {
            if (candidate == null || candidate.gameObject == null)
            {
                continue;
            }

            var path = BuildHierarchyPath(candidate.transform);
            if (path.IndexOf("HERMES", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            if (candidate.targetGraphic is not Image background
                || candidate.graphic is not Image checkmark
                || ReferenceEquals(background, checkmark)
                || background.sprite == null
                || checkmark.sprite == null)
            {
                continue;
            }

            var lowerPath = path.ToLowerInvariant();
            var lowerName = candidate.name.ToLowerInvariant();
            var backgroundName = background.sprite.name?.ToLowerInvariant() ?? string.Empty;
            var checkmarkName = checkmark.sprite.name?.ToLowerInvariant() ?? string.Empty;
            var graphicName = checkmark.name.ToLowerInvariant();

            var score = 0;
            if (lowerPath.Contains("ragfair")) score += 320;
            if (lowerPath.Contains("filter")) score += 260;
            if (lowerPath.Contains("popup") || lowerPath.Contains("window")) score += 80;
            if (lowerName.Contains("checkbox")) score += 220;
            if (lowerName.Contains("check")) score += 100;
            if (lowerName.Contains("filter")) score += 70;
            if (backgroundName.Contains("checkbox")) score += 180;
            if (backgroundName.Contains("filter")) score += 50;
            if (checkmarkName.Contains("check")) score += 220;
            if (graphicName.Contains("check")) score += 180;

            var rect = candidate.transform as RectTransform;
            if (rect != null && rect.rect.width <= 64f && rect.rect.height <= 64f)
            {
                score += 25;
            }

            if (score <= bestScore)
            {
                continue;
            }

            best = candidate;
            bestBackground = background;
            bestCheckmark = checkmark;
            bestScore = score;
        }

        if (best == null || bestBackground == null || bestCheckmark == null || bestScore < 350)
        {
            if (!_nativeFleaCheckboxProbeLogged)
            {
                _nativeFleaCheckboxProbeLogged = true;
                Plugin.Log?.LogWarning(
                    "HERMES could not resolve the native Ragfair filter checkbox sprites yet; using the visible fallback checkmark.");
            }

            return null;
        }

        _nativeFleaCheckboxStyle = new NativeFleaCheckboxStyle
        {
            BackgroundSprite = bestBackground.sprite,
            BackgroundColor = bestBackground.color,
            BackgroundType = bestBackground.type,
            BackgroundPreserveAspect = bestBackground.preserveAspect,
            CheckmarkSprite = bestCheckmark.sprite,
            CheckmarkColor = bestCheckmark.color,
            CheckmarkType = bestCheckmark.type,
            CheckmarkPreserveAspect = bestCheckmark.preserveAspect,
            ToggleColors = best.colors
        };

        Plugin.Log?.LogInfo(
            $"HERMES captured native Ragfair filter checkbox '{BuildHierarchyPath(best.transform)}' "
            + $"with checkmark sprite '{bestCheckmark.sprite.name}'.");
        return _nativeFleaCheckboxStyle;
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        var names = new Stack<string>();
        var current = transform;
        var depth = 0;
        while (current != null && depth++ < 16)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static GameObject CreateFallbackCheckmark(Transform parent)
    {
        var root = new GameObject("FallbackCheckmark", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        HermesNativeUiFramework.Stretch((RectTransform)root.transform, 1f, 1f, 1f, 1f);

        AddFallbackCheckLine(root.transform, "ShortStroke", new Vector2(-3.2f, -1.4f), new Vector2(2.5f, 7f), -42f);
        AddFallbackCheckLine(root.transform, "LongStroke", new Vector2(2.1f, 1.3f), new Vector2(2.5f, 11f), 43f);
        return root;
    }

    private static void AddFallbackCheckLine(
        Transform parent,
        string name,
        Vector2 anchoredPosition,
        Vector2 size,
        float rotation)
    {
        var line = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        line.transform.SetParent(parent, false);
        var rect = (RectTransform)line.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        rect.localEulerAngles = new Vector3(0f, 0f, rotation);
        var image = line.GetComponent<Image>();
        image.color = HermesNativeUiFramework.AccentTextColor;
        image.raycastTarget = false;
    }

    private sealed class NativeFleaCheckboxStyle
    {
        public Sprite? BackgroundSprite { get; set; }
        public Color BackgroundColor { get; set; }
        public Image.Type BackgroundType { get; set; }
        public bool BackgroundPreserveAspect { get; set; }
        public Sprite? CheckmarkSprite { get; set; }
        public Color CheckmarkColor { get; set; }
        public Image.Type CheckmarkType { get; set; }
        public bool CheckmarkPreserveAspect { get; set; }
        public ColorBlock ToggleColors { get; set; }
    }

    private static Button AddButton(
        Transform parent,
        string text,
        UnityAction action,
        float width,
        bool interactable = true,
        bool selected = false,
        float height = CompactControlHeight,
        float fontSize = 12.5f)
    {
        var root = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.ButtonBackgroundSprite;
        image.type = image.sprite != null && image.sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        image.color = selected ? new Color(0.78f, 0.77f, 0.70f, 0.95f) : new Color(0.24f, 0.25f, 0.24f, 0.82f);
        image.raycastTarget = true;
        var button = root.GetComponent<Button>();
        button.targetGraphic = image;
        button.interactable = interactable;
        button.transition = Selectable.Transition.ColorTint;
        button.onClick.AddListener(action);
        var colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = selected ? new Color(0.90f, 0.89f, 0.82f, 1f) : new Color(0.37f, 0.38f, 0.36f, 1f);
        colors.pressedColor = new Color(0.16f, 0.17f, 0.16f, 1f);
        colors.disabledColor = new Color(0.12f, 0.13f, 0.13f, 0.55f);
        button.colors = colors;
        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleHeight = 0f;
        var label = HermesNativeUiFramework.CreateText("Label", root.transform, fontSize, true, TextAlignmentOptions.Center);
        label.text = text;
        label.color = selected ? new Color32(26, 28, 27, 255) : HermesNativeUiFramework.NormalTextColor;
        HermesNativeUiFramework.Stretch(label.rectTransform, 7f, 2f, 7f, 2f);
        return button;
    }

    private static TMP_Text AddText(
        Transform parent,
        string text,
        float size,
        bool bold,
        Color color,
        TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        float? preferredWidth = null)
    {
        var label = HermesNativeUiFramework.CreateText("Text", parent, size, bold, alignment);
        label.text = text ?? string.Empty;
        label.color = color;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (preferredWidth.HasValue)
        {
            layout.minWidth = preferredWidth.Value;
            layout.preferredWidth = preferredWidth.Value;
        }
        else
        {
            layout.flexibleWidth = 1f;
        }
        layout.minHeight = Math.Max(18f, size + 5f);
        layout.flexibleHeight = 0f;
        var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return label;
    }

    private static RectTransform AddCard(
        Transform parent,
        string title,
        string body,
        string meta,
        Action? onClick = null,
        Color? color = null)
    {
        var types = onClick is null
            ? new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) }
            : new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) };
        var root = new GameObject("Card", types);
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        var image = root.GetComponent<Image>();
        image.color = color ?? HermesNativeUiFramework.RowColor;
        image.raycastTarget = onClick is not null;
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(9, 9, 6, 6);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var cardElement = root.GetComponent<LayoutElement>();
        cardElement.minHeight = 48f;
        cardElement.flexibleHeight = 0f;
        if (onClick is not null)
        {
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            button.onClick.AddListener(() => onClick());
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(image.color.r + 0.08f, image.color.g + 0.08f, image.color.b + 0.08f, Math.Max(0.88f, image.color.a));
            colors.pressedColor = new Color(0.12f, 0.13f, 0.13f, 0.92f);
            button.colors = colors;
        }

        AddText(root.transform, title, 15f, true, HermesNativeUiFramework.NormalTextColor);
        if (!string.IsNullOrWhiteSpace(body))
        {
            AddText(root.transform, body, 13f, false, HermesNativeUiFramework.NormalTextColor);
        }
        if (!string.IsNullOrWhiteSpace(meta))
        {
            AddText(root.transform, meta, 11.5f, false, HermesNativeUiFramework.MutedTextColor);
        }
        AddBottomSeparator(root.transform);
        return rect;
    }

    private static void AddSectionHeader(Transform parent, string title)
    {
        HermesNativeUiFramework.CreateSectionHeader(parent, title, CompactSectionHeight);
    }

    private static void AddEmptyState(Transform parent, string title, string detail)
    {
        var card = AddCard(parent, title, detail, "");
        card.GetComponent<Image>().color = new Color(0.02f, 0.025f, 0.025f, 0.42f);
    }

    private static void AddMetricGrid(Transform parent, params (string Label, string Value)[] metrics)
    {
        if (metrics.Length == 0)
        {
            return;
        }

        var root = new GameObject("MetricGrid", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var rootLayout = root.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = 3f;
        rootLayout.childAlignment = TextAnchor.UpperLeft;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var rootElement = root.GetComponent<LayoutElement>();
        rootElement.minHeight = CompactMetricHeight;
        rootElement.flexibleHeight = 0f;

        var columns = metrics.Length switch
        {
            <= 4 => metrics.Length,
            5 or 6 => 3,
            _ => 4
        };
        for (var offset = 0; offset < metrics.Length; offset += columns)
        {
            var row = new GameObject("MetricRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(root.transform, false);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 3f;
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            var rowElement = row.GetComponent<LayoutElement>();
            rowElement.minHeight = CompactMetricHeight;
            rowElement.preferredHeight = CompactMetricHeight;
            rowElement.flexibleHeight = 0f;

            var count = Math.Min(columns, metrics.Length - offset);
            for (var index = 0; index < count; index++)
            {
                var metric = metrics[offset + index];
                var cell = HermesNativeUiFramework.CreatePanel("Metric", row.transform, HermesNativeUiFramework.RowColor);
                var cellElement = cell.gameObject.AddComponent<LayoutElement>();
                cellElement.minWidth = 145f;
                cellElement.preferredWidth = 210f;
                cellElement.flexibleWidth = 1f;
                cellElement.minHeight = CompactMetricHeight;
                cellElement.preferredHeight = CompactMetricHeight;
                cellElement.flexibleHeight = 0f;
                var cellLayout = cell.gameObject.AddComponent<VerticalLayoutGroup>();
                cellLayout.padding = new RectOffset(8, 8, 4, 4);
                cellLayout.spacing = 0f;
                cellLayout.childAlignment = TextAnchor.MiddleLeft;
                cellLayout.childControlWidth = true;
                cellLayout.childControlHeight = true;
                cellLayout.childForceExpandWidth = true;
                cellLayout.childForceExpandHeight = false;
                AddText(cell, metric.Label, 10f, true, HermesNativeUiFramework.MutedTextColor);
                AddText(cell, metric.Value, 13.5f, true, HermesNativeUiFramework.NormalTextColor);
            }

            for (var index = count; index < columns; index++)
            {
                var spacer = new GameObject("MetricSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacer.transform.SetParent(row.transform, false);
                var spacerElement = spacer.GetComponent<LayoutElement>();
                spacerElement.minWidth = 145f;
                spacerElement.preferredWidth = 210f;
                spacerElement.flexibleWidth = 1f;
                spacerElement.minHeight = CompactMetricHeight;
                spacerElement.preferredHeight = CompactMetricHeight;
                spacerElement.flexibleHeight = 0f;
            }
        }
    }

    private (RectTransform Root, RectTransform Content, ScrollRect Scroll) CreateScroll(Transform parent, string key, bool flexibleHeight)
    {
        var scroll = HermesNativeUiFramework.CreateScrollView(parent, key);
        var layout = scroll.Root.gameObject.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.flexibleHeight = flexibleHeight ? 1f : 0f;
        layout.minHeight = flexibleHeight ? 120f : 72f;
        _activeScrolls[key] = scroll.ScrollRect;
        return (scroll.Root, scroll.Content, scroll.ScrollRect);
    }

    private static void AddAnchoredBottomRule(RectTransform parent)
    {
        var separator = new GameObject("BottomRule", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var rect = (RectTransform)separator.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = new Vector2(0f, 1f);
        var layout = separator.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
    }

    private static void AddBottomSeparator(Transform parent)
    {
        var separator = new GameObject("Separator", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
        separator.GetComponent<LayoutElement>().preferredHeight = 1f;
        separator.GetComponent<LayoutElement>().minHeight = 1f;
    }

    private void CaptureScrollPositions()
    {
        foreach (var pair in _activeScrolls)
        {
            if (pair.Value != null)
            {
                _savedScrollPositions[pair.Key] = pair.Value.verticalNormalizedPosition;
            }
        }
    }

    private string BuildItemResultSetKey()
    {
        if (_state is null)
        {
            return string.Empty;
        }

        var results = _state.SearchResults;
        return string.Join("|",
            _state.SearchQuery.Trim(),
            _state.SearchLoading,
            RuntimeHelpers.GetHashCode(results),
            results.Count);
    }

    private IEnumerator RestoreScrollPositionsNextFrame()
    {
        var forceTop = new HashSet<string>(_scrollsToForceTop, StringComparer.Ordinal);
        _scrollsToForceTop.Clear();

        yield return null;
        Canvas.ForceUpdateCanvases();
        RestoreScrollPositions(forceTop);

        // ContentSizeFitter and ScrollRect can perform one more layout pass after the first
        // canvas update. Reassert forced-top lists once more so a new item search never lands
        // at the bottom because the prior empty result view reported a normalized value of 0.
        if (forceTop.Count > 0)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            RestoreScrollPositions(forceTop);
        }
    }

    private void RestoreScrollPositions(HashSet<string> forceTop)
    {
        foreach (var pair in _activeScrolls)
        {
            var scroll = pair.Value;
            if (scroll == null)
            {
                continue;
            }

            var shouldForceTop = forceTop.Contains(pair.Key);
            if (!shouldForceTop && !_savedScrollPositions.TryGetValue(pair.Key, out _))
            {
                continue;
            }

            var position = shouldForceTop ? 1f : Mathf.Clamp01(_savedScrollPositions[pair.Key]);
            scroll.StopMovement();
            scroll.velocity = Vector2.zero;
            scroll.verticalNormalizedPosition = position;

            if (shouldForceTop && scroll.content != null)
            {
                var anchored = scroll.content.anchoredPosition;
                scroll.content.anchoredPosition = new Vector2(anchored.x, 0f);
                _savedScrollPositions[pair.Key] = 1f;
            }
        }
    }

    private void Invalidate(float delay = 0f)
    {
        _forceRebuild = true;
        _nextSyncAt = Time.unscaledTime + Math.Max(0f, delay);
    }

    private static string Money(long? amount) => amount.HasValue ? $"₽{amount.Value:N0}" : "N/A";
    private static string FormatCount(double value) => Math.Abs(value - Math.Round(value)) < 0.0001d ? Math.Round(value).ToString("N0") : value.ToString("0.##");
    private static string YesNo(bool value) => value ? "COVERED" : "MISSING";

    private static string FormatCurrency(long amount, string currency)
        => currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1d)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }
        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
        if (duration.TotalMinutes >= 1d)
        {
            return $"{(int)duration.TotalMinutes}m";
        }
        return $"{Math.Max(1, duration.Seconds)}s";
    }

    #endregion
}
