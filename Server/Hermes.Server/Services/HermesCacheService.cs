using System.Collections.Concurrent;
using System.Threading;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Hermes.Server.Services;

public sealed record HermesAnalysisCacheDiagnostics(
    int EntryCount,
    long Hits,
    long Misses,
    long Writes,
    int TtlSeconds);

/// <summary>
/// Short-lived shared cache for market valuations and market summaries.
/// Profile inventory, quests, hideout state, and selected owned-copy selections are never cached here.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesCacheService
{
    public const int MarketCacheTtlSeconds = 20;

    public long Generation => Interlocked.Read(ref _generation);

    private readonly ConcurrentDictionary<string, CacheEntry<HermesMarketUnitValuation>> _marketUnitValues =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, CacheEntry<HermesMarketSummaryResponse>> _marketSummaries =
        new(StringComparer.OrdinalIgnoreCase);

    private long _hits;
    private long _misses;
    private long _writes;
    private long _generation;
    private long _lastInvalidatedUnixTime;
    private string _lastInvalidationReason = "Server startup";

    public bool TryGetMarketUnitValue(MongoId templateId, out HermesMarketUnitValuation valuation)
    {
        var key = templateId.ToString();
        return TryGet(_marketUnitValues, key, out valuation);
    }

    public void SetMarketUnitValue(
        MongoId templateId,
        HermesMarketUnitValuation valuation,
        long expectedGeneration)
    {
        Set(_marketUnitValues, templateId.ToString(), valuation, expectedGeneration);
    }

    public bool TryGetMarketSummary(
        string itemKey,
        MongoId sessionId,
        out HermesMarketSummaryResponse response)
    {
        var key = $"{sessionId}:{itemKey}";
        return TryGet(_marketSummaries, key, out response);
    }

    public void SetMarketSummary(
        string itemKey,
        MongoId sessionId,
        HermesMarketSummaryResponse response,
        long expectedGeneration)
    {
        var key = $"{sessionId}:{itemKey}";
        Set(_marketSummaries, key, response, expectedGeneration);
    }

    public HermesCacheClearResponse Clear(string? reason = null)
    {
        _marketUnitValues.Clear();
        _marketSummaries.Clear();
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _lastInvalidatedUnixTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _lastInvalidationReason = string.IsNullOrWhiteSpace(reason)
            ? "Manual HERMES refresh"
            : reason.Trim();

        var status = GetStatus();
        return new HermesCacheClearResponse(
            true,
            "HERMES market caches were cleared. New requests will read current trader and flea data.",
            status);
    }

    public HermesCacheStatusResponse GetStatus(
        HermesAnalysisCacheDiagnostics? stashAnalysis = null,
        HermesAnalysisCacheDiagnostics? loadoutAnalysis = null)
    {
        PurgeExpired(_marketUnitValues);
        PurgeExpired(_marketSummaries);

        var timestamps = _marketUnitValues.Values
            .Select(entry => entry.CreatedUnixTime)
            .Concat(_marketSummaries.Values.Select(entry => entry.CreatedUnixTime))
            .ToList();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        long? oldestAge = timestamps.Count > 0
            ? Math.Max(0L, now - timestamps.Min())
            : null;
        long? newestAge = timestamps.Count > 0
            ? Math.Max(0L, now - timestamps.Max())
            : null;

        return new HermesCacheStatusResponse(
            true,
            null,
            _marketUnitValues.Count,
            _marketSummaries.Count,
            stashAnalysis?.EntryCount ?? 0,
            loadoutAnalysis?.EntryCount ?? 0,
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _writes),
            stashAnalysis?.Hits ?? 0L,
            stashAnalysis?.Misses ?? 0L,
            stashAnalysis?.Writes ?? 0L,
            loadoutAnalysis?.Hits ?? 0L,
            loadoutAnalysis?.Misses ?? 0L,
            loadoutAnalysis?.Writes ?? 0L,
            Interlocked.Read(ref _generation),
            MarketCacheTtlSeconds,
            stashAnalysis?.TtlSeconds ?? 0,
            loadoutAnalysis?.TtlSeconds ?? 0,
            oldestAge,
            newestAge,
            _lastInvalidationReason,
            Interlocked.Read(ref _lastInvalidatedUnixTime) > 0
                ? Interlocked.Read(ref _lastInvalidatedUnixTime)
                : null);
    }

    private bool TryGet<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        out T value)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresUnixTime > now)
            {
                Interlocked.Increment(ref _hits);
                value = entry.Value;
                return true;
            }

            cache.TryRemove(key, out _);
        }

        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    private void Set<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        T value,
        long expectedGeneration)
    {
        if (expectedGeneration != Generation)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cache[key] = new CacheEntry<T>(
            value,
            now,
            now + MarketCacheTtlSeconds,
            expectedGeneration);
        Interlocked.Increment(ref _writes);
    }

    private static void PurgeExpired<T>(ConcurrentDictionary<string, CacheEntry<T>> cache)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var pair in cache)
        {
            if (pair.Value.ExpiresUnixTime <= now)
            {
                cache.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record CacheEntry<T>(
        T Value,
        long CreatedUnixTime,
        long ExpiresUnixTime,
        long Generation);
}
