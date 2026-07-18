using Hermes.Client.Models;

namespace Hermes.Client;

/// <summary>
/// Dedicated client for the small server-revision protocol. Existing HERMES request methods remain
/// untouched so this drop-in can be applied without replacing the current HermesApiClient.
/// </summary>
internal static class HermesRevisionApiClient
{
    public static Task<HermesAssistantPrepareResponse> PrepareAssistantFeedAsync(
        HermesStashRequestSettings stashSettings,
        HermesLoadoutRequestSettings loadoutSettings)
    {
        var route = "/hermes/assistant/prepare/"
                    + stashSettings.ToRouteSuffix().Trim('/')
                    + "/loadout/"
                    + loadoutSettings.ToRouteSuffix().Trim('/');
        return GetAsync<HermesAssistantPrepareResponse>(
            route,
            TimeSpan.FromSeconds(Math.Max(30, Plugin.Settings.GetLongRequestTimeoutSeconds())));
    }

    /// <summary>
    /// Opens one server-held change watch. The SPT server keeps the request open for 30 seconds
    /// while HERMES is visible or 60 seconds while it is closed. It returns early only when a
    /// real HERMES domain revision advances. An empty changed-domain list is only a quiet
    /// keep-alive response and never causes a workspace refresh.
    /// </summary>
    public static Task<HermesChangesResponse> WatchChangesAsync(long knownRevision, bool hermesOpen)
    {
        var holdSeconds = hermesOpen ? 30 : 60;
        var route = "/hermes/watch/"
                    + Math.Max(0L, knownRevision)
                    + "/"
                    + (hermesOpen ? "open" : "closed");
        var transportGraceSeconds = Math.Max(30, Plugin.Settings.GetRequestTimeoutSeconds());
        return GetAsync<HermesChangesResponse>(
            route,
            TimeSpan.FromSeconds(holdSeconds + transportGraceSeconds));
    }

    public static Task<HermesAssistantAlertsResponse> GetAssistantAlertsAsync()
    {
        return GetAsync<HermesAssistantAlertsResponse>(
            "/hermes/assistant/alerts",
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    public static Task<HermesRecheckResponse> RequestRecheckAsync()
    {
        return GetAsync<HermesRecheckResponse>(
            "/hermes/recheck",
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    public static Task<HermesRecheckResponse> InvalidatePreparedWorkspaceAsync(string workspace)
    {
        var route = (workspace ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "craft" or "crafts" => "/hermes/workspace/invalidate/crafts",
            "stash" => "/hermes/workspace/invalidate/stash",
            "loadout" or "raidplanner" or "raid planner" => "/hermes/workspace/invalidate/loadout",
            "assistant" or "chat" => "/hermes/workspace/invalidate/assistant",
            _ => "/hermes/workspace/invalidate/hideout"
        };
        return GetAsync<HermesRecheckResponse>(
            route,
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    // Used by the one startup/post-raid full load and by explicit top-button refreshes.
    // Opening a workspace in Alpha 14.0.7 reads its materialized server summary without a source scan.
    public static Task<HermesChangesResponse> GetChangesAsync(long knownRevision)
    {
        var route = "/hermes/changes/" + Math.Max(0L, knownRevision);
        return GetAsync<HermesChangesResponse>(
            route,
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    private static Task<T> GetAsync<T>(string route, TimeSpan timeout)
        where T : class
    {
        var isServerHeldWatch = route.StartsWith(
            "/hermes/watch/",
            StringComparison.OrdinalIgnoreCase);
        return HermesRequestBroker.GetDataAsync<T>(
            route,
            () => throw new HermesInvalidResponseException(
                route,
                $"The data payload could not be converted to {typeof(T).Name}."),
            timeout,
            isServerHeldWatch ? "server-held watch" : "revision request",
            suppressSlowWarning: isServerHeldWatch);
    }

}
