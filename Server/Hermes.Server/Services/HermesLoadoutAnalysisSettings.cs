namespace Hermes.Server.Services;

public sealed record HermesLoadoutAnalysisSettings(
    int MinimumWeaponDurabilityPercent,
    int MinimumArmorDurabilityPercent,
    int MinimumLoadedRounds,
    int MinimumSpareMagazines,
    int MinimumSpareRounds,
    int MinimumHealingResource,
    int HydrationWarningPercent,
    int EnergyWarningPercent,
    bool RequireHeavyBleedTreatment,
    bool RequireLightBleedTreatment,
    bool RequireFractureTreatment,
    bool RequirePainTreatment,
    bool RequireHydrationProvision,
    bool RequireEnergyProvision,
    bool IncludeValueAnalysis,
    bool EnableInsuranceWarnings,
    long HighValueUninsuredThreshold)
{
    public static HermesLoadoutAnalysisSettings Default { get; } = new(
        70,
        50,
        1,
        1,
        30,
        100,
        50,
        50,
        true,
        true,
        true,
        true,
        false,
        false,
        true,
        true,
        100_000L);

    public string CacheKey =>
        $"{MinimumWeaponDurabilityPercent}-{MinimumArmorDurabilityPercent}-{MinimumLoadedRounds}-"
        + $"{MinimumSpareMagazines}-{MinimumSpareRounds}-{MinimumHealingResource}-"
        + $"{HydrationWarningPercent}-{EnergyWarningPercent}-"
        + $"{(RequireHeavyBleedTreatment ? 1 : 0)}-{(RequireLightBleedTreatment ? 1 : 0)}-"
        + $"{(RequireFractureTreatment ? 1 : 0)}-{(RequirePainTreatment ? 1 : 0)}-"
        + $"{(RequireHydrationProvision ? 1 : 0)}-{(RequireEnergyProvision ? 1 : 0)}-"
        + $"{(IncludeValueAnalysis ? 1 : 0)}-{(EnableInsuranceWarnings ? 1 : 0)}-"
        + $"{HighValueUninsuredThreshold}";

    public static HermesLoadoutAnalysisSettings Parse(string? rawTail)
    {
        var segments = (rawTail ?? string.Empty)
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 17)
        {
            return Default;
        }

        return new HermesLoadoutAnalysisSettings(
            ParseInt(segments[0], Default.MinimumWeaponDurabilityPercent, 1, 100),
            ParseInt(segments[1], Default.MinimumArmorDurabilityPercent, 1, 100),
            ParseInt(segments[2], Default.MinimumLoadedRounds, 0, 200),
            ParseInt(segments[3], Default.MinimumSpareMagazines, 0, 20),
            ParseInt(segments[4], Default.MinimumSpareRounds, 0, 1000),
            ParseInt(segments[5], Default.MinimumHealingResource, 0, 5000),
            ParseInt(segments[6], Default.HydrationWarningPercent, 0, 100),
            ParseInt(segments[7], Default.EnergyWarningPercent, 0, 100),
            ParseBool(segments[8], Default.RequireHeavyBleedTreatment),
            ParseBool(segments[9], Default.RequireLightBleedTreatment),
            ParseBool(segments[10], Default.RequireFractureTreatment),
            ParseBool(segments[11], Default.RequirePainTreatment),
            ParseBool(segments[12], Default.RequireHydrationProvision),
            ParseBool(segments[13], Default.RequireEnergyProvision),
            ParseBool(segments[14], Default.IncludeValueAnalysis),
            ParseBool(segments[15], Default.EnableInsuranceWarnings),
            ParseLong(segments[16], Default.HighValueUninsuredThreshold, 0L, 10_000_000L));
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
