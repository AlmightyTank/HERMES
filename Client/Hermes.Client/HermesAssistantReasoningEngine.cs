using Hermes.Client.Models;

namespace Hermes.Client;

internal sealed class HermesAssistantRaidRanking
{
    public HermesAssistantRaidRanking(HermesRaidPlanSummary plan, int score, IReadOnlyList<string> reasons)
    {
        Plan = plan;
        Score = score;
        Reasons = reasons;
    }

    public HermesRaidPlanSummary Plan { get; }
    public int Score { get; }
    public IReadOnlyList<string> Reasons { get; }
}

internal sealed class HermesAssistantRecommendation
{
    public HermesAssistantRecommendation(
        int priority,
        string category,
        string title,
        string detail,
        string tabName)
    {
        Priority = priority;
        Category = category;
        Title = title;
        Detail = detail;
        TabName = tabName;
    }

    public int Priority { get; }
    public string Category { get; }
    public string Title { get; }
    public string Detail { get; }
    public string TabName { get; }
}

internal static class HermesAssistantReasoningEngine
{
    public static IReadOnlyList<HermesAssistantRaidRanking> RankRaids(
        HermesLoadoutSummaryResponse loadout,
        bool preferPrepared)
    {
        var rankings = new List<HermesAssistantRaidRanking>();
        foreach (var plan in loadout.RaidPlans.Where(candidate => candidate.ActiveQuestCount > 0))
        {
            var incompleteObjectives = Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount);
            var score = plan.ActiveQuestCount * 24
                        + incompleteObjectives * 8
                        + Math.Clamp(loadout.ReadinessScore / 5, 0, 20)
                        - plan.MissingRequirementCount * 24
                        - loadout.CriticalCount * 4;

            if (plan.Status.Equals("Prepared", StringComparison.OrdinalIgnoreCase))
            {
                score += preferPrepared ? 42 : 20;
            }
            else if (plan.MissingRequirementCount == 0)
            {
                score += preferPrepared ? 26 : 14;
            }

            var reasons = new List<string>
            {
                $"{plan.ActiveQuestCount:N0} active quest(s)",
                $"{incompleteObjectives:N0} incomplete objective(s)"
            };

            if (plan.MissingRequirementCount == 0)
            {
                reasons.Add("no missing pre-raid quest requirement");
            }
            else
            {
                reasons.Add($"{plan.MissingRequirementCount:N0} missing pre-raid requirement(s)");
            }

            var acquireInRaid = plan.CombinedRequirements.Count(requirement => requirement.AcquireInRaid && !requirement.IsSatisfied);
            if (acquireInRaid > 0)
            {
                reasons.Add($"{acquireInRaid:N0} item(s) acquired during raid");
            }

            rankings.Add(new HermesAssistantRaidRanking(plan, score, reasons));
        }

