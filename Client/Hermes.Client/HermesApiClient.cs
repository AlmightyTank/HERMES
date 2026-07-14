using System.Diagnostics;
using Hermes.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

internal static class HermesApiClient
{
    public const int RequestTimeoutSeconds = 12;
    public const int StashRequestTimeoutSeconds = 30;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);
    private static readonly TimeSpan StashRequestTimeout = TimeSpan.FromSeconds(StashRequestTimeoutSeconds);
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromSeconds(2.5d);

    public static Task<HermesSearchResponse> SearchAsync(string query)
    {
        var route = "/hermes/search/" + Uri.EscapeDataString(query);
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

    public static Task<HermesStashSummaryResponse> GetStashSummaryAsync()
    {
        return GetDataAsync(
            "/hermes/stash/summary",
            () => new HermesStashSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no stash summary."
            },
            StashRequestTimeout);
    }

    public static Task<HermesLoadoutSummaryResponse> GetLoadoutSummaryAsync()
    {
        return GetDataAsync(
            "/hermes/loadout/summary",
            () => new HermesLoadoutSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no loadout summary."
            },
            StashRequestTimeout);
    }

    public static Task<HermesStashInstanceSelectionResponse> GetInventoryInstanceSelectionAsync(string profileItemId)
    {
        var route = "/hermes/inventory/instance/" + Uri.EscapeDataString(profileItemId);
        return GetDataAsync(
            route,
            () => new HermesStashInstanceSelectionResponse
            {
                Found = false,
                Message = "HERMES could not resolve the selected PMC inventory item."
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
                Message = "HERMES returned no stash instances for this item."
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

    public static Task<HermesMarketSummaryResponse> GetMarketSummaryAsync(string itemKey)
    {
        var route = "/hermes/market/" + Uri.EscapeDataString(itemKey);
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
            });
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
            });
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
                Message = "HERMES could not clear its market caches."
            });
    }

    public static string DescribeFailure(Exception exception, string operation)
    {
        return exception switch
        {
            HermesRequestTimeoutException timeout =>
                $"{operation} timed out after {timeout.Timeout.TotalSeconds:N0} seconds. Retry; the server may still be finishing and warming the cache.",
            HermesInvalidResponseException =>
                $"{operation} returned an invalid response. Check the HERMES client and SPT server logs.",
            _ =>
                $"{operation} failed. Check the HERMES client and SPT server logs."
        };
    }

    private static async Task<T> GetDataAsync<T>(
        string route,
        Func<T> fallback,
        TimeSpan? requestTimeout = null)
    {
        var effectiveTimeout = requestTimeout ?? RequestTimeout;
        var stopwatch = Stopwatch.StartNew();
        Task<string> requestTask;
        try
        {
            requestTask = RequestHandler.GetJsonAsync(route);
        }
        catch (Exception ex)
        {
            throw new HermesTransportException(route, ex);
        }

        var timeoutTask = Task.Delay(effectiveTimeout);
        var completedTask = await Task.WhenAny(requestTask, timeoutTask);
        if (completedTask != requestTask)
        {
            ObserveLateCompletion(requestTask, route);
            Plugin.Log.LogWarning($"HERMES request timed out after {effectiveTimeout.TotalSeconds:N0}s: {route}");
            throw new HermesRequestTimeoutException(route, effectiveTimeout);
        }

        string json;
        try
        {
            json = await requestTask;
        }
        catch (Exception ex)
        {
            throw new HermesTransportException(route, ex);
        }
        finally
        {
            stopwatch.Stop();
        }

        if (stopwatch.Elapsed >= SlowRequestThreshold)
        {
            Plugin.Log.LogWarning($"Slow HERMES request ({stopwatch.Elapsed.TotalSeconds:N1}s): {route}");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new HermesInvalidResponseException(route, "The server returned an empty response.");
        }

        try
        {
            var token = JToken.Parse(json);
            var data = token["data"] ?? token["Data"] ?? token;
            return data.ToObject<T>() ?? fallback();
        }
        catch (JsonException ex)
        {
            throw new HermesInvalidResponseException(route, "The server response was not valid JSON.", ex);
        }
    }

    private static void ObserveLateCompletion(Task<string> requestTask, string route)
    {
        _ = requestTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception is not null)
                {
                    Plugin.Log.LogError($"Timed-out HERMES request later failed ({route}): {task.Exception}");
                }
                else
                {
                    Plugin.Log.LogDebug($"Timed-out HERMES request later completed: {route}");
                }
            },
            TaskScheduler.Default);
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
    }

    public HermesInvalidResponseException(string route, string message, Exception innerException)
        : base($"HERMES route '{route}' returned an invalid response: {message}", innerException)
    {
    }
}
