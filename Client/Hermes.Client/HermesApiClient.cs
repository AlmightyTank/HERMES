using System.Diagnostics;
using Hermes.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

internal static class HermesApiClient
{
    public const int DefaultRequestTimeoutSeconds = 12;
    public const int MinimumLongRequestTimeoutSeconds = 30;

    private static readonly object InFlightSync = new();
    private static readonly Dictionary<string, Task<string>> InFlightRequests =
        new(StringComparer.OrdinalIgnoreCase);

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

    private static async Task<T> GetDataAsync<T>(
        string route,
        Func<T> fallback,
        TimeSpan? requestTimeout = null)
    {
        _ = fallback;
        var effectiveTimeout = requestTimeout ?? RequestTimeout;
        var stopwatch = Stopwatch.StartNew();
        var completedSuccessfully = false;
        HermesRequestDiagnostics.RequestStarted(route);

        try
        {
            var requestTask = GetJsonRequestTask(route);
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(effectiveTimeout, timeoutCancellation.Token);
            var completedTask = await Task.WhenAny(requestTask, timeoutTask);
            if (completedTask != requestTask)
            {
                ObserveLateCompletion(requestTask, route);
                Plugin.Log.LogWarning($"HERMES request timed out after {effectiveTimeout.TotalSeconds:N0}s: {route}");
                throw new HermesRequestTimeoutException(route, effectiveTimeout);
            }

            timeoutCancellation.Cancel();
            string json;
            try
            {
                json = await requestTask;
            }
            catch (HermesTransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HermesTransportException(route, ex);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new HermesInvalidResponseException(route, "The server returned an empty response.");
            }

            T result;
            try
            {
                var token = JToken.Parse(json);
                ThrowIfServerReportedError(token, route);
                var data = token["data"] ?? token["Data"] ?? token;
                if (data.Type is JTokenType.Null or JTokenType.Undefined)
                {
                    throw new HermesInvalidResponseException(route, "The server response did not contain a data payload.");
                }

                result = data.ToObject<T>()
                         ?? throw new HermesInvalidResponseException(
                             route,
                             $"The data payload could not be converted to {typeof(T).Name}.");
            }
            catch (HermesInvalidResponseException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new HermesInvalidResponseException(route, "The server response was not valid JSON for this client model.", ex);
            }

            stopwatch.Stop();
            var slow = stopwatch.Elapsed >= TimeSpan.FromSeconds(Plugin.Settings.GetSlowRequestWarningSeconds());
            if (slow)
            {
                Plugin.Log.LogWarning($"Slow HERMES request ({stopwatch.Elapsed.TotalSeconds:N1}s): {route}");
            }
            else if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogDebug($"HERMES request completed in {stopwatch.Elapsed.TotalMilliseconds:N0}ms: {route}");
            }

            HermesRequestDiagnostics.RequestCompleted(route, stopwatch.ElapsedMilliseconds, slow);
            completedSuccessfully = true;
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            HermesRequestDiagnostics.RequestFailed(route, ex, stopwatch.ElapsedMilliseconds);
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogError($"HERMES request failed after {stopwatch.Elapsed.TotalMilliseconds:N0}ms ({route}): {ex}");
            }

            throw;
        }
        finally
        {
            if (!completedSuccessfully && stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
        }
    }

    private static Task<string> GetJsonRequestTask(string route)
    {
        if (!Plugin.Settings.ShareDuplicateRequests.Value)
        {
            return StartJsonRequest(route);
        }

        lock (InFlightSync)
        {
            if (InFlightRequests.TryGetValue(route, out var existing))
            {
                HermesRequestDiagnostics.RequestDeduplicated();
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"Sharing in-flight HERMES request: {route}");
                }

                return existing;
            }

            var created = StartJsonRequest(route);
            InFlightRequests[route] = created;
            _ = created.ContinueWith(
                _ =>
                {
                    lock (InFlightSync)
                    {
                        if (InFlightRequests.TryGetValue(route, out var current)
                            && ReferenceEquals(current, created))
                        {
                            InFlightRequests.Remove(route);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return created;
        }
    }

    private static Task<string> StartJsonRequest(string route)
    {
        try
        {
            return RequestHandler.GetJsonAsync(route);
        }
        catch (Exception ex)
        {
            return Task.FromException<string>(new HermesTransportException(route, ex));
        }
    }

    private static void ThrowIfServerReportedError(JToken token, string route)
    {
        var errorToken = token["err"] ?? token["Err"] ?? token["error"] ?? token["Error"];
        if (errorToken is null || errorToken.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return;
        }

        var hasError = errorToken.Type switch
        {
            JTokenType.Integer => errorToken.Value<long>() != 0L,
            JTokenType.Boolean => errorToken.Value<bool>(),
            JTokenType.String => !string.IsNullOrWhiteSpace(errorToken.Value<string>())
                                 && !string.Equals(errorToken.Value<string>(), "0", StringComparison.Ordinal),
            _ => false
        };
        if (!hasError)
        {
            return;
        }

        var message = (token["errmsg"] ?? token["message"] ?? token["Message"])?.ToString();
        throw new HermesInvalidResponseException(
            route,
            string.IsNullOrWhiteSpace(message)
                ? "The SPT server reported an error for this route."
                : $"The SPT server reported: {message}");
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
                else if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"Timed-out HERMES request later completed: {route}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
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
        UserMessage = message;
    }

    public HermesInvalidResponseException(string route, string message, Exception innerException)
        : base($"HERMES route '{route}' returned an invalid response: {message}", innerException)
    {
        UserMessage = message;
    }

    public string UserMessage { get; }
}
