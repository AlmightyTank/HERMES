using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

internal static class HermesNativeWorkspaceRuntime
{
    private static int _clientRefreshRevision;

    internal static bool Active { get; set; }

    internal static int ClientRefreshRevision
        => Volatile.Read(ref _clientRefreshRevision);

    internal static void RequestClientRefresh()
        => Interlocked.Increment(ref _clientRefreshRevision);
}


internal sealed record HermesNativeCraftFocusSnapshot(
    int Revision,
    string DisplayName,
    IReadOnlyCollection<string> CraftKeys);

internal static class HermesNativeCraftFocus
{
    private static readonly object Sync = new();
    private static int _revision;
    private static string _displayName = string.Empty;
    private static HashSet<string> _craftKeys = new(StringComparer.OrdinalIgnoreCase);

    internal static void Set(string displayName, IEnumerable<string> craftKeys)
    {
        lock (Sync)
        {
            _displayName = displayName?.Trim() ?? string.Empty;
            _craftKeys = new HashSet<string>(
                craftKeys.Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            _revision++;
        }

        HermesNativeWorkspaceRuntime.RequestClientRefresh();
    }

    internal static HermesNativeCraftFocusSnapshot Read()
    {
        lock (Sync)
        {
            return new HermesNativeCraftFocusSnapshot(
                _revision,
                _displayName,
                new HashSet<string>(_craftKeys, StringComparer.OrdinalIgnoreCase));
        }
    }
}

internal static class HermesNativeSearchBridge
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo QueryField = typeof(HermesWindow).GetField("_query", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_query");
    private static readonly FieldInfo LastSubmittedQueryField = typeof(HermesWindow).GetField("_lastSubmittedQuery", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_lastSubmittedQuery");
    private static readonly FieldInfo DeferredSearchField = typeof(HermesWindow).GetField("_deferredSearchAt", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_deferredSearchAt");
    private static readonly FieldInfo SearchingField = typeof(HermesWindow).GetField("_searching", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_searching");
    private static readonly MethodInfo RunSearchMethod = typeof(HermesWindow).GetMethod("RunSearchAsync", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "RunSearchAsync");
    private static readonly MethodInfo ClearMethod = typeof(HermesWindow).GetMethod("Clear", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "Clear");

    internal static string Query(HermesWindow window)
        => QueryField.GetValue(window) as string ?? string.Empty;

    internal static bool IsSearching(HermesWindow window)
        => SearchingField.GetValue(window) is true;

    internal static bool CanSearch(HermesWindow window)
        => !IsSearching(window)
           && Query(window).Trim().Length >= Plugin.Settings.GetMinimumSearchCharacters();

    internal static void SetQuery(HermesWindow window, string value)
    {
        value ??= string.Empty;
        QueryField.SetValue(window, value);

        var trimmedLength = value.Trim().Length;
        var deferredAt = Plugin.Settings.SearchWhileTyping.Value
                         && trimmedLength >= Plugin.Settings.GetMinimumSearchCharacters()
            ? Time.realtimeSinceStartup + 0.35f
            : -1f;
        DeferredSearchField.SetValue(window, deferredAt);
    }

    internal static void Search(HermesWindow window)
    {
        if (!CanSearch(window))
        {
            return;
        }

        DeferredSearchField.SetValue(window, -1f);
        _ = RunSearchMethod.Invoke(window, null);
    }

    internal static void Clear(HermesWindow window)
    {
        ClearMethod.Invoke(window, null);
        QueryField.SetValue(window, string.Empty);
        LastSubmittedQueryField.SetValue(window, string.Empty);
        DeferredSearchField.SetValue(window, -1f);
    }
}

/// <summary>
/// The native Items & Market toolbar owns the visible search input, so the legacy IMGUI
/// search row is skipped only while the native HERMES workspace is active.
/// </summary>
internal sealed class HermesNativeItemSearchBarSuppressionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesWindow).GetMethod(
                   "DrawSearchBar",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawSearchBar");
    }

    [PatchPrefix]
    private static bool Prefix()
    {
        return !HermesNativeWorkspaceRuntime.Active;
    }
}
