using Hermes.Client.Models;

namespace Hermes.Client;

internal static class HermesApiClient
{
    public const int DefaultRequestTimeoutSeconds = 12;
    public const int MinimumLongRequestTimeoutSeconds = 30;

    private static TimeSpan RequestTimeout => TimeSpan.FromSeconds(
        Plugin.Settings.GetRequestTimeoutSeconds());

    private static TimeSpan LongRequestTimeout => TimeSpan.FromSeconds(
        Plugin.Settings.GetLongRequestTimeoutSeconds());

    public static Task<HermesProfileContextResponse> GetProfileContextAsync()
    {
        return GetDataAsync(
            "/hermes/profile/context",
            () => new HermesProfileContextResponse
            {
                Found = false,
                Message = "HERMES could not resolve the active PMC profile context."
            });
    }

    public static Task<HermesSearchResponse> SearchAsync(string query, int maximumResults)
    {
        var boundedMaximum = Math.Clamp(maximumResults, 5, 50);
        var route = $"/hermes/search/{boundedMaximum}/" + Uri.EscapeDataString(query);
        return GetDataAsync(
            route,
            () => new HermesSearchResponse { Query = query });
    }

    public static Task<HermesItemSelectionResponse> GetPreviewItemSelectionAsync(string templateId)
    {
        var route = "/hermes/item/template/" + Uri.EscapeDataString(templateId);
        return GetDataAsync(
            route,
            () => new HermesItemSelectionResponse
            {
                Found = false,
                Message = "HERMES could not resolve the selected preview item."
            });
    }

