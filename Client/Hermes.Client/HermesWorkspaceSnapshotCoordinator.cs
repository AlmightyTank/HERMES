using System.Reflection;
using Hermes.Client.Models;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Owns one automatic full workspace load during profile startup and one after each raid.
/// Between those full loads, opening a workspace downloads that workspace's already-prepared
/// server summary so the screen always reflects the server cache. The top Refresh button is the
/// only path that first asks the server to recheck source data before downloading the active view.
/// </summary>
internal sealed class HermesWorkspaceSnapshotCoordinator
{
    private const float InitialDelaySeconds = 5f;
    private const float PostRaidReloadDelaySeconds = 5f;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const float RaidStatePollSeconds = 0.5f;
    private const float WorkspaceOpenDebounceSeconds = 1.5f;
    private static readonly Lazy<Func<bool>> RaidActiveReader = new(BuildRaidActiveReader);

    private static readonly FieldInfo HideoutPanelField = RequiredField(typeof(HermesWindow), "_hideoutPanel");
    private static readonly FieldInfo CraftPanelField = RequiredField(typeof(HermesWindow), "_craftPanel");
    private static readonly FieldInfo StashPanelField = RequiredField(typeof(HermesWindow), "_stashPanel");
    private static readonly FieldInfo LoadoutPanelField = RequiredField(typeof(HermesWindow), "_loadoutPanel");
    private static readonly FieldInfo NoticeServiceField = RequiredField(typeof(HermesWindow), "_noticeService");
    private static readonly FieldInfo RefreshStatusField = RequiredField(typeof(HermesWindow), "_refreshStatus");

    private readonly HermesWindow _window;
    private readonly object _pendingSync = new();
    private readonly object _sharedLoadoutSync = new();
    private readonly object _preRaidPrefetchSync = new();
    private readonly object _workspaceRefreshSync = new();
    private readonly Dictionary<string, Task> _workspaceRefreshTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _lastWorkspaceOpenRequestAt = new(StringComparer.OrdinalIgnoreCase);
    private HermesWorkspaceSnapshotResponse? _pendingSnapshot;
    private readonly Queue<DeltaBundle> _pendingDeltas = new();
    private HermesLoadoutSummaryResponse? _pendingSharedLoadout;
    private HermesLoadoutSummaryResponse? _cachedLoadout;
    private Task<HermesLoadoutSummaryResponse>? _sharedLoadoutTask;
    private Task<HermesLoadoutSummaryResponse>? _preRaidPrefetchTask;
    private HermesLoadoutSummaryResponse? _preRaidPreparedLoadout;
    private long _preRaidPreparedUnixTime;
    private int _preRaidPrefetchGeneration;
    private bool _loading;
    private bool _initialized;
    private bool _automaticFullLoadPending = true;
    private bool _automaticFullLoadAfterRaid;
    private bool _raidWasActive;
    private bool _legacyRefreshSuppressed;
    private float _nextRaidStateCheckAt;
    private float _nextCheckAt;
    private long _revision;
    private string? _profileToken;
    private string _hideoutSemanticFingerprint = string.Empty;
    private string _craftsSemanticFingerprint = string.Empty;
    private string _stashSemanticFingerprint = string.Empty;
    private string _loadoutSemanticFingerprint = string.Empty;

    internal static HermesWorkspaceSnapshotCoordinator? Current { get; private set; }

    /// <summary>
    /// True while the one automatic startup/post-raid full workspace load is running.
    /// There is no continuous client-side workspace watch in the materialized workspace pipeline.
    /// </summary>
    internal static bool IsBackgroundCheckActive => Current?._loading == true;

    /// <summary>
    /// True after Hideout, Crafts, Stash, and Loadout have all been loaded for the active PMC.
    /// Assistant refresh controls can then rebuild alerts locally without starting another server request.
    /// </summary>
    internal bool HasLoadedWorkspaceData => _initialized;

    /// <summary>
    /// True while a startup or post-raid full workspace snapshot is being prepared.
    /// </summary>
    internal bool IsInitialWorkspaceLoadActive => _loading;

    /// <summary>
    /// The exact loadout model currently displayed by the HERMES Loadout and Raid Planner pages.
    /// Pre-raid readiness reads this object directly instead of issuing a second independent request.
    /// </summary>
    internal HermesLoadoutSummaryResponse? CachedLoadout
    {
        get
        {
            // The Loadout panel is the user-visible source of truth. Prefer the exact object
            // currently rendered there, then fall back to the coordinator cache while the panel
            // is still being initialized.
            var panel = LoadoutPanelField.GetValue(_window);
            var displayed = panel is null
                ? null
                : Get<HermesLoadoutSummaryResponse>(panel, "_summary");
            return displayed is { Found: true } ? displayed : _cachedLoadout;
        }
    }

    /// <summary>
    /// Starts or joins one full shared loadout refresh. Pre-raid uses this after painting the
    /// existing HERMES findings so stale equipment or quest warnings cannot remain hidden.
    /// </summary>
    internal Task<HermesLoadoutSummaryResponse> RefreshSharedLoadoutAsync()
        => GetSharedLoadoutAsync(forceRefresh: true);

    /// <summary>
    /// Starts the expensive readiness/loadout refresh while the player is still on EFT's map
    /// selection screen. Every later pre-raid consumer joins this task or reads its completed
    /// result, so Insurance never launches a duplicate full analysis.
    /// </summary>
    internal Task<HermesLoadoutSummaryResponse> BeginPreRaidReadinessPrefetch()
    {
        TaskCompletionSource<HermesLoadoutSummaryResponse> completion;
        int generation;
        lock (_preRaidPrefetchSync)
        {
            if (_preRaidPrefetchTask is { IsCompleted: false })
            {
                return _preRaidPrefetchTask;
            }

            completion = new TaskCompletionSource<HermesLoadoutSummaryResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            generation = ++_preRaidPrefetchGeneration;
            _preRaidPrefetchTask = completion.Task;
        }

        _ = CompletePreRaidReadinessPrefetchAsync(completion, generation);
        return completion.Task;
    }

