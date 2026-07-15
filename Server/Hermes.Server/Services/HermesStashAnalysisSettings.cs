namespace Hermes.Server.Services;

public sealed record HermesStashAnalysisSettings(
    bool IncludeActiveQuestReservations,
    bool IncludeFutureQuestReservations,
    bool IncludeNextHideoutReservations,
    bool IncludeFutureHideoutReservations,
    bool PreferFoundInRaidCopies,
    int DuplicateBaselineReserve,
    int WeaponDurabilityThresholdPercent,
    int ArmorDurabilityThresholdPercent,
    int LowResourceThresholdPercent,
    int KeyUsesWarningThreshold,
    long MinimumCleanupValue,
    long MinimumValuePerRecoveredCell,
    int MaximumRecommendations,
    bool IncludeProtectedCurrencies)
{
    public static HermesStashAnalysisSettings Default { get; } = new(
        true,
        true,
        true,
        true,
        true,
        1,
        70,
        50,
        20,
        1,
        0L,
        0L,
        300,
        true);

    public string CacheKey =>
        $"{(IncludeActiveQuestReservations ? 1 : 0)}-{(IncludeFutureQuestReservations ? 1 : 0)}-"
        + $"{(IncludeNextHideoutReservations ? 1 : 0)}-{(IncludeFutureHideoutReservations ? 1 : 0)}-"
        + $"{(PreferFoundInRaidCopies ? 1 : 0)}-{DuplicateBaselineReserve}-"
        + $"{WeaponDurabilityThresholdPercent}-{ArmorDurabilityThresholdPercent}-"
        + $"{LowResourceThresholdPercent}-{KeyUsesWarningThreshold}-"
        + $"{MinimumCleanupValue}-{MinimumValuePerRecoveredCell}-{MaximumRecommendations}-"
        + $"{(IncludeProtectedCurrencies ? 1 : 0)}";

    public static HermesStashAnalysisSettings Parse(string? rawTail)
    {
        var segments = (rawTail ?? string.Empty)
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 14)
        {
            return Default;
        }

        return new HermesStashAnalysisSettings(
            ParseBool(segments[0], Default.IncludeActiveQuestReservations),
            ParseBool(segments[1], Default.IncludeFutureQuestReservations),
            ParseBool(segments[2], Default.IncludeNextHideoutReservations),
            ParseBool(segments[3], Default.IncludeFutureHideoutReservations),
            ParseBool(segments[4], Default.PreferFoundInRaidCopies),
            ParseInt(segments[5], Default.DuplicateBaselineReserve, 0, 1000),
            ParseInt(segments[6], Default.WeaponDurabilityThresholdPercent, 1, 100),
            ParseInt(segments[7], Default.ArmorDurabilityThresholdPercent, 1, 100),
            ParseInt(segments[8], Default.LowResourceThresholdPercent, 0, 100),
            ParseInt(segments[9], Default.KeyUsesWarningThreshold, 0, 100),
            ParseLong(segments[10], Default.MinimumCleanupValue, 0L, 100_000_000L),
            ParseLong(segments[11], Default.MinimumValuePerRecoveredCell, 0L, 10_000_000L),
            ParseInt(segments[12], Default.MaximumRecommendations, 25, 1000),
            ParseBool(segments[13], Default.IncludeProtectedCurrencies));
    }

    private static int ParseInt(string value, int fallback, int minimum, int maximum)
    {
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static long ParseLong(string value, long fallback, long minimum, long maximum)
    {
        return long.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static bool ParseBool(string value, bool fallback)
    {
        if (value is "1" or "true" or "True")
        {
            return true;
        }

        if (value is "0" or "false" or "False")
        {
            return false;
        }

        return fallback;
    }
}
