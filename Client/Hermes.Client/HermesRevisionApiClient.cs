using Hermes.Client.Models;

namespace Hermes.Client;

/// <summary>
/// Client facade for the small server-revision, prepared-feed, and invalidation protocol.
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
    // Opening a workspace in the materialized workspace pipeline reads its materialized server summary without a source scan.
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
        return HermesRequestBroker.GetDataAsync<T>(
            route,
            () => throw new HermesInvalidResponseException(
                route,
                $"The data payload could not be converted to {typeof(T).Name}."),
            timeout,
            "revision request");
    }

}