    private async Task CompletePreRaidReadinessPrefetchAsync(
        TaskCompletionSource<HermesLoadoutSummaryResponse> completion,
        int generation)
    {
        try
        {
            // Map selection is the one pre-raid point where accuracy matters more than merely
            // reopening a prepared workspace. Recheck the profile sources first so equipment,
            // vitals, insurance, and quest changes invalidate the server-held loadout response
            // before readiness joins the shared loadout request.
            try
            {
                var recheck = await HermesRevisionApiClient.RequestRecheckAsync();
                if (recheck.Accepted)
                {
                    var changes = await HermesRevisionApiClient.GetChangesAsync(_revision);
                    if (changes.Found)
                    {
                        if (!string.IsNullOrWhiteSpace(changes.ContextToken))
                        {
                            _profileToken = changes.ContextToken;
                        }

                        _revision = Math.Max(_revision, changes.Revision);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogWarning(
                        $"HERMES map-selection source recheck continued with the prepared loadout: {ex.Message}");
                }
            }

            var response = await GetSharedLoadoutAsync(forceRefresh: true);
            if (response is { Found: true })
            {
                lock (_preRaidPrefetchSync)
                {
                    if (generation == _preRaidPrefetchGeneration)
                    {
                        _preRaidPreparedLoadout = response;
                        _preRaidPreparedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                }
            }

            completion.TrySetResult(response);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES map-selection readiness preparation failed: {ex.Message}");
            completion.TrySetException(ex);
        }
    }

    internal bool TryGetPreparedPreRaidLoadout(
        out HermesLoadoutSummaryResponse? response,
        out long preparedUnixTime)
    {
        lock (_preRaidPrefetchSync)
        {
            response = _preRaidPreparedLoadout;
            preparedUnixTime = _preRaidPreparedUnixTime;
            return response is { Found: true };
        }
    }

    internal Task<HermesLoadoutSummaryResponse>? ActivePreRaidPrefetch
    {
        get
        {
            lock (_preRaidPrefetchSync)
            {
                return _preRaidPrefetchTask is { IsCompleted: false }
                    ? _preRaidPrefetchTask
                    : null;
            }
        }
    }

    private HermesWorkspaceSnapshotCoordinator(HermesWindow window)
    {
        _window = window;
        _nextCheckAt = Time.realtimeSinceStartup + InitialDelaySeconds;
        InvalidateProfileBoundRequests();
        SuppressLegacyAutomaticRefresh();
    }

    internal static HermesWorkspaceSnapshotCoordinator Configure(HermesWindow window)
    {
        Current = new HermesWorkspaceSnapshotCoordinator(window);
        return Current;
    }

    /// <summary>
    /// Compatibility entry point used by the native Assistant state. It may accelerate the
    /// one pending startup/post-raid full load, but it never starts another full workspace
    /// request after that one-shot load has completed.
    /// </summary>
    internal void EnsureInitialLoad()
    {
        if (_automaticFullLoadPending && !_loading)
        {
            _nextCheckAt = Time.realtimeSinceStartup;
        }
    }

    internal void OnPresentationOpened()
    {
        // Opening HERMES may accelerate the one startup load. Once that one-shot load has
        // completed, reopening the presentation refreshes only the currently visible workspace
        // from the server's prepared summary cache.
        if (_automaticFullLoadPending && !_loading)
        {
            _nextCheckAt = Time.realtimeSinceStartup;
            return;
        }

        if (_loading)
        {
            return;
        }

        QueueWorkspaceOpenRefresh(GetActiveTabName());
    }

    internal void RequestImmediateRecheck()
    {
        // Kept for compatibility with older callers. Refreshing is now explicit and tab-scoped.
        _ = RefreshWorkspaceAsync(GetActiveTabName(), manual: true);
    }

    internal void OnWorkspaceSelected(string tabName)
    {
        var normalized = NormalizeWorkspace(tabName);
        if (normalized is "ItemSearch" or "")
        {
            return;
        }

        // Never duplicate the startup/post-raid full load. The active tab will receive that
        // completed snapshot. Afterward, every workspace opening requests only its prepared
        // server summary; it does not trigger a source scan or a full multi-workspace load.
        if (_automaticFullLoadPending || _loading)
        {
            return;
        }

        QueueWorkspaceOpenRefresh(normalized);
    }

    private void QueueWorkspaceOpenRefresh(string tabName)
    {
        var normalized = NormalizeWorkspace(tabName);
        if (normalized is "ItemSearch" or "")
        {
            return;
        }

        var requestKey = WorkspaceRequestKey(normalized);
        var now = Time.realtimeSinceStartup;
        lock (_workspaceRefreshSync)
        {
            if (_lastWorkspaceOpenRequestAt.TryGetValue(requestKey, out var previous)
                && now - previous < WorkspaceOpenDebounceSeconds)
            {
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"HERMES reused the recent {requestKey} workspace-open refresh.");
                }
                return;
            }

            _lastWorkspaceOpenRequestAt[requestKey] = now;
        }

        _ = RefreshWorkspaceAsync(normalized, manual: false);
    }