        return rankings
            .OrderByDescending(ranking => ranking.Score)
            .ThenBy(ranking => ranking.Plan.MissingRequirementCount)
            .ThenByDescending(ranking => ranking.Plan.ActiveQuestCount)
            .ThenByDescending(ranking => ranking.Plan.ObjectiveCount - ranking.Plan.CompletedObjectiveCount)
            .ThenBy(ranking => ranking.Plan.MapName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<HermesAssistantRecommendation> BuildRecommendations(
        HermesLoadoutSummaryResponse loadout,
        HermesStashSummaryResponse stash,
        HermesCraftsResponse crafts,
        HermesHideoutSummaryResponse hideout,
        bool preferPreparedRaids,
        bool includeEconomicRecommendations,
        int maximum)
    {
        var recommendations = new List<HermesAssistantRecommendation>();

        if (loadout.Found)
        {
            foreach (var warning in loadout.Warnings
                         .OrderBy(warning => SeverityRank(warning.Severity))
                         .Take(3))
            {
                var priority = warning.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
                    ? 110
                    : warning.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                        ? 96
                        : 78;
                recommendations.Add(new HermesAssistantRecommendation(
                    priority,
                    warning.Category,
                    "Fix loadout readiness",
                    warning.Message,
                    "Loadout"));
            }

            var bestRaid = RankRaids(loadout, preferPreparedRaids).FirstOrDefault();
            if (bestRaid is not null)
            {
                var plan = bestRaid.Plan;
                recommendations.Add(new HermesAssistantRecommendation(
                    100 + Math.Clamp(bestRaid.Score / 20, 0, 15),
                    "Raid Planner",
                    $"Run {plan.MapName}",
                    $"{plan.Status}: {string.Join("; ", bestRaid.Reasons)}.",
                    "Loadout/Raid Planner"));
            }
        }

        if (hideout.Found)
        {
            var completedProduction = hideout.ActiveProductions.FirstOrDefault(production => production.IsComplete);
            if (completedProduction is not null)
            {
                recommendations.Add(new HermesAssistantRecommendation(
                    104,
                    "Hideout",
                    "Collect completed production",
                    $"{completedProduction.OutputQuantity:N0} × {completedProduction.OutputName} is ready at {completedProduction.StationName}.",
                    "Hideout"));
            }

            var readyArea = hideout.Areas
                .Where(area => !area.IsConstructing
                               && area.TargetLevel.HasValue
                               && area.CurrentLevel < area.MaximumLevel
                               && area.MissingItemTypes == 0
                               && area.Status.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                .OrderBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (readyArea is not null)
            {
                recommendations.Add(new HermesAssistantRecommendation(
                    92,
                    "Hideout",
                    $"Upgrade {readyArea.Name}",
                    $"All item requirements appear met for level {readyArea.TargetLevel:N0}.",
                    "Hideout"));
            }
            else
            {
                var nearestArea = hideout.Areas
                    .Where(area => !area.IsConstructing
                                   && area.TargetLevel.HasValue
                                   && area.CurrentLevel < area.MaximumLevel
                                   && area.MissingItemTypes > 0)
                    .OrderBy(area => area.MissingItemTypes)
                    .ThenBy(area => area.EstimatedMissingHandbookCost)
                    .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (nearestArea is not null)
                {
                    recommendations.Add(new HermesAssistantRecommendation(
                        64,
                        "Hideout",
                        $"Prepare {nearestArea.Name} upgrade",
                        $"Missing {nearestArea.MissingItemTypes:N0} item type(s); handbook estimate ₽{nearestArea.EstimatedMissingHandbookCost:N0}.",
                        "Hideout"));
                }
            }
        }

        if (crafts.Found)
        {
            var collectCraft = crafts.Crafts.FirstOrDefault(craft => craft.IsComplete);
            if (collectCraft is not null)
            {
                recommendations.Add(new HermesAssistantRecommendation(
                    103,
                    "Crafts",
                    "Collect completed craft",
                    $"{collectCraft.OutputQuantity:N0} × {collectCraft.OutputName} is ready at {collectCraft.StationName}.",
                    "Crafts"));
            }

            var readyCrafts = crafts.Crafts.Where(craft => craft.CanStartNow && !craft.IsActive && !craft.IsComplete);
            var bestCraft = includeEconomicRecommendations
                ? readyCrafts
                    .Where(craft => craft.EstimatedEconomicProfit > 0)
                    .OrderByDescending(craft => craft.EstimatedEconomicProfitPerHour)
                    .ThenByDescending(craft => craft.EstimatedEconomicProfit)
                    .FirstOrDefault()
                : readyCrafts
                    .OrderByDescending(craft => craft.EstimatedOutputValue)
                    .ThenBy(craft => craft.DurationSeconds)
                    .FirstOrDefault();
            if (bestCraft is not null)
            {
                var economics = includeEconomicRecommendations
                    ? $"estimated economic profit ₽{bestCraft.EstimatedEconomicProfit:N0} (₽{bestCraft.EstimatedEconomicProfitPerHour:N0}/h)"
                    : $"estimated output value ₽{bestCraft.EstimatedOutputValue:N0}";
                recommendations.Add(new HermesAssistantRecommendation(
                    76,
                    "Crafts",
                    $"Start {bestCraft.OutputName}",
                    $"Ready now at {bestCraft.StationName} L{bestCraft.RequiredStationLevel}; {economics}.",
                    "Crafts"));
            }
        }

        if (stash.Found && includeEconomicRecommendations)
        {
            if (stash.PotentiallySellQuantity > 0d && stash.PotentialBestSaleValue > 0L)
            {
                recommendations.Add(new HermesAssistantRecommendation(
                    58,
                    "Stash",
                    "Review safe-to-sell surplus",
                    $"Up to {FormatCount(stash.PotentiallySellQuantity)} item(s) are potentially sellable for about ₽{stash.PotentialBestSaleValue:N0} after configured reservations.",
                    "Stash"));
            }

            if (stash.CleanupCandidateInstanceCount > 0 && stash.RecoverableCells > 0)
            {
                recommendations.Add(new HermesAssistantRecommendation(
                    52,
                    "Stash",
                    "Recover stash space",
                    $"{stash.CleanupCandidateInstanceCount:N0} cleanup candidate(s) could recover {stash.RecoverableCells:N0} cell(s), worth about ₽{stash.CleanupBestSaleValue:N0}.",
                    "Stash"));
            }
        }

        return recommendations
            .GroupBy(recommendation => recommendation.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(recommendation => recommendation.Priority).First())
            .OrderByDescending(recommendation => recommendation.Priority)
            .ThenBy(recommendation => recommendation.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recommendation => recommendation.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maximum, 3, 10))
            .ToList();
    }

    private static int SeverityRank(string severity)
    {
        return severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            ? 0
            : severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;
    }

    private static string FormatCount(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }
}
