using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;

namespace Hermes.Client;

/// <summary>
/// Typed bridge between the existing HERMES data controllers and the native uGUI body.
/// The legacy panels remain responsible for requests, cancellation, profile isolation, and
/// cache invalidation; this bridge only exposes their current read-only state to uGUI.
/// </summary>
internal sealed class HermesNativeWorkspaceState
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly HermesWindow _window;
    private readonly HermesAssistantPanel _assistant;
    private readonly HermesAssistantNoticeService _notices;
    private readonly HermesHideoutPanel _hideout;
    private readonly HermesCraftPanel _crafts;
    private readonly HermesStashPanel _stash;
    private readonly HermesLoadoutPanel _loadout;

    private static readonly MethodInfo RunSearchMethod = Method(typeof(HermesWindow), "RunSearchAsync");
    private static readonly MethodInfo SelectItemMethod = Method(typeof(HermesWindow), "SelectItemAsync");
    private static readonly MethodInfo SelectStashInstanceMethod = Method(typeof(HermesWindow), "SelectStashInstanceAsync");
    private static readonly MethodInfo SubmitAssistantMethod = Method(typeof(HermesAssistantPanel), "SubmitPromptAsync");
    private static readonly MethodInfo SelectCraftMethod = Method(typeof(HermesCraftPanel), "SelectCraftAsync");
    private static readonly MethodInfo SelectAreaMethod = Method(typeof(HermesHideoutPanel), "SelectAreaAsync");
    private static readonly MethodInfo DismissNoticeMethod = Method(typeof(HermesAssistantNoticeService), "DismissNotice");

    internal HermesNativeWorkspaceState(HermesWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _assistant = GetField<HermesAssistantPanel>(_window, "_assistantPanel")
                     ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_assistantPanel");
        _notices = GetField<HermesAssistantNoticeService>(_window, "_noticeService")
                   ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_noticeService");
        _hideout = GetField<HermesHideoutPanel>(_window, "_hideoutPanel")
                   ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_hideoutPanel");
        _crafts = GetField<HermesCraftPanel>(_window, "_craftPanel")
                  ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_craftPanel");
        _stash = GetField<HermesStashPanel>(_window, "_stashPanel")
                 ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_stashPanel");
        _loadout = GetField<HermesLoadoutPanel>(_window, "_loadoutPanel")
                   ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_loadoutPanel");
    }

    internal HermesWindow Window => _window;
    internal string ActiveTab => HermesEftWindowReflection.ActiveTabName(_window);

    internal string SearchQuery
    {
        get => GetField<string>(_window, "_query") ?? string.Empty;
        set => SetField(_window, "_query", value ?? string.Empty);
    }

    internal string SearchStatus => GetField<string>(_window, "_status") ?? string.Empty;
    internal string DetailStatus => GetField<string>(_window, "_detailStatus") ?? string.Empty;
    internal bool SearchLoading => GetField<bool>(_window, "_searching");
    internal bool DetailLoading => GetField<bool>(_window, "_loadingDetails") || GetField<bool>(_window, "_loadingInstancePrice");
    internal IReadOnlyList<HermesItemSummary> SearchResults => GetField<IReadOnlyList<HermesItemSummary>>(_window, "_results") ?? [];
    internal HermesItemSummary? SelectedItem => GetField<HermesItemSummary>(_window, "_selectedItem");
    internal HermesItemSummary? AssistantSelectedItem => _window.GetAssistantSelectedItem();
    internal HermesTraderSummaryResponse? TraderSummary => GetField<HermesTraderSummaryResponse>(_window, "_traderSummary");
    internal HermesMarketSummaryResponse? MarketSummary => GetField<HermesMarketSummaryResponse>(_window, "_marketSummary");
    internal HermesItemHideoutUsageResponse? ItemUsage => GetField<HermesItemHideoutUsageResponse>(_window, "_hideoutUsage");
    internal IReadOnlyList<HermesStashInstanceSummary> StashInstances => GetField<IReadOnlyList<HermesStashInstanceSummary>>(_window, "_stashInstances") ?? [];
    internal string? SelectedStashInstanceKey => GetField<string>(_window, "_selectedStashInstanceKey");
    internal string? AssistantSelectedStashInstanceKey => _window.GetAssistantSelectedInstanceKey();
    internal HermesStashInstanceSummary? SelectedStashInstance => StashInstances.FirstOrDefault(instance =>
        string.Equals(instance.InstanceKey, SelectedStashInstanceKey, StringComparison.OrdinalIgnoreCase));
    internal long? DisplayedItemReferenceValue => SelectedStashInstance is { ConditionAdjustedReferenceValue: > 0 } selected
        ? selected.ConditionAdjustedReferenceValue
        : SelectedItem?.ReferencePrice;
    internal string DisplayedItemReferenceLabel => SelectedStashInstance switch
    {
        { ChildItemCount: > 0 } => "ASSEMBLED VALUE",
        not null => "INSTANCE VALUE",
        _ => "REFERENCE"
    };

    internal string AssistantStatus => GetField<string>(_assistant, "_status") ?? string.Empty;
    internal bool AssistantLoading => GetField<bool>(_assistant, "_loading");
    internal IReadOnlyList<HermesNativeAssistantMessageData> AssistantMessages => ReadAssistantMessages();
    internal IReadOnlyList<string> AssistantSuggestedPrompts => _assistant.GetSuggestedPromptButtons(AssistantSelectedItem);
    internal IReadOnlyList<HermesNativeNoticeData> Notices => ReadNotices();
    internal bool NoticesLoading => GetField<bool>(_notices, "_checking");
    internal string NoticeStatus => GetField<string>(_notices, "_status") ?? string.Empty;
    internal int ActiveNoticeCount => Notices.Count(notice => !notice.Dismissed);
    internal bool WorkspaceReady => HermesWorkspaceSnapshotCoordinator.Current?.HasLoadedWorkspaceData == true;
    internal bool WorkspaceInitialLoading => HermesWorkspaceSnapshotCoordinator.Current?.IsInitialWorkspaceLoadActive == true;

    internal HermesActionProposal? ActionProposal => GetField<HermesActionProposal>(_window, "_actionProposal");
    internal HermesActionResultResponse? ActionResult => GetField<HermesActionResultResponse>(_window, "_actionResult");
    internal HermesActionHistoryResponse? ActionHistory => GetField<HermesActionHistoryResponse>(_window, "_actionHistory");
    internal string ActionStatus => GetField<string>(_window, "_actionStatus") ?? string.Empty;
    internal bool ActionLoading => GetField<bool>(_window, "_actionLoading");
    internal IReadOnlyCollection<string> SelectedTagActionInstanceKeys => _window.SelectedTagActionInstanceKeys;
    internal string TagActionMode
    {
        get => _window.TagActionMode;
        set => _window.TagActionMode = value;
    }
    internal string TagDraftName
    {
        get => _window.TagDraftName;
        set => _window.TagDraftName = value;
    }
    internal string TagDraftColor
    {
        get => _window.TagDraftColor;
        set => _window.TagDraftColor = value;
    }

    internal string AssistantContextLabel
    {
        get
        {
            var assistantItem = AssistantSelectedItem;
            if (assistantItem is null)
            {
                return "ACTIVE PMC";
            }

            var suffix = string.IsNullOrWhiteSpace(AssistantSelectedStashInstanceKey)
                ? string.Empty
                : " • EXACT COPY";
            var maximumNameLength = suffix.Length == 0 ? 28 : 18;
            var name = assistantItem.Name.Length <= maximumNameLength
                ? assistantItem.Name
                : assistantItem.Name[..Math.Max(1, maximumNameLength - 1)].TrimEnd() + "…";
            return name + suffix;
        }
    }

    internal string AssistantOverviewStatus
    {
        get
        {
            if (AssistantLoading)
            {
                return string.IsNullOrWhiteSpace(AssistantStatus)
                    ? "HERMES IS PREPARING A RESPONSE"
                    : AssistantStatus;
            }

            if (!WorkspaceReady)
            {
                return WorkspaceInitialLoading
                    ? "PREPARING SHARED PMC PROFILE DATA"
                    : "WAITING FOR SHARED PMC PROFILE DATA";
            }

            if (NoticesLoading)
            {
                return "CHECKING THE LOADED PMC PROFILE";
            }

            if (!string.IsNullOrWhiteSpace(AssistantStatus))
            {
                return AssistantStatus;
            }

            return string.IsNullOrWhiteSpace(NoticeStatus)
                ? "CURRENT PROFILE DATA READY"
                : NoticeStatus;
        }
    }

    internal HermesHideoutSummaryResponse? HideoutSummary => GetField<HermesHideoutSummaryResponse>(_hideout, "_summary");
    internal HermesHideoutAreaSummary? SelectedHideoutArea => GetField<HermesHideoutAreaSummary>(_hideout, "_selectedArea");
    internal HermesHideoutAreaDetailResponse? HideoutDetail => GetField<HermesHideoutAreaDetailResponse>(_hideout, "_detail");
    internal bool HideoutLoading => GetField<bool>(_hideout, "_loading") || GetField<bool>(_hideout, "_detailLoading");
    internal string HideoutStatus => GetField<string>(_hideout, "_status") ?? string.Empty;

    internal HermesCraftsResponse? CraftSummary => GetField<HermesCraftsResponse>(_crafts, "_response");
    internal HermesCraftSummary? SelectedCraft => GetField<HermesCraftSummary>(_crafts, "_selectedCraft");
    internal HermesCraftDetailResponse? CraftDetail => GetField<HermesCraftDetailResponse>(_crafts, "_detail");
    internal bool CraftLoading => GetField<bool>(_crafts, "_loading") || GetField<bool>(_crafts, "_detailLoading");
    internal string CraftStatus => GetField<string>(_crafts, "_status") ?? string.Empty;

    internal HermesStashSummaryResponse? StashSummary => GetField<HermesStashSummaryResponse>(_stash, "_summary");
    internal bool StashLoading => GetField<bool>(_stash, "_loading");
    internal string StashStatus => GetField<string>(_stash, "_status") ?? string.Empty;

    internal HermesLoadoutSummaryResponse? LoadoutSummary => GetField<HermesLoadoutSummaryResponse>(_loadout, "_summary");
    internal bool LoadoutLoading => GetField<bool>(_loadout, "_loading");
    internal string LoadoutStatus => GetField<string>(_loadout, "_status") ?? string.Empty;

    internal void RunSearch()
    {
        _ = RunSearchMethod.Invoke(_window, null);
    }

    internal void SelectItem(HermesItemSummary item)
    {
        _ = SelectItemMethod.Invoke(_window, [item, null, true]);
    }

    internal void OpenLoadoutItem(string? profileItemId, string itemName)
    {
        _window.OpenForLoadoutItem(profileItemId ?? string.Empty, itemName);
    }

    internal void OpenNamedItem(string itemName, string sourceLabel)
    {
        _window.OpenForNamedItem(itemName, sourceLabel);
    }

    internal void SelectStashInstance(string? instanceKey)
    {
        _ = SelectStashInstanceMethod.Invoke(_window, [instanceKey]);
    }

    internal void SubmitAssistant(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || AssistantLoading)
        {
            return;
        }

        _ = SubmitAssistantMethod.Invoke(
            _assistant,
            [prompt.Trim(), AssistantSelectedItem, AssistantSelectedStashInstanceKey, true]);
    }

    internal void ClearAssistant() => _assistant.Clear();

    internal void ClearAssistantContext() => _window.ClearAssistantContext();

    internal void RefreshAssistantFromCache()
    {
        var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
        if (coordinator is null)
        {
            RefreshNotices();
            return;
        }

        coordinator.EnsureInitialLoad();
        _ = coordinator.RefreshWorkspaceAsync("Assistant", manual: true);
    }

    internal void CheckPreparedAssistantFeed()
        => _ = _notices.RefreshFromPreparedServerAsync(manual: true);

    internal void RefreshNotices() => _notices.UpdateFromWorkspaceData(
        profileToken: null,
        loadout: LoadoutSummary,
        hideout: HideoutSummary,
        crafts: CraftSummary,
        stash: StashSummary,
        manual: true);
    internal void ClearNotices() => _notices.Clear();

    internal void OpenNotice(HermesNativeNoticeData notice)
    {
        _window.OpenNativeNoticeTarget(notice.TargetTab);
    }

    internal void DismissNotice(HermesNativeNoticeData notice)
    {
        if (notice.Source is not null)
        {
            DismissNoticeMethod.Invoke(_notices, [notice.Source]);
        }
    }

    internal void SelectCraft(HermesCraftSummary craft)
    {
        _ = SelectCraftMethod.Invoke(_crafts, [craft]);
    }

    internal void SelectHideoutArea(HermesHideoutAreaSummary area)
    {
        _ = SelectAreaMethod.Invoke(_hideout, [area]);
    }

    internal void ProposeTestAction() => _ = _window.ProposeTestActionAsync();
    internal void ProposeInventoryTagAction() => _ = _window.ProposeInventoryTagActionAsync();
    internal void ProposeInventoryTagAction(string mode, string tagName, string tagColor, params string[] instanceKeys)
        => _ = _window.ProposeInventoryTagActionAsync(mode, tagName, tagColor, instanceKeys);
    internal void ProposeCraftCollectAction(bool collectAllCompleted, params string[] productionKeys)
        => _ = _window.ProposeCraftCollectActionAsync(productionKeys, collectAllCompleted);
    internal void ToggleTagActionInstance(string instanceKey) => _window.ToggleTagActionInstance(instanceKey);
    internal void SelectAllMatchingTagActionInstances() => _window.SelectAllMatchingTagActionInstances();
    internal void ClearTagActionSelection() => _window.ClearTagActionSelection();
    internal void ConfirmAction() => _ = _window.ConfirmActionAsync();
    internal void CancelAction() => _ = _window.CancelActionAsync();
    internal void RefreshActionHistory() => _ = _window.RefreshActionHistoryAsync();

    internal void RefreshActive() => HermesEftWindowReflection.Refresh(_window);
    internal void ClearActive() => HermesEftWindowReflection.Clear(_window);
    internal void Navigate(string tabName) => _window.OpenNativeNoticeTarget(tabName);

    internal string BuildFingerprint()
    {
        var tab = ActiveTab;
        return tab switch
        {
            "Assistant" => string.Join("|",
                tab,
                AssistantMessages.Count,
                AssistantMessages.LastOrDefault()?.Text,
                Notices.Count,
                Notices.Count(notice => !notice.Dismissed),
                AssistantStatus,
                AssistantLoading,
                NoticeStatus,
                NoticesLoading,
                WorkspaceReady,
                WorkspaceInitialLoading,
                AssistantContextLabel),
            "ItemSearch" => string.Join("|",
                tab,
                SearchQuery,
                SearchStatus,
                DetailStatus,
                SearchLoading,
                DetailLoading,
                Identity(SearchResults),
                SearchResults.Count,
                SearchResults.LastOrDefault()?.ItemKey,
                Identity(SelectedItem),
                SelectedItem?.ItemKey,
                SelectedStashInstanceKey,
                Identity(TraderSummary),
                TraderSummary?.BestSellOffer?.RoubleEquivalent,
                TraderSummary?.PurchaseOffers.Count,
                Identity(MarketSummary),
                MarketSummary?.ComparableOfferCount,
                MarketSummary?.MedianPrice,
                Identity(ItemUsage),
                ItemUsage?.QuestUses.Count,
                ItemUsage?.QuestKeyUses.Count,
                ItemUsage?.UpgradeUses.Count,
                ItemUsage?.ProducedBy.Count,
                ItemUsage?.UsedBy.Count,
                ActionStatus,
                ActionLoading,
                Identity(ActionProposal),
                ActionProposal?.ProposalId,
                ActionProposalExpiryState(ActionProposal),
                ActionProposal?.CanExecute,
                Identity(ActionResult),
                ActionResult?.Status,
                ActionResult?.Message,
                TagActionMode,
                TagDraftName,
                TagDraftColor,
                Plugin.Settings.AllowInventoryTagActions.Value,
                string.Join(",", SelectedTagActionInstanceKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)),
                string.Join(",", StashInstances.Select(instance => $"{instance.InstanceKey}:{instance.TagName}:{instance.TagColor}"))),
            "Actions" => string.Join("|",
                tab,
                ActionStatus,
                ActionLoading,
                Identity(ActionProposal),
                ActionProposal?.ProposalId,
                ActionProposalExpiryState(ActionProposal),
                ActionProposal?.CanExecute,
                Identity(ActionResult),
                ActionResult?.Status,
                ActionResult?.Message,
                Identity(ActionHistory),
                ActionHistory?.TotalActions,
                ActionHistory?.Entries.FirstOrDefault()?.HistoryId,
                TagActionMode,
                Plugin.Settings.AllowInventoryTagActions.Value,
                string.Join(",", SelectedTagActionInstanceKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)),
                StashInstances.Count,
                SelectedItem?.ItemKey),
            "Hideout" => string.Join("|",
                tab,
                HideoutStatus,
                HideoutLoading,
                Identity(HideoutSummary),
                HideoutSummary?.Areas.Count,
                HideoutSummary?.ActiveProductions.Count,
                HideoutSummary?.ReadyAreaCount,
                Identity(SelectedHideoutArea),
                SelectedHideoutArea?.AreaKey,
                Identity(HideoutDetail),
                HideoutDetail?.Requirements.Count,
                HideoutDetail?.EstimatedMissingAcquisitionCost),
            "Crafts" => string.Join("|",
                tab,
                CraftStatus,
                CraftLoading,
                Identity(CraftSummary),
                CraftSummary?.Crafts.Count,
                CraftSummary?.Crafts.Count(craft => craft.CanStartNow),
                Identity(SelectedCraft),
                SelectedCraft?.CraftKey,
                Identity(CraftDetail),
                CraftDetail?.Ingredients.Count,
                CraftDetail?.Craft?.EstimatedBestSaleProfit),
            "Stash" => string.Join("|",
                tab,
                StashStatus,
                StashLoading,
                Identity(StashSummary),
                StashSummary?.GeneratedUnixTime,
                StashSummary?.IndependentItemCount,
                StashSummary?.Recommendations.Count,
                StashSummary?.CleanupCandidates.Count,
                StashSummary?.DuplicateGroups.Count,
                StashSummary?.DamagedOrDepletedItems.Count),
            "Loadout" or "RaidPlanner" => string.Join("|",
                tab,
                LoadoutStatus,
                LoadoutLoading,
                Identity(LoadoutSummary),
                LoadoutSummary?.GeneratedUnixTime,
                LoadoutSummary?.ReadinessScore,
                LoadoutSummary?.Warnings.Count,
                LoadoutSummary?.RaidPlans.Count,
                LoadoutSummary?.ValueSummary.Items.Count),
            _ => tab
        };
    }

    private static string ActionProposalExpiryState(HermesActionProposal? proposal)
    {
        if (proposal is null)
        {
            return "none";
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= proposal.ExpiresUnixTime
            ? $"{proposal.ExpiresUnixTime}:expired"
            : $"{proposal.ExpiresUnixTime}:active";
    }

    private static int Identity(object? value)
        => value is null ? 0 : RuntimeHelpers.GetHashCode(value);

    private IReadOnlyList<HermesNativeAssistantMessageData> ReadAssistantMessages()
    {
        var source = GetField<object>(_assistant, "_messages") as IEnumerable;
        if (source is null)
        {
            return [];
        }

        var output = new List<HermesNativeAssistantMessageData>();
        foreach (var message in source)
        {
            if (message is null)
            {
                continue;
            }

            var actions = new List<HermesNativeAssistantActionData>();
            var actionSource = Property(message, "Actions") as IEnumerable;
            if (actionSource is not null)
            {
                foreach (var action in actionSource)
                {
                    if (action is null)
                    {
                        continue;
                    }

                    actions.Add(new HermesNativeAssistantActionData(
                        Property(action, "Label") as string ?? "OPEN",
                        Property(action, "TabName") as string ?? "Assistant"));
                }
            }

            output.Add(new HermesNativeAssistantMessageData(
                Property(message, "IsUser") is true,
                Property(message, "Text") as string ?? string.Empty,
                Property(message, "Source") as string ?? string.Empty,
                actions));
        }

        return output;
    }

    private IReadOnlyList<HermesNativeNoticeData> ReadNotices()
    {
        var source = GetField<object>(_notices, "_notices") as IEnumerable;
        if (source is null)
        {
            return [];
        }

        var output = new List<HermesNativeNoticeData>();
        foreach (var notice in source)
        {
            if (notice is null)
            {
                continue;
            }

            output.Add(new HermesNativeNoticeData(
                Property(notice, "Severity") as string ?? "Info",
                Property(notice, "Category") as string ?? string.Empty,
                Property(notice, "Title") as string ?? "HERMES notice",
                Property(notice, "Message") as string ?? string.Empty,
                Property(notice, "TargetTab") as string ?? "Assistant",
                Property(notice, "Dismissed") is true,
                notice));
        }

        return output;
    }

    private static object? Property(object owner, string name)
        => owner.GetType().GetProperty(name, InstanceFlags)?.GetValue(owner);

    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, InstanceFlags)
           ?? throw new MissingMethodException(type.FullName, name);

    private static T? GetField<T>(object owner, string name)
    {
        var value = FindField(owner.GetType(), name)?.GetValue(owner);
        if (value is T typed)
        {
            return typed;
        }

        return default;
    }

    private static void SetField<T>(object owner, string name, T value)
    {
        var field = FindField(owner.GetType(), name)
                    ?? throw new MissingFieldException(owner.GetType().FullName, name);
        field.SetValue(owner, value);
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, InstanceFlags);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }
}

internal sealed record HermesNativeAssistantMessageData(
    bool IsUser,
    string Text,
    string Source,
    IReadOnlyList<HermesNativeAssistantActionData> Actions);

internal sealed record HermesNativeAssistantActionData(string Label, string TabName);

internal sealed record HermesNativeNoticeData(
    string Severity,
    string Category,
    string Title,
    string Message,
    string TargetTab,
    bool Dismissed,
    object? Source);
