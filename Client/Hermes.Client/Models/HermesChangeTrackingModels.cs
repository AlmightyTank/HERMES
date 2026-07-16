namespace Hermes.Client.Models;

internal sealed class HermesDomainRevisions
{
    public long Catalog { get; set; }
    public long Market { get; set; }
    public long Profile { get; set; }
    public long Stash { get; set; }
    public long Hideout { get; set; }
    public long Crafts { get; set; }
    public long Loadout { get; set; }
    public long RaidPlanner { get; set; }
    public long Assistant { get; set; }
}

internal sealed class HermesWorkspaceSnapshotResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ContextToken { get; set; } = string.Empty;
    public long Revision { get; set; }
    public HermesDomainRevisions Domains { get; set; } = new();
    public HermesHideoutSummaryResponse Hideout { get; set; } = new();
    public HermesCraftsResponse Crafts { get; set; } = new();
    public HermesStashSummaryResponse Stash { get; set; } = new();
    public HermesLoadoutSummaryResponse Loadout { get; set; } = new();
}

internal sealed class HermesChangesResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ContextToken { get; set; } = string.Empty;
    public long Revision { get; set; }
    public HermesDomainRevisions Domains { get; set; } = new();
    public List<string> Changed { get; set; } = [];
    public string? Reason { get; set; }
}