    public static Task<HermesStashSummaryResponse> GetStashSummaryAsync(
        HermesStashRequestSettings settings)
    {
        var route = "/hermes/stash/summary/" + settings.ToRouteSuffix();
        return GetDataAsync(
            route,
            () => new HermesStashSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no stash summary."
            },
            LongRequestTimeout);
    }

    public static Task<HermesLoadoutSummaryResponse> GetLoadoutSummaryAsync(
        HermesLoadoutRequestSettings settings)
    {
        var route = "/hermes/loadout/summary/" + settings.ToRouteSuffix();
        return GetDataAsync(
            route,
            () => new HermesLoadoutSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no loadout summary."
            },
            LongRequestTimeout);
    }

    public static Task<HermesStashInstanceSelectionResponse> GetInventoryInstanceSelectionAsync(string profileItemId)
    {
        var route = "/hermes/inventory/instance/" + Uri.EscapeDataString(profileItemId);
        return GetDataAsync(
            route,
            () => new HermesStashInstanceSelectionResponse
            {
                Found = false,
                Message = "HERMES could not resolve the selected inventory item."
            });
    }

    public static Task<HermesStashInstanceSelectionResponse> GetStashInstanceSelectionAsync(string profileItemId)
    {
        return GetInventoryInstanceSelectionAsync(profileItemId);
    }

    public static Task<HermesStashInstancesResponse> GetStashInstancesAsync(string itemKey)
    {
        var route = "/hermes/stash/" + Uri.EscapeDataString(itemKey);
        return GetDataAsync(
            route,
            () => new HermesStashInstancesResponse
            {
                Found = false,
                Message = "HERMES returned no owned copies for this item."
            });
    }

    public static Task<HermesTraderSummaryResponse> GetTraderSummaryAsync(
        string itemKey,
        string? instanceKey = null)
    {
        var route = "/hermes/traders/" + Uri.EscapeDataString(itemKey);
        if (!string.IsNullOrWhiteSpace(instanceKey))
        {
            route += "/" + Uri.EscapeDataString(instanceKey);
        }

        return GetDataAsync(
            route,
            () => new HermesTraderSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no trader information for this item."
            });
    }

    public static Task<HermesMarketSummaryResponse> GetMarketSummaryAsync(
        string itemKey,
        string? instanceKey = null)
    {
        var route = "/hermes/market/" + Uri.EscapeDataString(itemKey);
        if (!string.IsNullOrWhiteSpace(instanceKey))
        {
            route += "/" + Uri.EscapeDataString(instanceKey);
        }

        return GetDataAsync(
            route,
            () => new HermesMarketSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no local flea information for this item."
            });
    }

    public static Task<HermesHideoutSummaryResponse> GetHideoutSummaryAsync()
    {
        return GetDataAsync(
            "/hermes/hideout/summary",
            () => new HermesHideoutSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no hideout summary."
            },
            LongRequestTimeout);
    }

    public static Task<HermesHideoutAreaDetailResponse> GetHideoutAreaAsync(string areaKey)
    {
        var route = "/hermes/hideout/area/" + Uri.EscapeDataString(areaKey);
        return GetDataAsync(
            route,
            () => new HermesHideoutAreaDetailResponse
            {
                Found = false,
                Message = "HERMES returned no details for this hideout area."
            });
    }

    public static Task<HermesItemHideoutUsageResponse> GetItemHideoutUsageAsync(string itemKey)
    {
        var route = "/hermes/hideout/item/" + Uri.EscapeDataString(itemKey);
        return GetDataAsync(
            route,
            () => new HermesItemHideoutUsageResponse
            {
                Found = false,
                Message = "HERMES returned no quest, hideout, or crafting usage for this item."
            });
    }

    public static Task<HermesCraftsResponse> GetCraftsAsync()
    {
        return GetDataAsync(
            "/hermes/crafts/summary",
            () => new HermesCraftsResponse
            {
                Found = false,
                Message = "HERMES returned no crafting summary."
            },
            LongRequestTimeout);
    }

    public static Task<HermesCraftDetailResponse> GetCraftDetailAsync(string craftKey)
    {
        var route = "/hermes/crafts/detail/" + Uri.EscapeDataString(craftKey);
        return GetDataAsync(
            route,
            () => new HermesCraftDetailResponse
            {
                Found = false,
                Message = "HERMES returned no details for this craft."
            });
    }

    public static Task<HermesCacheStatusResponse> GetCacheStatusAsync()
    {
        return GetDataAsync(
            "/hermes/cache/status",
            () => new HermesCacheStatusResponse
            {
                Found = false,
                Message = "HERMES cache status is unavailable."
            });
    }

    public static Task<HermesCacheClearResponse> ClearCachesAsync()
    {
        return GetDataAsync(
            "/hermes/cache/clear",
            () => new HermesCacheClearResponse
            {
                Cleared = false,
                Message = "HERMES could not clear its market, stash-analysis, and loadout-analysis caches."
            });
    }

    public static HermesRequestDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return HermesRequestDiagnostics.Snapshot();
    }

    public static string DescribeFailure(Exception exception, string operation)
    {
        return exception switch
        {
            HermesRequestTimeoutException timeout =>
                $"{operation} timed out after {timeout.Timeout.TotalSeconds:N0} seconds. Retry after the current server analysis finishes.",
            HermesTransportException =>
                $"{operation} could not reach the HERMES server route. Confirm SPT.Server is running and check both HERMES logs.",
            HermesInvalidResponseException invalid =>
                $"{operation} returned an invalid or incompatible response. {invalid.UserMessage}",
            _ =>
                $"{operation} failed. Check the HERMES client and SPT server logs."
        };
    }

    private static Task<T> GetDataAsync<T>(
        string route,
        Func<T> fallback,
        TimeSpan? requestTimeout = null)
        where T : class
    {
        return HermesRequestBroker.GetDataAsync(
            route,
            fallback,
            requestTimeout ?? RequestTimeout);
    }

}

internal sealed class HermesRequestTimeoutException : TimeoutException
{
    public HermesRequestTimeoutException(string route, TimeSpan timeout)
        : base($"HERMES route '{route}' exceeded the {timeout.TotalSeconds:N0}-second client timeout.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

internal sealed class HermesTransportException(string route, Exception innerException)
    : Exception($"HERMES route '{route}' failed during transport.", innerException);

internal sealed class HermesInvalidResponseException : Exception
{
    public HermesInvalidResponseException(string route, string message)
        : base($"HERMES route '{route}' returned an invalid response: {message}")
    {
        UserMessage = message;
    }

    public HermesInvalidResponseException(string route, string message, Exception innerException)
        : base($"HERMES route '{route}' returned an invalid response: {message}", innerException)
    {
        UserMessage = message;
    }

    public string UserMessage { get; }
}