    internal Task RefreshWorkspaceAsync(string tabName, bool manual)
    {
        var normalized = NormalizeWorkspace(tabName);
        if (normalized is "ItemSearch" or "")
        {
            return Task.CompletedTask;
        }

        var requestKey = WorkspaceRequestKey(normalized);
        lock (_workspaceRefreshSync)
        {
            if (_workspaceRefreshTasks.TryGetValue(requestKey, out var active)
                && !active.IsCompleted)
            {
                return active;
            }

            var task = RefreshWorkspaceCoreAsync(normalized, manual);
            _workspaceRefreshTasks[requestKey] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_workspaceRefreshSync)
                    {
                        if (_workspaceRefreshTasks.TryGetValue(requestKey, out var current)
                            && ReferenceEquals(current, task))
                        {
                            _workspaceRefreshTasks.Remove(requestKey);
                        }
                    }
                },
                TaskScheduler.Default);
            return task;
        }
    }

    internal void RefreshNoticesFromLoadedData(bool manual)
    {
        UpdateNoticeCandidates(manual);
    }

    internal void Tick()
    {
        ApplyPending();
        TrackRaidLifecycle();

        if (_loading || !_automaticFullLoadPending || Time.realtimeSinceStartup < _nextCheckAt)
        {
            return;
        }

        var afterRaid = _automaticFullLoadAfterRaid;
        _automaticFullLoadPending = false;
        _automaticFullLoadAfterRaid = false;
        _loading = true;
        RefreshStatusField.SetValue(
            _window,
            afterRaid
                ? "Refreshing all HERMES workspaces after the raid..."
                : "Loading the initial HERMES workspace snapshot...");
        _ = FetchInitialSnapshotAsync(afterRaid);
    }

    private async Task FetchInitialSnapshotAsync(bool afterRaid)
    {
        try
        {
            // One explicit source scan precedes the one full startup/post-raid load. Domain
            // responses are isolated so a slow Crafts route cannot discard successful Hideout,
            // Stash, and Loadout results or cause the client to retry every workspace.
            if (afterRaid)
            {
                try
                {
                    await HermesRevisionApiClient.RequestRecheckAsync();
                }
                catch (Exception ex)
                {
                    if (Plugin.Settings.DetailedLogging.Value)
                    {
                        Plugin.Log.LogWarning($"HERMES post-raid source recheck continued with current server data: {ex.Message}");
                    }
                }
            }

            var changes = await TryFetchAsync(
                () => HermesRevisionApiClient.GetChangesAsync(afterRaid ? _revision : 0),
                afterRaid ? "post-raid revisions" : "initial revisions");

            // GetChanges performs the server-side semantic source scan and invalidates only the
            // affected caches. Start the expensive domain summaries after that scan completes.
            var hideoutTask = TryFetchAsync(HermesWorkspaceSummaryApiClient.GetHideoutSummaryAsync, "full-load hideout");
            var craftsTask = TryFetchAsync(HermesWorkspaceSummaryApiClient.GetCraftsAsync, "full-load crafts");
            var stashTask = TryFetchAsync(
                () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                "full-load stash");
            var loadoutTask = TryFetchAsync(
                () => GetSharedLoadoutAsync(forceRefresh: true),
                "full-load loadout");

            await Task.WhenAll((Task)hideoutTask, craftsTask, stashTask, loadoutTask);

            // Register the server-owned Assistant feed without downloading the same four large
            // workspace payloads a second time. The screen applies the results already returned
            // by the parallel summary requests above.
            var preparedFeed = await TryFetchAsync(
                () => HermesRevisionApiClient.PrepareAssistantFeedAsync(
                    Plugin.Settings.CreateStashRequestSettings(),
                    Plugin.Settings.CreateLoadoutRequestSettings()),
                afterRaid ? "post-raid Assistant feed preparation" : "initial Assistant feed preparation");

            var hideout = hideoutTask.Result;
            var crafts = craftsTask.Result;
            var stash = stashTask.Result;
            var loadout = loadoutTask.Result;

            // A caller can reach its local timeout while the SPT server continues the same
            // materialization. PrepareAssistantFeed joins those server-side gates. Once it
            // reports success, recover any locally timed-out result from the completed cache
            // instead of leaving the first client snapshot incomplete.
            if (preparedFeed is { Prepared: true })
            {
                if (hideout is not { Found: true })
                {
                    hideout = await TryFetchAsync(
                        HermesWorkspaceSummaryApiClient.GetHideoutSummaryAsync,
                        "full-load cached hideout recovery");
                }
                if (crafts is not { Found: true })
                {
                    crafts = await TryFetchAsync(
                        HermesWorkspaceSummaryApiClient.GetCraftsAsync,
                        "full-load cached crafts recovery");
                }
                if (stash is not { Found: true })
                {
                    stash = await TryFetchAsync(
                        () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                        "full-load cached stash recovery");
                }
                if (loadout is not { Found: true })
                {
                    loadout = await TryFetchAsync(
                        () => GetSharedLoadoutAsync(forceRefresh: false),
                        "full-load cached loadout recovery");
                }
            }

            var snapshot = new HermesWorkspaceSnapshotResponse
            {
                Found = hideout is { Found: true }
                        || crafts is { Found: true }
                        || stash is { Found: true }
                        || loadout is { Found: true },
                Message = afterRaid
                    ? "Post-raid HERMES workspace load completed."
                    : "Initial HERMES workspace load completed.",
                ContextToken = preparedFeed?.ContextToken
                               ?? changes?.ContextToken
                               ?? _profileToken
                               ?? string.Empty,
                Revision = preparedFeed?.Revision ?? changes?.Revision ?? _revision,
                Domains = preparedFeed?.Domains ?? changes?.Domains ?? new HermesDomainRevisions(),
                Hideout = hideout ?? new HermesHideoutSummaryResponse(),
                Crafts = crafts ?? new HermesCraftsResponse(),
                Stash = stash ?? new HermesStashSummaryResponse(),
                Loadout = loadout ?? new HermesLoadoutSummaryResponse()
            };

            lock (_pendingSync)
            {
                _pendingSnapshot = snapshot;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES one-time full workspace load failed: {ex.Message}");
            RefreshStatusField.SetValue(
                _window,
                HermesApiClient.DescribeFailure(ex, afterRaid
                    ? "Post-raid HERMES workspace load"
                    : "Initial HERMES workspace load"));
            _initialized = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Returns one shared loadout request. Initial HERMES loading, the Loadout page, Raid Planner,
    /// and pre-raid readiness all join this task instead of calculating the same profile twice.
    /// </summary>
    internal Task<HermesLoadoutSummaryResponse> GetSharedLoadoutAsync(bool forceRefresh)
    {
        lock (_sharedLoadoutSync)
        {
            if (_sharedLoadoutTask is { IsCompleted: false })
            {
                return _sharedLoadoutTask;
            }

            if (!forceRefresh && _cachedLoadout is { Found: true })
            {
                return Task.FromResult(_cachedLoadout);
            }

            _sharedLoadoutTask = FetchSharedLoadoutAsync();
            return _sharedLoadoutTask;
        }
    }

    private async Task<HermesLoadoutSummaryResponse> FetchSharedLoadoutAsync()
    {
        try
        {
            var response = await HermesApiClient.GetLoadoutSummaryAsync(
                Plugin.Settings.CreateLoadoutRequestSettings());
            lock (_sharedLoadoutSync)
            {
                _cachedLoadout = response;
            }
            lock (_pendingSync)
            {
                _pendingSharedLoadout = response;
            }

            return response;
        }
        finally
        {
            lock (_sharedLoadoutSync)
            {
                _sharedLoadoutTask = null;
            }
        }
    }

    /// <summary>
    /// Performs one lightweight source revision check first. When inventory, vitals, and quest
    /// inputs did not change, the already-rendered HERMES loadout object is returned immediately.
    /// A full loadout analysis is requested only after the server reports a loadout-domain change.
    /// </summary>
    internal async Task<HermesLoadoutSummaryResponse> RevalidateLoadoutAsync()
    {
        var cached = _cachedLoadout;
        try
        {
            var changes = await HermesRevisionApiClient.GetChangesAsync(_revision);
            var profileChanged = !string.IsNullOrWhiteSpace(_profileToken)
                                 && !string.IsNullOrWhiteSpace(changes.ContextToken)
                                 && !string.Equals(
                                     _profileToken,
                                     changes.ContextToken,
                                     StringComparison.Ordinal);
            var loadoutChanged = changes.Changed.Any(domain =>
                domain.Equals("loadout", StringComparison.OrdinalIgnoreCase)
                || domain.Equals("raidPlanner", StringComparison.OrdinalIgnoreCase));

            if (!profileChanged && !loadoutChanged && cached is { Found: true })
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            // A failed lightweight revalidation should not discard useful already-loaded data.
            if (cached is { Found: true })
            {
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogWarning($"HERMES loadout revalidation used cached data: {ex.Message}");
                }

                return cached;
            }

            throw;
        }

        return await GetSharedLoadoutAsync(forceRefresh: true);
    }

    private async Task RefreshWorkspaceCoreAsync(string workspace, bool manual)
    {
        // Cached-on-open refreshes keep the currently rendered screen visible. Loading chrome is
        // shown only for a manual Refresh or when that workspace has never received data.
        var showLoading = manual || !HasWorkspaceData(workspace);
        if (showLoading)
        {
            MarkWorkspaceLoading(workspace, true);
        }

        try
        {
            HermesChangesResponse? changes = null;

            // Opening a workspace reads the server's already-prepared summary directly. Only the
            // top Refresh button asks the server to rescan source data first.
            if (manual)
            {
                var recheck = await HermesRevisionApiClient.RequestRecheckAsync();
                if (!recheck.Accepted)
                {
                    throw new InvalidOperationException(
                        recheck.Message ?? "The HERMES server did not accept the source recheck.");
                }

                // The source scan owns invalidation. GetChanges compares semantic source
                // fingerprints and retires only workspaces whose inputs actually changed. A
                // manual Refresh must not throw away materialized responses when the server
                // confirms that their source data is unchanged.
                try
                {
                    changes = await HermesRevisionApiClient.GetChangesAsync(_revision);
                    if (changes.Found)
                    {
                        if (!string.IsNullOrWhiteSpace(_profileToken)
                            && !string.IsNullOrWhiteSpace(changes.ContextToken)
                            && !string.Equals(_profileToken, changes.ContextToken, StringComparison.Ordinal))
                        {
                            InvalidateProfileBoundRequests();
                            ClearProfileBoundWorkspaceData();
                            ClearSemanticFingerprints();
                        }

                        if (!string.IsNullOrWhiteSpace(changes.ContextToken))
                        {
                            _profileToken = changes.ContextToken;
                        }

                        _revision = Math.Max(_revision, changes.Revision);
                    }
                }
                catch (Exception ex)
                {
                    // The explicit source recheck was accepted, so the selected summary can still
                    // be downloaded even when the lightweight revision response is unavailable.
                    if (Plugin.Settings.DetailedLogging.Value)
                    {
                        Plugin.Log.LogWarning(
                            $"HERMES {workspace} revision settle continued with the prepared server summary: {ex.Message}");
                    }
                }
            }

            HermesHideoutSummaryResponse? hideout = null;
            HermesCraftsResponse? crafts = null;
            HermesStashSummaryResponse? stash = null;
            HermesLoadoutSummaryResponse? loadout = null;

            if (workspace == "Assistant")
            {
                if (!manual)
                {
                    // Opening Assistant reads one display-ready server feed. It must never reopen
                    // Hideout, Crafts, Stash, and Loadout merely to rebuild alert cards in Unity.
                    if (NoticeServiceField.GetValue(_window) is HermesAssistantNoticeService preparedNotices)
                    {
                        await preparedNotices.RefreshFromPreparedServerAsync(manual: false);
                    }
                    return;
                }

                // Manual Assistant Refresh remains accurate but is now domain-selective. The
                // source scan above already invalidated changed materializations, so unchanged
                // Hideout, Crafts, Stash, and Loadout responses stay hot on the server.
                var refreshAllAssistantSources = changes is null || !changes.Found;
                var refreshHideout = refreshAllAssistantSources || HasChangedDomain(changes, "hideout");
                var refreshCrafts = refreshAllAssistantSources || HasChangedDomain(changes, "crafts");
                var refreshStash = refreshAllAssistantSources || HasChangedDomain(changes, "stash");
                var refreshLoadout = refreshAllAssistantSources
                                     || HasChangedDomain(changes, "loadout")
                                     || HasChangedDomain(changes, "raidPlanner");

                var hideoutTask = refreshHideout
                    ? TryFetchAsync(
                        HermesWorkspaceSummaryApiClient.GetHideoutSummaryAsync,
                        "Assistant refreshed hideout")
                    : Task.FromResult<HermesHideoutSummaryResponse?>(null);
                var craftsTask = refreshCrafts
                    ? TryFetchAsync(
                        HermesWorkspaceSummaryApiClient.GetCraftsAsync,
                        "Assistant refreshed crafts")
                    : Task.FromResult<HermesCraftsResponse?>(null);
                var stashTask = refreshStash
                    ? TryFetchAsync(
                        () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                        "Assistant refreshed stash")
                    : Task.FromResult<HermesStashSummaryResponse?>(null);
                var loadoutTask = refreshLoadout
                    ? TryFetchAsync(
                        () => GetSharedLoadoutAsync(forceRefresh: true),
                        "Assistant refreshed loadout")
                    : Task.FromResult<HermesLoadoutSummaryResponse?>(null);

                await Task.WhenAll((Task)hideoutTask, craftsTask, stashTask, loadoutTask);
                hideout = hideoutTask.Result;
                crafts = craftsTask.Result;
                stash = stashTask.Result;
                loadout = loadoutTask.Result;

                var prepared = await TryFetchAsync(
                    () => HermesRevisionApiClient.PrepareAssistantFeedAsync(
                        Plugin.Settings.CreateStashRequestSettings(),
                        Plugin.Settings.CreateLoadoutRequestSettings()),
                    "Assistant prepared feed registration");
                if (prepared is { Prepared: true })
                {
                    // Server preparation joins any materialization that outlived a local timeout.
                    // Recover only missing client models from the completed server cache.
                    if (refreshHideout && hideout is not { Found: true })
                    {
                        hideout = await TryFetchAsync(
                            HermesWorkspaceSummaryApiClient.GetHideoutSummaryAsync,
                            "Assistant cached hideout recovery");
                    }
                    if (refreshCrafts && crafts is not { Found: true })
                    {
                        crafts = await TryFetchAsync(
                            HermesWorkspaceSummaryApiClient.GetCraftsAsync,
                            "Assistant cached crafts recovery");
                    }
                    if (refreshStash && stash is not { Found: true })
                    {
                        stash = await TryFetchAsync(
                            () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                            "Assistant cached stash recovery");
                    }
                    if (refreshLoadout && loadout is not { Found: true })
                    {
                        loadout = await TryFetchAsync(
                            () => GetSharedLoadoutAsync(forceRefresh: false),
                            "Assistant cached loadout recovery");
                    }

                    changes = new HermesChangesResponse
                    {
                        Found = true,
                        ContextToken = prepared.ContextToken,
                        Revision = prepared.Revision,
                        Domains = prepared.Domains
                    };
                }
            }
            else if (workspace == "Hideout")
            {
                hideout = await HermesWorkspaceSummaryApiClient.GetHideoutSummaryAsync();
            }
            else if (workspace == "Crafts")
            {
                crafts = await HermesWorkspaceSummaryApiClient.GetCraftsAsync();
            }
            else if (workspace == "Stash")
            {
                stash = await HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings());
            }
            else if (workspace is "Loadout" or "RaidPlanner")
            {
                loadout = await GetSharedLoadoutAsync(forceRefresh: true);
            }

            if (manual && workspace != "Assistant")
            {
                // Keep the server-owned Assistant feed aligned with a manually refreshed domain.
                // The selected summary has already been rebuilt, and the other summaries remain
                // materialized, so this combined registration is a cache read rather than a second
                // analysis pass.
                var prepared = await TryFetchAsync(
                    () => HermesRevisionApiClient.PrepareAssistantFeedAsync(
                        Plugin.Settings.CreateStashRequestSettings(),
                        Plugin.Settings.CreateLoadoutRequestSettings()),
                    $"{workspace} Assistant feed registration");
                if (prepared is { Prepared: true })
                {
                    changes = new HermesChangesResponse
                    {
                        Found = true,
                        ContextToken = prepared.ContextToken,
                        Revision = prepared.Revision,
                        Domains = prepared.Domains
                    };
                }
            }

            lock (_pendingSync)
            {
                _pendingDeltas.Enqueue(new DeltaBundle(
                    changes ?? new HermesChangesResponse
                    {
                        Found = true,
                        ContextToken = _profileToken ?? string.Empty,
                        Revision = _revision
                    },
                    hideout,
                    crafts,
                    stash,
                    loadout,
                    manual));
            }

            // The downloaded server summary is applied on the Unity thread with the queued delta.
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES {workspace} refresh failed: {ex.Message}");
            RefreshStatusField.SetValue(_window, HermesApiClient.DescribeFailure(ex, $"{workspace} refresh"));
        }
        finally
        {
            if (showLoading)
            {
                MarkWorkspaceLoading(workspace, false);
            }
        }
    }

    private void TrackRaidLifecycle()
    {
        if (Time.realtimeSinceStartup < _nextRaidStateCheckAt)
        {
            return;
        }

        _nextRaidStateCheckAt = Time.realtimeSinceStartup + RaidStatePollSeconds;
        var raidActive = IsRaidActive();
        if (raidActive)
        {
            _raidWasActive = true;
            return;
        }

        if (!_raidWasActive)
        {
            return;
        }

        _raidWasActive = false;
        ScheduleAutomaticFullWorkspaceLoad(afterRaid: true, PostRaidReloadDelaySeconds);
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogInfo("HERMES detected raid completion and scheduled one post-raid full workspace load.");
        }
    }

    private void ScheduleAutomaticFullWorkspaceLoad(bool afterRaid, float delaySeconds)
    {
        _automaticFullLoadPending = true;
        _automaticFullLoadAfterRaid |= afterRaid;
        _nextCheckAt = Time.realtimeSinceStartup + Math.Max(0f, delaySeconds);
    }

    private bool HasWorkspaceData(string workspace)
    {
        if (workspace == "Assistant")
        {
            return _initialized;
        }

        if (workspace == "Hideout")
        {
            var panel = HideoutPanelField.GetValue(_window);
            return panel is not null
                   && Get<HermesHideoutSummaryResponse>(panel, "_summary") is { Found: true };
        }

        if (workspace == "Crafts")
        {
            var panel = CraftPanelField.GetValue(_window);
            return panel is not null
                   && Get<HermesCraftsResponse>(panel, "_response") is { Found: true };
        }

        if (workspace == "Stash")
        {
            var panel = StashPanelField.GetValue(_window);
            return panel is not null
                   && Get<HermesStashSummaryResponse>(panel, "_summary") is { Found: true };
        }

        if (workspace is "Loadout" or "RaidPlanner")
        {
            return CachedLoadout is { Found: true };
        }

        return false;
    }

    private void MarkWorkspaceLoading(string workspace, bool loading)
    {
        object? panel = workspace switch
        {
            "Hideout" => HideoutPanelField.GetValue(_window),
            "Crafts" => CraftPanelField.GetValue(_window),
            "Stash" => StashPanelField.GetValue(_window),
            "Loadout" or "RaidPlanner" => LoadoutPanelField.GetValue(_window),
            _ => null
        };
        if (panel is not null)
        {
            Set(panel, "_loading", loading);
        }
    }

    private string GetActiveTabName()
        => FindField(_window, "_activeTab")?.GetValue(_window)?.ToString() ?? "ItemSearch";

    private static bool HasChangedDomain(HermesChangesResponse? changes, string domain)
        => changes?.Changed?.Any(value =>
            string.Equals(value, domain, StringComparison.OrdinalIgnoreCase)) == true;

    private static string WorkspaceRequestKey(string workspace)
        => workspace is "RaidPlanner" ? "Loadout" : workspace;

    private static string NormalizeWorkspace(string? tabName)
    {
        var value = tabName?.Trim() ?? string.Empty;
        return value.ToLowerInvariant() switch
        {
            "assistant" or "chat" => "Assistant",
            "hideout" => "Hideout",
            "craft" or "crafts" => "Crafts",
            "stash" => "Stash",
            "loadout" => "Loadout",
            "raid" or "raidplanner" or "raid planner" => "RaidPlanner",
            _ => "ItemSearch"
        };
    }

    private static bool IsRaidActive()
    {
        try
        {
            return RaidActiveReader.Value();
        }
        catch
        {
            return false;
        }
    }

    private static Func<bool> BuildRaidActiveReader()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var gameWorldType = assemblies
                .Select(assembly => assembly.GetType("EFT.GameWorld", false))
                .FirstOrDefault(type => type is not null);
            if (gameWorldType is null)
            {
                return static () => false;
            }

            var directProperty = gameWorldType.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var directField = gameWorldType.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            PropertyInfo? singletonProperty = null;
            if (directProperty is null && directField is null)
            {
                var singletonType = assemblies
                    .Select(assembly => assembly.GetType("Comfort.Common.Singleton`1", false))
                    .FirstOrDefault(type => type is not null);
                if (singletonType is not null)
                {
                    singletonProperty = singletonType
                        .MakeGenericType(gameWorldType)
                        .GetProperty(
                            "Instance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
            }

            return () => directProperty?.GetValue(null) is not null
                         || directField?.GetValue(null) is not null
                         || singletonProperty?.GetValue(null) is not null;
        }
        catch
        {
            return static () => false;
        }
    }

    private void SuppressLegacyAutomaticRefresh()
    {
        if (_legacyRefreshSuppressed)
        {
            return;
        }

        _legacyRefreshSuppressed = true;
        foreach (var panel in new[]
                 {
                     HideoutPanelField.GetValue(_window),
                     CraftPanelField.GetValue(_window),
                     StashPanelField.GetValue(_window),
                     LoadoutPanelField.GetValue(_window)
                 })
        {
            if (panel is null)
            {
                continue;
            }

            Set(panel, "_nextAutomaticRefresh", float.PositiveInfinity);
        }
    }

    private void ApplyPending()
    {
        HermesWorkspaceSnapshotResponse? snapshot;
        List<DeltaBundle> deltas;
        HermesLoadoutSummaryResponse? sharedLoadout;
        lock (_pendingSync)
        {
            snapshot = _pendingSnapshot;
            deltas = _pendingDeltas.ToList();
            _pendingDeltas.Clear();
            sharedLoadout = _pendingSharedLoadout;
            _pendingSnapshot = null;
            _pendingSharedLoadout = null;
        }

        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }

        foreach (var delta in deltas)
        {
            ApplyDelta(delta);
        }

        if (sharedLoadout is { Found: true })
        {
            var changed = ApplyLoadout(sharedLoadout);
            ApplyRaidPlanner(sharedLoadout);
            if (changed)
            {
                UpdateNoticeCandidates(manual: false);
            }
        }
    }

    private void ApplySnapshot(HermesWorkspaceSnapshotResponse snapshot)
    {
        if (!string.IsNullOrWhiteSpace(_profileToken)
            && !string.IsNullOrWhiteSpace(snapshot.ContextToken)
            && !string.Equals(_profileToken, snapshot.ContextToken, StringComparison.Ordinal))
        {
            InvalidateProfileBoundRequests();
            ClearProfileBoundWorkspaceData();
            ClearSemanticFingerprints();
        }

        if (!snapshot.Found)
        {
            // The automatic full-load budget is one attempt. Missing workspaces can be refreshed
            // when selected or explicitly through the top Refresh button.
            _initialized = true;
            RefreshStatusField.SetValue(
                _window,
                snapshot.Message ?? "The automatic HERMES workspace load did not return profile data. Open a workspace or press Refresh to try that view.");
            return;
        }

        _profileToken = snapshot.ContextToken;
        _revision = snapshot.Revision;
        var loaded = new List<string>();

        if (snapshot.Hideout is { Found: true })
        {
            ApplyHideout(snapshot.Hideout);
            loaded.Add("Hideout");
        }

        if (snapshot.Crafts is { Found: true })
        {
            ApplyCrafts(snapshot.Crafts);
            loaded.Add("Crafts");
        }

        if (snapshot.Stash is { Found: true })
        {
            ApplyStash(snapshot.Stash);
            loaded.Add("Stash");
        }

        if (snapshot.Loadout is { Found: true })
        {
            ApplyLoadout(snapshot.Loadout);
            ApplyRaidPlanner(snapshot.Loadout);
            loaded.Add("Loadout / Raid Planner");
        }

        // A full-load attempt is complete even if one expensive domain timed out. Successful
        // domains stay visible, and an unavailable domain is retried only when selected or when
        // the player explicitly presses Refresh.
        _initialized = true;

        RefreshStatusField.SetValue(
            _window,
            loaded.Count == 4
                ? string.Empty
                : loaded.Count > 0
                    ? $"Loaded {string.Join(", ", loaded)}. Unavailable workspaces will refresh when opened."
                    : "No workspace summary loaded. Open a workspace or press Refresh to retry that view.");
        UpdateNoticeCandidates(manual: false);
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogDebug(
                $"Initial HERMES server snapshot loaded at revision {_revision}: {string.Join(", ", loaded)}.");
        }
    }

    private void ApplyDelta(DeltaBundle delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.Changes.ContextToken))
        {
            _profileToken = delta.Changes.ContextToken;
        }

        var changed = new List<string>();
        if (delta.Hideout is { Found: true } && ApplyHideout(delta.Hideout))
        {
            changed.Add("Hideout");
        }

        if (delta.Crafts is { Found: true } && ApplyCrafts(delta.Crafts))
        {
            changed.Add("Crafts");
        }

        if (delta.Stash is { Found: true } && ApplyStash(delta.Stash))
        {
            changed.Add("Stash");
        }

        if (delta.Loadout is { Found: true } && ApplyLoadout(delta.Loadout))
        {
            ApplyRaidPlanner(delta.Loadout);
            changed.Add("Loadout / Raid Planner");
        }

        _revision = Math.Max(_revision, delta.Changes.Revision);

        // Background server revisions are intentionally silent. They update only the affected
        // workspace models and never replace the normal workspace subtitle with diagnostic text.
        RefreshStatusField.SetValue(_window, string.Empty);
        if (changed.Count > 0 || delta.Manual)
        {
            UpdateNoticeCandidates(manual: delta.Manual);
        }
        if (Plugin.Settings.DetailedLogging.Value)
        {
            var displayed = changed.Count > 0
                ? string.Join(", ", changed)
                : string.Join(", ", delta.Changes.Changed);
            Plugin.Log.LogDebug(
                $"HERMES applied server revision {_revision}: {displayed}."
                + (string.IsNullOrWhiteSpace(delta.Changes.Reason)
                    ? string.Empty
                    : $" Source: {delta.Changes.Reason}."));
        }
    }


    private bool ApplyHideout(HermesHideoutSummaryResponse response)
    {
        var fingerprint = BuildHideoutSemanticFingerprint(response);
        if (string.Equals(_hideoutSemanticFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        _hideoutSemanticFingerprint = fingerprint;

        var panel = HideoutPanelField.GetValue(_window);
        if (panel is null)
        {
            return false;
        }

        IncrementInt(panel, "_refreshVersion");
        IncrementInt(panel, "_detailVersion");
        Set(panel, "_summary", response);
        Set(panel, "_loadRequested", true);
        Set(panel, "_loading", false);
        Set(panel, "_detailLoading", false);
        Set(panel, "_status", $"Loaded {response.Areas.Count:N0} hideout area(s) and {response.ActiveProductions.Count:N0} production(s).");

        // Summary opening is one request. Area acquisition details are demand-loaded only after
        // the player explicitly selects an area card.
        Set(panel, "_selectedArea", null);
        Set(panel, "_detail", null);
        Set(panel, "_detailLoading", false);

        return true;
    }

    private bool ApplyCrafts(HermesCraftsResponse response)
    {
        var fingerprint = BuildCraftsSemanticFingerprint(response);
        if (string.Equals(_craftsSemanticFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        _craftsSemanticFingerprint = fingerprint;

        var panel = CraftPanelField.GetValue(_window);
        if (panel is null)
        {
            return false;
        }

        IncrementInt(panel, "_refreshVersion");
        IncrementInt(panel, "_detailVersion");
        Set(panel, "_response", response);
        Set(panel, "_loadRequested", true);
        Set(panel, "_loading", false);
        Set(panel, "_detailLoading", false);
        Set(panel, "_status", $"Loaded {response.TotalCrafts:N0} recipe(s). Select a recipe for live sourcing details.");

        // Recipe sourcing details remain demand-loaded. Opening or refreshing Crafts must not
        // automatically issue /hermes/crafts/detail for the first or previously selected row.
        Set(panel, "_selectedCraft", null);
        Set(panel, "_detail", null);
        Set(panel, "_detailLoading", false);
        return true;
    }

    private bool ApplyStash(HermesStashSummaryResponse response)
    {
        var fingerprint = BuildStashSemanticFingerprint(response);
        if (string.Equals(_stashSemanticFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        _stashSemanticFingerprint = fingerprint;

        var panel = StashPanelField.GetValue(_window);
        if (panel is null)
        {
            return false;
        }

        IncrementInt(panel, "_requestVersion");
        Set(panel, "_summary", response);
        Set(panel, "_requested", true);
        Set(panel, "_loading", false);
        Set(
            panel,
            "_status",
            $"Snapshot complete: {response.IndependentItemCount:N0} independent items; "
            + $"{response.SafeToSellInstanceCount + response.SellSurplusInstanceCount:N0} sell recommendation(s); "
            + $"{response.RecoverableCells:N0} recoverable cell(s).");
        return true;
    }

    private bool ApplyLoadout(HermesLoadoutSummaryResponse response)
    {
        _cachedLoadout = response;
        var fingerprint = BuildLoadoutSemanticFingerprint(response);
        if (string.Equals(_loadoutSemanticFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        _loadoutSemanticFingerprint = fingerprint;

        var panel = LoadoutPanelField.GetValue(_window);
        if (panel is null)
        {
            return false;
        }

        IncrementInt(panel, "_requestVersion");
        Set(panel, "_summary", response);
        Set(panel, "_requested", true);
        Set(panel, "_loading", false);
        Set(panel, "_lastRequestSettings", Plugin.Settings.CreateLoadoutRequestSettings());
        Set(panel, "_nextAutomaticRefresh", float.PositiveInfinity);
        Set(
            panel,
            "_status",
            $"Loadout assessment: {response.Readiness} ({response.ReadinessScore}/100) • "
            + $"{response.CriticalCount} critical • {response.WarningCount} warning(s).");
        Invoke(panel, "InitializeWarningGroups", response.Warnings);
        return true;
    }

    private static void ApplyRaidPlanner(HermesLoadoutSummaryResponse response)
    {
        var panel = HermesWorkspaceSeparation.RaidPlanner;
        IncrementInt(panel, "_requestVersion");
        Set(panel, "_summary", response);
        Set(panel, "_requested", true);
        Set(panel, "_loading", false);
        Set(panel, "_nextAutomaticRefresh", float.PositiveInfinity);
        Set(
            panel,
            "_status",
            $"{response.RaidPlans.Count:N0} map(s)  •  "
            + $"{response.RaidPlans.Sum(plan => plan.ActiveQuestCount):N0} active quest stage(s)");
    }

    private void UpdateNoticeCandidates(bool manual)
    {
        if (NoticeServiceField.GetValue(_window) is not HermesAssistantNoticeService notices)
        {
            return;
        }

        var hideout = HideoutPanelField.GetValue(_window);
        var crafts = CraftPanelField.GetValue(_window);
        var stash = StashPanelField.GetValue(_window);
        var loadout = LoadoutPanelField.GetValue(_window);
        notices.UpdateFromWorkspaceData(
            _profileToken,
            loadout is null ? null : Get<HermesLoadoutSummaryResponse>(loadout, "_summary"),
            hideout is null ? null : Get<HermesHideoutSummaryResponse>(hideout, "_summary"),
            crafts is null ? null : Get<HermesCraftsResponse>(crafts, "_response"),
            stash is null ? null : Get<HermesStashSummaryResponse>(stash, "_summary"),
            manual);
    }

    private static string BuildHideoutSemanticFingerprint(HermesHideoutSummaryResponse response)
        => response.ContentRevision > 0
            ? $"server:{response.ContentRevision}"
            : $"legacy:{response.ReadyAreaCount}:{response.MaterialBlockedAreaCount}:{response.ProgressionBlockedAreaCount}:{response.Areas.Count}:{response.ActiveProductions.Count}";

    private static string BuildCraftsSemanticFingerprint(HermesCraftsResponse response)
        => response.ContentRevision > 0
            ? $"server:{response.ContentRevision}"
            : $"legacy:{response.TotalCrafts}:{response.Crafts.Count}";

    private static string BuildStashSemanticFingerprint(HermesStashSummaryResponse response)
        => response.ContentRevision > 0
            ? $"server:{response.ContentRevision}"
            : $"legacy:{response.TotalItemInstances}:{response.IndependentItemCount}:{response.PotentialBestSaleValue}";

    private static string BuildLoadoutSemanticFingerprint(HermesLoadoutSummaryResponse response)
        => response.ContentRevision > 0
            ? $"server:{response.ContentRevision}"
            : $"legacy:{response.ReadinessScore}:{response.WarningCount}:{response.CriticalCount}:{response.GeneratedUnixTime}";

    private void ClearSemanticFingerprints()
    {
        _hideoutSemanticFingerprint = string.Empty;
        _craftsSemanticFingerprint = string.Empty;
        _stashSemanticFingerprint = string.Empty;
        _loadoutSemanticFingerprint = string.Empty;
    }

    private void InvalidateProfileBoundRequests()
    {
        var hideout = HideoutPanelField.GetValue(_window);
        if (hideout is not null)
        {
            IncrementInt(hideout, "_refreshVersion");
            IncrementInt(hideout, "_detailVersion");
            Set(hideout, "_loading", false);
            Set(hideout, "_detailLoading", false);
            Set(hideout, "_loadRequested", true);
        }

        var crafts = CraftPanelField.GetValue(_window);
        if (crafts is not null)
        {
            IncrementInt(crafts, "_refreshVersion");
            IncrementInt(crafts, "_detailVersion");
            Set(crafts, "_loading", false);
            Set(crafts, "_detailLoading", false);
            Set(crafts, "_loadRequested", true);
        }

        var stash = StashPanelField.GetValue(_window);
        if (stash is not null)
        {
            IncrementInt(stash, "_requestVersion");
            Set(stash, "_loading", false);
            Set(stash, "_requested", true);
        }

        var loadout = LoadoutPanelField.GetValue(_window);
        if (loadout is not null)
        {
            IncrementInt(loadout, "_requestVersion");
            Set(loadout, "_loading", false);
            Set(loadout, "_requested", true);
            Set(loadout, "_nextAutomaticRefresh", float.PositiveInfinity);
        }

        var raidPlanner = HermesWorkspaceSeparation.RaidPlanner;
        IncrementInt(raidPlanner, "_requestVersion");
        Set(raidPlanner, "_loading", false);
        Set(raidPlanner, "_requested", true);
        Set(raidPlanner, "_nextAutomaticRefresh", float.PositiveInfinity);
    }

    private void ClearProfileBoundWorkspaceData()
    {
        lock (_workspaceRefreshSync)
        {
            _lastWorkspaceOpenRequestAt.Clear();
        }

        _cachedLoadout = null;
        lock (_preRaidPrefetchSync)
        {
            _preRaidPreparedLoadout = null;
            _preRaidPreparedUnixTime = 0;
            _preRaidPrefetchTask = null;
            _preRaidPrefetchGeneration++;
        }
        lock (_pendingSync)
        {
            _pendingSharedLoadout = null;
        }

        var hideout = HideoutPanelField.GetValue(_window);
        if (hideout is not null)
        {
            Set(hideout, "_summary", null);
            Set(hideout, "_selectedArea", null);
            Set(hideout, "_detail", null);
            Set(hideout, "_status", "Loading current hideout status for the active PMC profile...");
            Set(hideout, "_loadRequested", true);
        }

        var crafts = CraftPanelField.GetValue(_window);
        if (crafts is not null)
        {
            Set(crafts, "_response", null);
            Set(crafts, "_selectedCraft", null);
            Set(crafts, "_detail", null);
            Set(crafts, "_status", "Loading recipes for the active PMC profile...");
            Set(crafts, "_loadRequested", true);
        }

        var stash = StashPanelField.GetValue(_window);
        if (stash is not null)
        {
            Set(stash, "_summary", null);
            Set(stash, "_status", "Loading stash analysis for the active PMC profile...");
            Set(stash, "_requested", true);
        }

        var loadout = LoadoutPanelField.GetValue(_window);
        if (loadout is not null)
        {
            Set(loadout, "_summary", null);
            Set(loadout, "_status", "Loading loadout analysis for the active PMC profile...");
            Set(loadout, "_requested", true);
        }

        var raidPlanner = HermesWorkspaceSeparation.RaidPlanner;
        Set(raidPlanner, "_summary", null);
        Set(raidPlanner, "_status", "Loading raid plans for the active PMC profile...");
        Set(raidPlanner, "_requested", true);
    }

    private static async Task<T?> TryFetchAsync<T>(Func<Task<T>> fetch, string source)
        where T : class
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES {source} request failed after a server revision: {ex.Message}");
            return null;
        }
    }

    private static FieldInfo RequiredField(Type type, string name)
        => type.GetField(name, InstanceFlags)
           ?? throw new MissingFieldException(type.FullName, name);

    private static FieldInfo? FindField(object target, string name)
        => target.GetType().GetField(name, InstanceFlags);

    private static T? Get<T>(object target, string name)
        where T : class
        => FindField(target, name)?.GetValue(target) as T;

    private static void Set(object target, string name, object? value)
    {
        var field = FindField(target, name);
        if (field is null)
        {
            return;
        }

        if (value is null || field.FieldType.IsInstanceOfType(value) || field.FieldType.IsValueType)
        {
            field.SetValue(target, value);
        }
    }

    private static void IncrementInt(object target, string name)
    {
        var field = FindField(target, name);
        if (field?.FieldType == typeof(int))
        {
            field.SetValue(target, (int)(field.GetValue(target) ?? 0) + 1);
        }
    }

    private static void Invoke(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, InstanceFlags);
        method?.Invoke(target, arguments);
    }

    private static void InvokeTask(object target, string methodName, params object[] arguments)
    {
        try
        {
            _ = target.GetType().GetMethod(methodName, InstanceFlags)?.Invoke(target, arguments) as Task;
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES could not refresh changed {methodName} detail: {ex.Message}");
            }
        }
    }

    private sealed class DeltaBundle
    {
        public DeltaBundle(
            HermesChangesResponse changes,
            HermesHideoutSummaryResponse? hideout,
            HermesCraftsResponse? crafts,
            HermesStashSummaryResponse? stash,
            HermesLoadoutSummaryResponse? loadout,
            bool manual)
        {
            Changes = changes;
            Hideout = hideout;
            Crafts = crafts;
            Stash = stash;
            Loadout = loadout;
            Manual = manual;
        }

        public HermesChangesResponse Changes { get; }
        public HermesHideoutSummaryResponse? Hideout { get; }
        public HermesCraftsResponse? Crafts { get; }
        public HermesStashSummaryResponse? Stash { get; }
        public HermesLoadoutSummaryResponse? Loadout { get; }
        public bool Manual { get; }
    }
}

/// <summary>
/// Presentation-open accelerates the one startup full load. After that load finishes, reopening
/// HERMES refreshes only the currently visible workspace from the server's prepared summary.
/// </summary>
internal sealed class HermesSnapshotPresentationOpenPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("OnPresentationOpened", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "OnPresentationOpened");

    [PatchPrefix]
    private static bool Prefix()
    {
        HermesWorkspaceSnapshotCoordinator.Current?.OnPresentationOpened();
        return false;
    }
}
