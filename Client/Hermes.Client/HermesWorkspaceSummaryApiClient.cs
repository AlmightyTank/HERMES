using Hermes.Client.Models;

namespace Hermes.Client;

/// <summary>
/// Long-budget workspace facade. All transport and cross-feature request sharing is owned by
/// HermesRequestBroker, so Assistant and workspace callers can never launch duplicate routes.
/// </summary>
internal static class HermesWorkspaceSummaryApiClient
{
    internal static Task<HermesHideoutSummaryResponse> GetHideoutSummaryAsync()
        => HermesRequestBroker.GetDataAsync(
            "/hermes/hideout/summary",
            () => new HermesHideoutSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no hideout summary."
            },
            TimeSpan.FromSeconds(Math.Max(30, Plugin.Settings.GetLongRequestTimeoutSeconds())),
            "workspace request");

    internal static Task<HermesCraftsResponse> GetCraftsAsync()
        => HermesRequestBroker.GetDataAsync(
            "/hermes/crafts/summary",
            () => new HermesCraftsResponse
            {
                Found = false,
                Message = "HERMES returned no crafting summary."
            },
            TimeSpan.FromSeconds(Math.Max(30, Plugin.Settings.GetLongRequestTimeoutSeconds())),
            "workspace request");
}
