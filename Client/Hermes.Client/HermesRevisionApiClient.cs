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

        if (stopwatch.Elapsed >= TimeSpan.FromSeconds(Plugin.Settings.GetSlowRequestWarningSeconds()))
        {
            Plugin.Log.LogWarning($"Slow HERMES revision request ({stopwatch.Elapsed.TotalSeconds:N1}s): {route}");
        }
        else if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogDebug($"HERMES revision request completed in {stopwatch.Elapsed.TotalMilliseconds:N0}ms: {route}");
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
