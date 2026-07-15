using System.Threading;

namespace Hermes.Client;

internal static class HermesRequestDiagnostics
{
    private static readonly object Sync = new();
    private static long _started;
    private static long _completed;
    private static long _failed;
    private static long _timedOut;
    private static long _invalidResponses;
    private static long _transportFailures;
    private static long _slowRequests;
    private static long _deduplicatedRequests;
    private static long _active;
    private static string _lastRoute = string.Empty;
    private static string _lastFailure = string.Empty;
    private static long _lastDurationMilliseconds;

    public static void RequestStarted(string route)
    {
        Interlocked.Increment(ref _started);
        Interlocked.Increment(ref _active);
        lock (Sync)
        {
            _lastRoute = route;
        }
    }

    public static void RequestCompleted(string route, long durationMilliseconds, bool slow)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Decrement(ref _active);
        if (slow)
        {
            Interlocked.Increment(ref _slowRequests);
        }

        lock (Sync)
        {
            _lastRoute = route;
            _lastDurationMilliseconds = Math.Max(0L, durationMilliseconds);
        }
    }

    public static void RequestFailed(string route, Exception exception, long durationMilliseconds)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Decrement(ref _active);
        switch (exception)
        {
            case HermesRequestTimeoutException:
                Interlocked.Increment(ref _timedOut);
                break;
            case HermesInvalidResponseException:
                Interlocked.Increment(ref _invalidResponses);
                break;
            case HermesTransportException:
                Interlocked.Increment(ref _transportFailures);
                break;
        }

        lock (Sync)
        {
            _lastRoute = route;
            _lastDurationMilliseconds = Math.Max(0L, durationMilliseconds);
            _lastFailure = exception.Message;
        }
    }

    public static void RequestDeduplicated()
    {
        Interlocked.Increment(ref _deduplicatedRequests);
    }

    public static HermesRequestDiagnosticsSnapshot Snapshot()
    {
        lock (Sync)
        {
            return new HermesRequestDiagnosticsSnapshot
            {
                Started = Interlocked.Read(ref _started),
                Completed = Interlocked.Read(ref _completed),
                Failed = Interlocked.Read(ref _failed),
                TimedOut = Interlocked.Read(ref _timedOut),
                InvalidResponses = Interlocked.Read(ref _invalidResponses),
                TransportFailures = Interlocked.Read(ref _transportFailures),
                SlowRequests = Interlocked.Read(ref _slowRequests),
                DeduplicatedRequests = Interlocked.Read(ref _deduplicatedRequests),
                Active = Math.Max(0L, Interlocked.Read(ref _active)),
                LastRoute = _lastRoute,
                LastFailure = _lastFailure,
                LastDurationMilliseconds = Interlocked.Read(ref _lastDurationMilliseconds)
            };
        }
    }
}

internal sealed class HermesRequestDiagnosticsSnapshot
{
    public long Started { get; set; }
    public long Completed { get; set; }
    public long Failed { get; set; }
    public long TimedOut { get; set; }
    public long InvalidResponses { get; set; }
    public long TransportFailures { get; set; }
    public long SlowRequests { get; set; }
    public long DeduplicatedRequests { get; set; }
    public long Active { get; set; }
    public string LastRoute { get; set; } = string.Empty;
    public string LastFailure { get; set; } = string.Empty;
    public long LastDurationMilliseconds { get; set; }
}
