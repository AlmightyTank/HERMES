using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Formatting And Diagnostics

    private string FormatDiagnosticsStatus()
    {
        var requests = HermesApiClient.GetDiagnosticsSnapshot();
        if (_cacheStatus is null || !_cacheStatus.Found)
        {
            return $"Caches unavailable • Requests: {requests.Active:N0} active, {requests.Completed:N0} completed, {requests.Failed:N0} failed";
        }

        var marketEntries = _cacheStatus.MarketUnitValueEntryCount + _cacheStatus.MarketSummaryEntryCount;
        return $"Cache M/S/L: {marketEntries:N0}/{_cacheStatus.StashAnalysisEntryCount:N0}/{_cacheStatus.LoadoutAnalysisEntryCount:N0}"
               + $" • Requests: {requests.Active:N0} active, {requests.Completed:N0} ok, {requests.Failed:N0} failed, {requests.DeduplicatedRequests:N0} shared"
               + $" • Alerts: {_noticeService.ActiveNoticeCount:N0}";
    }

    private string BuildDiagnosticsReport()
    {
        var requests = HermesApiClient.GetDiagnosticsSnapshot();
        var lines = new List<string>
        {
            $"HERMES {HermesVersionInfo.DisplayVersion} diagnostics",
            $"Active tab: {_activeTab}",
            $"Client requests: started={requests.Started}, completed={requests.Completed}, failed={requests.Failed}, active={requests.Active}",
            $"Failures: timeout={requests.TimedOut}, transport={requests.TransportFailures}, invalid-response={requests.InvalidResponses}",
            $"Performance: slow={requests.SlowRequests}, shared-duplicates={requests.DeduplicatedRequests}, last-duration-ms={requests.LastDurationMilliseconds}",
            $"Last route: {requests.LastRoute}",
            $"Last failure: {(string.IsNullOrWhiteSpace(requests.LastFailure) ? "none" : requests.LastFailure)}",
            $"Assistant alerts: {_noticeService.GetDiagnosticsSummary()}"
        };

        if (_cacheStatus is { Found: true })
        {
            var marketEntries = _cacheStatus.MarketUnitValueEntryCount + _cacheStatus.MarketSummaryEntryCount;
            lines.Add($"Market cache: entries={marketEntries}, hits={_cacheStatus.CacheHits}, misses={_cacheStatus.CacheMisses}, writes={_cacheStatus.CacheWrites}, ttl={_cacheStatus.TtlSeconds}s");
            lines.Add($"Stash cache: entries={_cacheStatus.StashAnalysisEntryCount}, hits={_cacheStatus.StashCacheHits}, misses={_cacheStatus.StashCacheMisses}, writes={_cacheStatus.StashCacheWrites}, ttl={_cacheStatus.StashTtlSeconds}s");
            lines.Add($"Loadout cache: entries={_cacheStatus.LoadoutAnalysisEntryCount}, hits={_cacheStatus.LoadoutCacheHits}, misses={_cacheStatus.LoadoutCacheMisses}, writes={_cacheStatus.LoadoutCacheWrites}, ttl={_cacheStatus.LoadoutTtlSeconds}s");
            lines.Add($"Last cache invalidation: {_cacheStatus.LastInvalidationReason}");
        }
        else
        {
            lines.Add($"Server cache status: {_cacheStatus?.Message ?? "unavailable"}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task LoadCacheStatusAsync()
    {
        try
        {
            _cacheStatus = await HermesApiClient.GetCacheStatusAsync();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
            _cacheStatus = new HermesCacheStatusResponse
            {
                Found = false,
                Message = HermesApiClient.DescribeFailure(ex, "Cache status")
            };
        }
        finally
        {
            _nextCacheStatusRefresh = Time.realtimeSinceStartup + Plugin.Settings.GetCacheStatusRefreshSeconds();
            _cacheStatusLoading = false;
        }
    }

    private static string FormatCount(double count)
    {
        return Math.Abs(count - Math.Round(count)) < 0.0001d
            ? Math.Round(count).ToString("N0")
            : count.ToString("0.##");
    }

    private static string FormatCurrency(long amount, string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }

    #endregion
}
