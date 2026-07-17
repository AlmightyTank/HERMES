using System.Diagnostics;
using Hermes.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

/// <summary>
/// Dedicated client for the small server-revision protocol. Existing HERMES request methods remain
/// untouched so this drop-in can be applied without replacing the current HermesApiClient.
/// </summary>
internal static class HermesRevisionApiClient
{
    public static Task<HermesWorkspaceSnapshotResponse> GetWorkspaceSnapshotAsync(
        HermesStashRequestSettings stashSettings,
        HermesLoadoutRequestSettings loadoutSettings)
    {
        var route = "/hermes/snapshot/"
                    + stashSettings.ToRouteSuffix().Trim('/')
                    + "/loadout/"
                    + loadoutSettings.ToRouteSuffix().Trim('/');
        return GetAsync<HermesWorkspaceSnapshotResponse>(
            route,
            TimeSpan.FromSeconds(Math.Max(120, Plugin.Settings.GetLongRequestTimeoutSeconds())));
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

    public static Task<HermesRecheckResponse> RequestRecheckAsync()
    {
        return GetAsync<HermesRecheckResponse>(
            "/hermes/recheck",
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    // Kept for diagnostics and backwards compatibility. Normal operation uses the
    // server-held watch route above instead of repeatedly polling this immediate route.
    public static Task<HermesChangesResponse> GetChangesAsync(long knownRevision)
    {
        var route = "/hermes/changes/" + Math.Max(0L, knownRevision);
        return GetAsync<HermesChangesResponse>(
            route,
            TimeSpan.FromSeconds(Plugin.Settings.GetRequestTimeoutSeconds()));
    }

    private static async Task<T> GetAsync<T>(string route, TimeSpan timeout)
        where T : class
    {
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

        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
        var completed = await Task.WhenAny(requestTask, timeoutTask);
        if (completed != requestTask)
        {
            ObserveLateCompletion(requestTask, route);
            throw new HermesRequestTimeoutException(route, timeout);
        }

        timeoutCancellation.Cancel();
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

        var isServerHeldWatch = route.StartsWith("/hermes/watch/", StringComparison.OrdinalIgnoreCase);
        if (!isServerHeldWatch
            && stopwatch.Elapsed >= TimeSpan.FromSeconds(Plugin.Settings.GetSlowRequestWarningSeconds()))
        {
            Plugin.Log.LogWarning($"Slow HERMES revision request ({stopwatch.Elapsed.TotalSeconds:N1}s): {route}");
        }
        else if (Plugin.Settings.DetailedLogging.Value)
        {
            var kind = isServerHeldWatch ? "server-held watch" : "revision request";
            Plugin.Log.LogDebug(
                $"HERMES {kind} completed in {stopwatch.Elapsed.TotalMilliseconds:N0}ms: {route}");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new HermesInvalidResponseException(route, "The server returned an empty response.");
        }

        try
        {
            var token = JToken.Parse(json);
            ThrowIfServerReportedError(token, route);
            var data = token["data"] ?? token["Data"] ?? token;
            return data.ToObject<T>()
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
            throw new HermesInvalidResponseException(route, "The server response was not valid JSON.", ex);
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
                    Plugin.Log.LogError($"Timed-out HERMES revision request later failed ({route}): {task.Exception}");
                }
                else if (Plugin.Settings.DetailedLogging.Value)
                {
                    Plugin.Log.LogDebug($"Timed-out HERMES revision request later completed: {route}");
                }
            },
            TaskScheduler.Default);
    }
}
