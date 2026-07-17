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
    internal HermesTraderSummaryResponse? TraderSummary => GetField<HermesTraderSummaryResponse>(_window, "_traderSummary");
    internal HermesMarketSummaryResponse? MarketSummary => GetField<HermesMarketSummaryResponse>(_window, "_marketSummary");
    internal HermesItemHideoutUsageResponse? ItemUsage => GetField<HermesItemHideoutUsageResponse>(_window, "_hideoutUsage");
    internal IReadOnlyList<HermesStashInstanceSummary> StashInstances => GetField<IReadOnlyList<HermesStashInstanceSummary>>(_window, "_stashInstances") ?? [];
    internal string? SelectedStashInstanceKey => GetField<string>(_window, "_selectedStashInstanceKey");

    internal string AssistantStatus => GetField<string>(_assistant, "_status") ?? string.Empty;
    internal bool AssistantLoading => GetField<bool>(_assistant, "_loading");
    internal IReadOnlyList<HermesNativeAssistantMessageData> AssistantMessages => ReadAssistantMessages();
    internal IReadOnlyList<HermesNativeNoticeData> Notices => ReadNotices();
    internal bool NoticesLoading => GetField<bool>(_notices, "_checking");
    internal string NoticeStatus => GetField<string>(_notices, "_status") ?? string.Empty;

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
            [prompt.Trim(), SelectedItem, SelectedStashInstanceKey, true]);
    }

    internal void ClearAssistant() => _assistant.Clear();
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
                NoticesLoading),
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
                ItemUsage?.UpgradeUses.Count,
                ItemUsage?.ProducedBy.Count,
                ItemUsage?.UsedBy.Count),
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
