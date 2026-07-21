namespace Hermes.Server.Models;

public sealed record HermesDomainRevisions(
    long Catalog,
    long Market,
    long Profile,
    long Stash,
    long Hideout,
    long Crafts,
    long Loadout,
    long RaidPlanner,
    long Assistant);

public sealed record HermesWorkspaceSnapshotResponse(
    bool Found,
    string? Message,
    string ContextToken,
    long Revision,
    HermesDomainRevisions Domains,
    HermesHideoutSummaryResponse Hideout,
    HermesCraftsResponse Crafts,
    HermesStashSummaryResponse Stash,
    HermesLoadoutSummaryResponse Loadout);

/// <summary>
/// Carries the Alerts fields alongside so a single call to /hermes/assistant/prepare/ is enough
/// for a manual refresh to display current alerts, instead of needing a follow-up call to
/// /hermes/assistant/alerts for the same freshly prepared feed.
/// </summary>
public sealed record HermesAssistantPrepareResponse(
    bool Prepared,
    string? Message,
    string ContextToken,
    long Revision,
    HermesDomainRevisions Domains,
    bool IsStale,
    int TotalAlerts,
    IReadOnlyList<HermesAssistantAlertSummary> Alerts);

public sealed record HermesChangesResponse(
    bool Found,
    string? Message,
    string ContextToken,
    long Revision,
    HermesDomainRevisions Domains,
    IReadOnlyList<string> Changed,
    string? Reason);

public sealed record HermesRecheckResponse(
    bool Accepted,
    string? Message,
    string ContextToken);
