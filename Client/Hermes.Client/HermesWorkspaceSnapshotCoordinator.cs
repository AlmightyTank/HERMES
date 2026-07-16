using System.Reflection;
using Hermes.Client.Models;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Loads one server-created workspace snapshot, then polls only a tiny domain-revision route.
/// Full workspace summaries are requested again only for domains the server marked as changed.
/// </summary>
internal sealed class HermesWorkspaceSnapshotCoordinator
{
    private const float InitialDelaySeconds = 5f;
    private const float ActiveRevisionCheckSeconds = 10f;
    private const float InactiveRevisionCheckSeconds = 30f;
    private const float RetrySeconds = 10f;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo HideoutPanelField = RequiredField(typeof(HermesWindow), "_hideoutPanel");
    private static readonly FieldInfo CraftPanelField = RequiredField(typeof(HermesWindow), "_craftPanel");
    private static readonly FieldInfo StashPanelField = RequiredField(typeof(HermesWindow), "_stashPanel");
    private static readonly FieldInfo LoadoutPanelField = RequiredField(typeof(HermesWindow), "_loadoutPanel");
    private static readonly FieldInfo RefreshStatusField = RequiredField(typeof(HermesWindow), "_refreshStatus");

    private readonly HermesWindow _window;
    private readonly object _pendingSync = new();
    private HermesWorkspaceSnapshotResponse? _pendingSnapshot;
    private DeltaBundle? _pendingDelta;
    private bool _loading;
    private bool _initialized;
    private float _nextCheckAt;
    private long _revision;
    private string? _profileToken;

    internal static HermesWorkspaceSnapshotCoordinator? Current { get; private set; }

    /// <summary>
    /// True only while the lightweight server revision request is in flight. Native UI activity
    /// indicators ignore this background work so HERMES does not flash a spinner every poll.
    /// </summary>
    internal static bool IsBackgroundCheckActive => Current?._loading == true && Current._initialized;

    private HermesWorkspaceSnapshotCoordinator(HermesWindow window)
    {
        _window = window;
        _nextCheckAt = Time.realtimeSinceStartup + InitialDelaySeconds;
    }

    internal static HermesWorkspaceSnapshotCoordinator Configure(HermesWindow window)
    {
        Current = new HermesWorkspaceSnapshotCoordinator(window);
        return Current;
    }

    internal void EnsureInitialLoad()
    {
        // Opening HERMES only accelerates the very first snapshot. Reopening the tab no longer
        // forces an extra revision request on top of the normal quiet polling cadence.
        if (!_initialized)
        {
            _nextCheckAt = Time.realtimeSinceStartup;
        }
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

        _ = CheckServerRevisionAsync();
    }

    private async Task FetchInitialSnapshotAsync()
    {
        try
        {
            var snapshot = await HermesRevisionApiClient.GetWorkspaceSnapshotAsync(
                Plugin.Settings.CreateStashRequestSettings(),
                Plugin.Settings.CreateLoadoutRequestSettings());
            lock (_pendingSync)
            {
                _pendingSnapshot = snapshot;
            }

            ScheduleNextCheck();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HERMES initial server snapshot failed: {ex.Message}");
            RefreshStatusField.SetValue(
                _window,
                HermesApiClient.DescribeFailure(ex, "Initial HERMES snapshot"));
            _nextCheckAt = Time.realtimeSinceStartup + RetrySeconds;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task CheckServerRevisionAsync()
    {
        try
        {
            var changes = await HermesRevisionApiClient.GetChangesAsync(_revision);
            if (!changes.Found)
            {
                throw new InvalidOperationException(changes.Message ?? "HERMES revision status is unavailable.");
            }

            if (!string.IsNullOrWhiteSpace(_profileToken)
                && !string.IsNullOrWhiteSpace(changes.ContextToken)
                && !string.Equals(_profileToken, changes.ContextToken, StringComparison.Ordinal))
            {
                _initialized = false;
                _revision = 0;
                _profileToken = changes.ContextToken;
                _nextCheckAt = Time.realtimeSinceStartup;
                return;
            }

            if (changes.Revision <= _revision || changes.Changed.Count == 0)
            {
                _revision = Math.Max(_revision, changes.Revision);
                ScheduleNextCheck();
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
                    () => HermesApiClient.GetLoadoutSummaryAsync(Plugin.Settings.CreateLoadoutRequestSettings()),
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

            ScheduleNextCheck();
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"HERMES server revision check failed: {ex.Message}");
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
        lock (_pendingSync)
        {
            snapshot = _pendingSnapshot;
            delta = _pendingDelta;
            _pendingSnapshot = null;
            _pendingDelta = null;
        }

        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }

        if (delta is not null)
        {
            ApplyDelta(delta);
        }
    }

    private void ApplySnapshot(HermesWorkspaceSnapshotResponse snapshot)
    {
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
        if (delta.Hideout is { Found: true })
        {
            ApplyHideout(delta.Hideout);
            changed.Add("Hideout");
        }

        if (delta.Crafts is { Found: true })
        {
            ApplyCrafts(delta.Crafts);
            changed.Add("Crafts");
        }

        if (delta.Stash is { Found: true })
        {
            ApplyStash(delta.Stash);
            changed.Add("Stash");
        }

        if (delta.Loadout is { Found: true })
        {
            ApplyLoadout(delta.Loadout);
            ApplyRaidPlanner(delta.Loadout);
            changed.Add("Loadout / Raid Planner");
        }

        _revision = Math.Max(_revision, delta.Changes.Revision);

        // Background server revisions are intentionally silent. They update only the affected
        // workspace models and never replace the normal workspace subtitle with diagnostic text.
        RefreshStatusField.SetValue(_window, string.Empty);
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

    private void ScheduleNextCheck()
    {
        _nextCheckAt = Time.realtimeSinceStartup
                       + (HermesNativeWorkspaceRuntime.Active
                           ? ActiveRevisionCheckSeconds
                           : InactiveRevisionCheckSeconds);
    }

    private void ApplyHideout(HermesHideoutSummaryResponse response)
    {
        var panel = HideoutPanelField.GetValue(_window);
        if (panel is null)
        {
            return;
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
    }

    private void ApplyCrafts(HermesCraftsResponse response)
    {
        var panel = CraftPanelField.GetValue(_window);
        if (panel is null)
        {
            return;
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
            return;
        }

        var selected = response.Crafts.FirstOrDefault(craft => craft.CraftKey == previous.CraftKey);
        if (selected is not null)
        {
            Set(panel, "_selectedCraft", selected);
            if (IsActiveTab("Crafts"))
            {
                InvokeTask(panel, "SelectCraftAsync", selected);
            }
        }
    }

    private void ApplyStash(HermesStashSummaryResponse response)
    {
        var panel = StashPanelField.GetValue(_window);
        if (panel is null)
        {
            return;
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
    }

    private void ApplyLoadout(HermesLoadoutSummaryResponse response)
    {
        var panel = LoadoutPanelField.GetValue(_window);
        if (panel is null)
        {
            return;
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

    private void SuppressLegacyAutomaticRefresh()
    {
        if (!_initialized)
        {
            return;
        }

        var loadout = LoadoutPanelField.GetValue(_window);
        if (loadout is not null)
        {
            Set(loadout, "_nextAutomaticRefresh", float.PositiveInfinity);
        }

        Set(HermesWorkspaceSeparation.RaidPlanner, "_nextAutomaticRefresh", float.PositiveInfinity);
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
