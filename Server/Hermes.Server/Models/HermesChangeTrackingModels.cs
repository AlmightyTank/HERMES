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
