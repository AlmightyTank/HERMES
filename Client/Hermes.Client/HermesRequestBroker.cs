using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

/// <summary>
/// One transport, timeout, parsing, diagnostics, and in-flight deduplication pipeline for every
/// HERMES client feature. The route is the deduplication key, so Assistant, workspace, revision,
/// and detail callers always join the same server request instead of calculating it twice.
/// </summary>
internal static class HermesRequestBroker
{
    private static readonly object InFlightSync = new();
    private static readonly Dictionary<string, Task<string>> InFlightRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RecentResponse> RecentResponses =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RecentResponseReuseWindow = TimeSpan.FromSeconds(1);
    private const int MaximumRecentResponses = 96;

    internal static async Task<T> GetDataAsync<T>(
        string route,
        Func<T> fallback,
        TimeSpan timeout,
        string requestKind = "request",
        bool suppressSlowWarning = false)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        var completedSuccessfully = false;
        HermesRequestDiagnostics.RequestStarted(route);

        try
        {
            var requestTask = GetJsonRequestTask(route);
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
            var completed = await Task.WhenAny(requestTask, timeoutTask);
            if (completed != requestTask)
            {
                ObserveLateCompletion(requestTask, route, requestKind);
                Plugin.Log.LogWarning(
                    $"HERMES {requestKind} timed out after {timeout.TotalSeconds:N0}s: {route}");
                throw new HermesRequestTimeoutException(route, timeout);
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
                    throw new HermesInvalidResponseException(
                        route,
                        "The server response did not contain a data payload.");
                }

                result = data.ToObject<T>() ?? fallback();
            }
            catch (HermesInvalidResponseException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new HermesInvalidResponseException(
                    route,
                    "The server response was not valid JSON for this client model.",
                    ex);
            }

            stopwatch.Stop();
            var slow = !suppressSlowWarning
                       && stopwatch.Elapsed >= TimeSpan.FromSeconds(
                           Plugin.Settings.GetSlowRequestWarningSeconds());
            if (slow)
            {
                Plugin.Log.LogWarning(
                    $"Slow HERMES {requestKind} ({stopwatch.Elapsed.TotalSeconds:N1}s): {route}");
            }
            else if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogDebug(
                    $"HERMES {requestKind} completed in {stopwatch.Elapsed.TotalMilliseconds:N0}ms: {route}");
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
                Plugin.Log.LogError(
                    $"HERMES {requestKind} failed after {stopwatch.Elapsed.TotalMilliseconds:N0}ms ({route}): {ex}");
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
            if (TryGetRecentResponse(route, out var recentJson))
            {
                HermesRequestDiagnostics.RequestDeduplicated();
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"Reusing immediate HERMES response: {route}");
                }

                return Task.FromResult(recentJson);
            }

            if (InFlightRequests.TryGetValue(route, out var existing))
            {
                HermesRequestDiagnostics.RequestDeduplicated();
                if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"Sharing cross-feature HERMES request: {route}");
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
            if (InvalidatesRecentResponses(route))
            {
                lock (InFlightSync)
                {
                    RecentResponses.Clear();
                }
            }

            return StartJsonRequestCoreAsync(route);
        }
        catch (Exception ex)
        {
            return Task.FromException<string>(new HermesTransportException(route, ex));
        }
    }

    private static async Task<string> StartJsonRequestCoreAsync(string route)
    {
        var json = await RequestHandler.GetJsonAsync(route);
        if (!string.IsNullOrWhiteSpace(json) && IsRecentResponseCacheable(route))
        {
            lock (InFlightSync)
            {
                RecentResponses[route] = new RecentResponse(
                    json,
                    DateTimeOffset.UtcNow + RecentResponseReuseWindow);
                TrimRecentResponses();
            }
        }

        return json;
    }

    private static bool TryGetRecentResponse(string route, out string json)
    {
        json = string.Empty;
        if (!IsRecentResponseCacheable(route)
            || !RecentResponses.TryGetValue(route, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            RecentResponses.Remove(route);
            return false;
        }

        json = entry.Json;
        return true;
    }


    private static bool InvalidatesRecentResponses(string route)
    {
        return route.Equals("/hermes/recheck", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/workspace/invalidate/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/assistant/prepare/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentResponseCacheable(string route)
    {
        return route.Equals("/hermes/status", StringComparison.OrdinalIgnoreCase)
               || route.Equals("/hermes/cache/status", StringComparison.OrdinalIgnoreCase)
               || route.Equals("/hermes/quest-keys/status", StringComparison.OrdinalIgnoreCase)
               || route.Equals("/hermes/assistant/alerts", StringComparison.OrdinalIgnoreCase)
               || route.Equals("/hermes/hideout/summary", StringComparison.OrdinalIgnoreCase)
               || route.Equals("/hermes/crafts/summary", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/stash/summary/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/loadout/summary/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/search/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/trader/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/market/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/inventory/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/item/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/hideout/area/", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/hermes/crafts/detail/", StringComparison.OrdinalIgnoreCase);
    }

    private static void TrimRecentResponses()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in RecentResponses
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            RecentResponses.Remove(key);
        }

        if (RecentResponses.Count <= MaximumRecentResponses)
        {
            return;
        }

        foreach (var key in RecentResponses
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .Take(RecentResponses.Count - MaximumRecentResponses)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            RecentResponses.Remove(key);
        }
    }

    private sealed record RecentResponse(
        string Json,
        DateTimeOffset ExpiresAt);


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

    private static void ObserveLateCompletion(
        Task<string> requestTask,
        string route,
        string requestKind)
    {
        _ = requestTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception is not null)
                {
                    Plugin.Log.LogError(
                        $"Timed-out HERMES {requestKind} later failed ({route}): {task.Exception}");
                }
                else if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug(
                        $"Timed-out HERMES {requestKind} later completed: {route}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
