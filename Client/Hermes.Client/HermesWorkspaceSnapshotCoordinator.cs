using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Hermes.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Loads one server-created workspace snapshot, then maintains one quiet server-held change watch.
/// The server holds that request for 30 seconds while HERMES is open or 60 seconds while closed,
/// and the client downloads full summaries only after a real domain revision is reported.
/// </summary>
internal sealed class HermesWorkspaceSnapshotCoordinator
{
    private const float InitialDelaySeconds = 0.5f;
    private const float WatchReconnectDelaySeconds = 0.2f;
    private const float RetrySeconds = 15f;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo HideoutPanelField = RequiredField(typeof(HermesWindow), "_hideoutPanel");
    private static readonly FieldInfo CraftPanelField = RequiredField(typeof(HermesWindow), "_craftPanel");
    private static readonly FieldInfo StashPanelField = RequiredField(typeof(HermesWindow), "_stashPanel");
    private static readonly FieldInfo LoadoutPanelField = RequiredField(typeof(HermesWindow), "_loadoutPanel");
    private static readonly FieldInfo NoticeServiceField = RequiredField(typeof(HermesWindow), "_noticeService");
    private static readonly FieldInfo RefreshStatusField = RequiredField(typeof(HermesWindow), "_refreshStatus");

    private readonly HermesWindow _window;
    private readonly object _pendingSync = new();
    private readonly object _sharedLoadoutSync = new();
    private HermesWorkspaceSnapshotResponse? _pendingSnapshot;
    private DeltaBundle? _pendingDelta;
    private HermesLoadoutSummaryResponse? _pendingSharedLoadout;
    private HermesLoadoutSummaryResponse? _cachedLoadout;
    private Task<HermesLoadoutSummaryResponse>? _sharedLoadoutTask;
    private bool _loading;
    private bool _initialized;
    private float _nextCheckAt;
    private long _revision;
    private string? _profileToken;
    private string _hideoutSemanticFingerprint = string.Empty;
    private string _craftsSemanticFingerprint = string.Empty;
    private string _stashSemanticFingerprint = string.Empty;
    private string _loadoutSemanticFingerprint = string.Empty;

    internal static HermesWorkspaceSnapshotCoordinator? Current { get; private set; }

    /// <summary>
    /// True only while the quiet server-held watch is in flight. Native UI activity indicators
    /// ignore this background work, so waiting for a real server change never flashes a spinner.
    /// </summary>
    internal static bool IsBackgroundCheckActive => Current?._loading == true && Current._initialized;

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

    internal void EnsureInitialLoad()
    {
        // Opening HERMES only accelerates the very first snapshot. Once initialized, one server-
        // held watch is already active and reopening the tab never forces a second request.
        if (!_initialized)
        {
            _nextCheckAt = Time.realtimeSinceStartup;
        }
    }

    internal void RequestImmediateRecheck()
    {
        _nextCheckAt = Time.realtimeSinceStartup;
    }

    internal void RefreshNoticesFromLoadedData(bool manual)
    {
        UpdateNoticeCandidates(manual);
    }

    internal void Tick()
    {
        ApplyPending();
        SuppressLegacyAutomaticRefresh();

        if (_loading || Time.realtimeSinceStartup < _nextCheckAt)
        {
            return;
        }

        _loading = true;
        if (!_initialized)
        {
            RefreshStatusField.SetValue(_window, "Loading initial HERMES server snapshot...");
            _ = FetchInitialSnapshotAsync();
            return;
        }

        _ = WatchServerChangesAsync();
    }

    private async Task FetchInitialSnapshotAsync()
    {
        try
        {
            // The old /hermes/snapshot route built Hideout, Crafts, Stash, and Loadout serially.
            // The same domain endpoints are safe to request together (normal HERMES refresh already
            // does this), so startup now waits for the slowest domain instead of the sum of all four.
            var changesTask = HermesRevisionApiClient.GetChangesAsync(0);
            var hideoutTask = HermesApiClient.GetHideoutSummaryAsync();
            var craftsTask = HermesApiClient.GetCraftsAsync();
            var stashTask = HermesApiClient.GetStashSummaryAsync(
                Plugin.Settings.CreateStashRequestSettings());
            var loadoutTask = GetSharedLoadoutAsync(forceRefresh: true);

            await Task.WhenAll(
                (Task)changesTask,
                hideoutTask,
                craftsTask,
                stashTask,
                loadoutTask);

            var changes = changesTask.Result;
            var snapshot = new HermesWorkspaceSnapshotResponse
            {
                Found = changes.Found,
                Message = changes.Message,
                ContextToken = changes.ContextToken,
                Revision = changes.Revision,
                Domains = changes.Domains,
                Hideout = hideoutTask.Result,
                Crafts = craftsTask.Result,
                Stash = stashTask.Result,
                Loadout = loadoutTask.Result
            };

            lock (_pendingSync)
            {
                _pendingSnapshot = snapshot;
            }

            ScheduleWatchReconnect();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES parallel initial workspace load failed: {ex.Message}");
            RefreshStatusField.SetValue(
                _window,
                HermesApiClient.DescribeFailure(ex, "Initial HERMES workspace load"));
            _nextCheckAt = Time.realtimeSinceStartup + RetrySeconds;
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

    private async Task WatchServerChangesAsync()
    {
        try
        {
            var hermesOpen = HermesNativeWorkspaceRuntime.Active;
            var changes = await HermesRevisionApiClient.WatchChangesAsync(_revision, hermesOpen);
            if (!changes.Found)
            {
                throw new InvalidOperationException(changes.Message ?? "HERMES revision status is unavailable.");
            }

            if (!string.IsNullOrWhiteSpace(_profileToken)
                && !string.IsNullOrWhiteSpace(changes.ContextToken)
                && !string.Equals(_profileToken, changes.ContextToken, StringComparison.Ordinal))
            {
                ResetForProfileChange(changes.ContextToken);
                return;
            }

            if (changes.Revision <= _revision || changes.Changed.Count == 0)
            {
                _revision = Math.Max(_revision, changes.Revision);
                RefreshStatusField.SetValue(_window, string.Empty);
                ScheduleWatchReconnect();
                return;
            }

            var domains = new HashSet<string>(changes.Changed, StringComparer.OrdinalIgnoreCase);
            var hideoutTask = domains.Contains("hideout")
                ? TryFetchAsync(HermesApiClient.GetHideoutSummaryAsync, "changed hideout")
                : Task.FromResult<HermesHideoutSummaryResponse?>(null);
            var craftsTask = domains.Contains("crafts")
                ? TryFetchAsync(HermesApiClient.GetCraftsAsync, "changed crafts")
                : Task.FromResult<HermesCraftsResponse?>(null);
            var stashTask = domains.Contains("stash")
                ? TryFetchAsync(
                    () => HermesApiClient.GetStashSummaryAsync(Plugin.Settings.CreateStashRequestSettings()),
                    "changed stash")
                : Task.FromResult<HermesStashSummaryResponse?>(null);
            var loadoutTask = domains.Contains("loadout") || domains.Contains("raidPlanner")
                ? TryFetchAsync(
                    () => GetSharedLoadoutAsync(forceRefresh: true),
                    "changed loadout and raid planner")
                : Task.FromResult<HermesLoadoutSummaryResponse?>(null);

            await Task.WhenAll((Task)hideoutTask, craftsTask, stashTask, loadoutTask);
            lock (_pendingSync)
            {
                _pendingDelta = new DeltaBundle(
                    changes,
                    hideoutTask.Result,
                    craftsTask.Result,
                    stashTask.Result,
                    loadoutTask.Result);
            }

            ScheduleWatchReconnect();
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES server-held update watch failed: {ex.Message}");
            }

            _nextCheckAt = Time.realtimeSinceStartup + RetrySeconds;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyPending()
    {
        HermesWorkspaceSnapshotResponse? snapshot;
        DeltaBundle? delta;
        HermesLoadoutSummaryResponse? sharedLoadout;
        lock (_pendingSync)
        {
            snapshot = _pendingSnapshot;
            delta = _pendingDelta;
            sharedLoadout = _pendingSharedLoadout;
            _pendingSnapshot = null;
            _pendingDelta = null;
            _pendingSharedLoadout = null;
        }

        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }

        if (delta is not null)
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
            ClearProfileBoundWorkspaceData();
        }

        if (!snapshot.Found)
        {
            _initialized = false;
            _nextCheckAt = Time.realtimeSinceStartup + RetrySeconds;
            RefreshStatusField.SetValue(
                _window,
                snapshot.Message ?? "Waiting for the active PMC profile before loading HERMES data...");
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

        _initialized = snapshot.Hideout is { Found: true }
                       && snapshot.Crafts is { Found: true }
                       && snapshot.Stash is { Found: true }
                       && snapshot.Loadout is { Found: true };

        if (!_initialized)
        {
            _nextCheckAt = Time.realtimeSinceStartup + RetrySeconds;
            RefreshStatusField.SetValue(
                _window,
                loaded.Count > 0
                    ? $"Initial data partially loaded: {string.Join(", ", loaded)}. Retrying unavailable workspaces..."
                    : "Waiting for the active PMC profile before loading workspace data...");
            return;
        }

        RefreshStatusField.SetValue(_window, string.Empty);
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
        if (changed.Count > 0)
        {
            UpdateNoticeCandidates(manual: false);
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

    private bool IsActiveTab(string expected)
    {
        var active = FindField(_window, "_activeTab")?.GetValue(_window)?.ToString() ?? string.Empty;
        return active.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleWatchReconnect()
    {
        // The previous request already waited on the server. Reconnect quickly so there is only
        // one continuous watch instead of a client timer repeatedly asking whether anything changed.
        _nextCheckAt = Time.realtimeSinceStartup + WatchReconnectDelaySeconds;
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

        var previous = Get<HermesHideoutAreaSummary>(panel, "_selectedArea");
        var selected = previous is null
            ? response.Areas.FirstOrDefault()
            : response.Areas.FirstOrDefault(area => area.AreaKey == previous.AreaKey)
              ?? response.Areas.FirstOrDefault();
        if (selected is not null)
        {
            Set(panel, "_selectedArea", selected);
            if (IsActiveTab("Hideout"))
            {
                InvokeTask(panel, "SelectAreaAsync", selected);
            }
        }

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

        var previous = Get<HermesCraftSummary>(panel, "_selectedCraft");
        if (previous is null)
        {
            Set(panel, "_detail", null);
            Set(panel, "_detailLoading", false);
            return true;
        }

        var selected = response.Crafts.FirstOrDefault(craft => craft.CraftKey == previous.CraftKey);
        if (selected is null)
        {
            // The previously selected recipe may belong to a different profile or may no longer
            // be visible for this profile. Never leave its detail model on screen.
            Set(panel, "_selectedCraft", null);
            Set(panel, "_detail", null);
            Set(panel, "_detailLoading", false);
            return true;
        }

        Set(panel, "_selectedCraft", selected);
        if (IsActiveTab("Crafts"))
        {
            InvokeTask(panel, "SelectCraftAsync", selected);
        }

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
        => BuildSemanticFingerprint(
            response,
            "SecondsUntilComplete",
            "SecondsRemaining",
            "EstimatedGeneratorRuntimeSeconds",
            "FuelResourceRemaining",
            "FuelCounter",
            "AirFilterCounter",
            "WaterFilterCounter");

    private static string BuildCraftsSemanticFingerprint(HermesCraftsResponse response)
        => BuildSemanticFingerprint(response);

    private static string BuildStashSemanticFingerprint(HermesStashSummaryResponse response)
        => BuildSemanticFingerprint(response, "GeneratedUnixTime", "CacheTtlSeconds");

    private static string BuildLoadoutSemanticFingerprint(HermesLoadoutSummaryResponse response)
        => BuildSemanticFingerprint(response, "GeneratedUnixTime");

    private static string BuildSemanticFingerprint(object response, params string[] ignoredProperties)
    {
        var ignored = ignoredProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var token = JToken.FromObject(response);
        RemoveIgnoredProperties(token, ignored);

        var json = token.ToString(Formatting.None);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void RemoveIgnoredProperties(JToken token, ISet<string> ignored)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToList())
            {
                if (ignored.Contains(property.Name))
                {
                    property.Remove();
                    continue;
                }

                RemoveIgnoredProperties(property.Value, ignored);
            }
            return;
        }

        if (token is JArray array)
        {
            foreach (var child in array)
            {
                RemoveIgnoredProperties(child, ignored);
            }
        }
    }

    private void ClearSemanticFingerprints()
    {
        _hideoutSemanticFingerprint = string.Empty;
        _craftsSemanticFingerprint = string.Empty;
        _stashSemanticFingerprint = string.Empty;
        _loadoutSemanticFingerprint = string.Empty;
    }

    private void SuppressLegacyAutomaticRefresh()
    {
        // The revision coordinator is the only owner of automatic profile-bound summary loads.
        // Setting these request flags from the first frame prevents the legacy panel Draw methods
        // from launching overlapping /hideout, /crafts, /stash and /loadout requests. An older
        // response can therefore never arrive after a new profile snapshot and overwrite it.
        var hideout = HideoutPanelField.GetValue(_window);
        if (hideout is not null)
        {
            Set(hideout, "_loadRequested", true);
        }

        var crafts = CraftPanelField.GetValue(_window);
        if (crafts is not null)
        {
            Set(crafts, "_loadRequested", true);
        }

        var stash = StashPanelField.GetValue(_window);
        if (stash is not null)
        {
            Set(stash, "_requested", true);
        }

        var loadout = LoadoutPanelField.GetValue(_window);
        if (loadout is not null)
        {
            Set(loadout, "_requested", true);
            Set(loadout, "_nextAutomaticRefresh", float.PositiveInfinity);
        }

        var raidPlanner = HermesWorkspaceSeparation.RaidPlanner;
        Set(raidPlanner, "_requested", true);
        Set(raidPlanner, "_nextAutomaticRefresh", float.PositiveInfinity);
    }

    private void ResetForProfileChange(string contextToken)
    {
        InvalidateProfileBoundRequests();
        ClearProfileBoundWorkspaceData();
        ClearSemanticFingerprints();
        _initialized = false;
        _revision = 0;
        _profileToken = contextToken;
        _nextCheckAt = Time.realtimeSinceStartup;
        RefreshStatusField.SetValue(_window, "Loading HERMES data for the active PMC profile...");
        UpdateNoticeCandidates(manual: false);

        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogInfo("HERMES detected an active PMC profile change and discarded the previous profile snapshot.");
        }
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
        _cachedLoadout = null;
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
            HermesLoadoutSummaryResponse? loadout)
        {
            Changes = changes;
            Hideout = hideout;
            Crafts = crafts;
            Stash = stash;
            Loadout = loadout;
        }

        public HermesChangesResponse Changes { get; }
        public HermesHideoutSummaryResponse? Hideout { get; }
        public HermesCraftsResponse? Crafts { get; }
        public HermesStashSummaryResponse? Stash { get; }
        public HermesLoadoutSummaryResponse? Loadout { get; }
    }
}

/// <summary>
/// Presentation-open now asks the revision coordinator for an immediate lightweight check rather
/// than forcing the active workspace to rebuild.
/// </summary>
internal sealed class HermesSnapshotPresentationOpenPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("OnPresentationOpened", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "OnPresentationOpened");

    [PatchPrefix]
    private static bool Prefix()
    {
        HermesWorkspaceSnapshotCoordinator.Current?.EnsureInitialLoad();
        return false;
    }
}
