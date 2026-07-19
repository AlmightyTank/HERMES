using System.Security.Cryptography;
using System.Text;
using Hermes.Server.Models;
using Hermes.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Routers;

[Injectable]
public sealed class HermesDynamicRouter(
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    HermesCatalogService catalogService,
    HermesStashService stashService,
    HermesStashAnalysisService stashAnalysisService,
    HermesLoadoutService loadoutService,
    HermesTraderService traderService,
    HermesMarketService marketService,
    HermesHideoutService hideoutService,
    HermesChangeTrackingService changeTrackingService)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction(
                "/hermes/profile/context",
                (_, _, sessionId, _) =>
                {
                    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"HERMES:PROFILE:{sessionId}"));
                    var token = Convert.ToHexString(bytes).ToLowerInvariant();
                    var response = new HermesProfileContextResponse(true, null, token);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/snapshot/",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/snapshot/");
                    const string separator = "/loadout/";
                    var separatorIndex = tail.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                    var stashTail = separatorIndex >= 0 ? tail[..separatorIndex] : tail;
                    var loadoutTail = separatorIndex >= 0
                        ? tail[(separatorIndex + separator.Length)..]
                        : string.Empty;
                    var stashSettings = HermesStashAnalysisSettings.Parse('/' + stashTail.Trim('/'));
                    var loadoutSettings = HermesLoadoutAnalysisSettings.Parse('/' + loadoutTail.Trim('/'));
                    var response = changeTrackingService.GetSnapshot(sessionId, stashSettings, loadoutSettings);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/assistant/prepare/",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/assistant/prepare/");
                    const string separator = "/loadout/";
                    var separatorIndex = tail.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                    var stashTail = separatorIndex >= 0 ? tail[..separatorIndex] : tail;
                    var loadoutTail = separatorIndex >= 0
                        ? tail[(separatorIndex + separator.Length)..]
                        : string.Empty;
                    var stashSettings = HermesStashAnalysisSettings.Parse('/' + stashTail.Trim('/'));
                    var loadoutSettings = HermesLoadoutAnalysisSettings.Parse('/' + loadoutTail.Trim('/'));
                    var response = changeTrackingService.PrepareAssistantFeed(
                        sessionId,
                        stashSettings,
                        loadoutSettings);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/changes/",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/changes/");
                    _ = long.TryParse(tail.Trim('/'), out var knownRevision);
                    var response = changeTrackingService.GetChanges(sessionId, Math.Max(0L, knownRevision));
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/search/",
                (url, _, _, _) =>
                {
                    var tail = GetTail(url, "/hermes/search/");
                    var separator = tail.IndexOf('/');
                    var maximumResults = 30;
                    var query = tail;
                    if (separator > 0
                        && int.TryParse(tail[..separator], out var parsedMaximum))
                    {
                        maximumResults = Math.Clamp(parsedMaximum, 5, 50);
                        query = tail[(separator + 1)..];
                    }

                    var response = catalogService.Search(query, maximumResults);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/item/template/",
                (url, _, _, _) =>
                {
                    var templateId = GetTail(url, "/hermes/item/template/");
                    var response = catalogService.GetTemplateSelection(templateId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/stash/summary",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/stash/summary");
                    var settings = HermesStashAnalysisSettings.Parse(tail);
                    var response = stashAnalysisService.GetSummary(sessionId, settings);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/loadout/summary",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/loadout/summary");
                    var settings = HermesLoadoutAnalysisSettings.Parse(tail);
                    var response = loadoutService.GetSummary(sessionId, settings);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/inventory/instance/",
                (url, _, sessionId, _) =>
                {
                    var profileItemId = GetTail(url, "/hermes/inventory/instance/");
                    var response = stashService.GetInventoryInstanceSelection(profileItemId, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/stash/instance/",
                (url, _, sessionId, _) =>
                {
                    var profileItemId = GetTail(url, "/hermes/stash/instance/");
                    var response = stashService.GetInstanceSelection(profileItemId, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/stash/",
                (url, _, sessionId, _) =>
                {
                    var itemKey = GetTail(url, "/hermes/stash/");
                    var response = stashService.GetInstances(itemKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/traders/",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/traders/");
                    var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var itemKey = segments.ElementAtOrDefault(0);
                    var instanceKey = segments.ElementAtOrDefault(1);
                    var response = traderService.GetSummary(itemKey, instanceKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/market/",
                (url, _, sessionId, _) =>
                {
                    var tail = GetTail(url, "/hermes/market/");
                    var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var itemKey = segments.ElementAtOrDefault(0);
                    var instanceKey = segments.ElementAtOrDefault(1);
                    var response = marketService.GetSummary(itemKey, sessionId, instanceKey);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/hideout/summary",
                (_, _, sessionId, _) =>
                {
                    var response = hideoutService.GetSummary(sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/hideout/area/",
                (url, _, sessionId, _) =>
                {
                    var areaKey = GetTail(url, "/hermes/hideout/area/");
                    var response = hideoutService.GetAreaDetail(areaKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/hideout/item/",
                (url, _, sessionId, _) =>
                {
                    var itemKey = GetTail(url, "/hermes/hideout/item/");
                    var response = hideoutService.GetItemUsage(itemKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/crafts/summary",
                (_, _, sessionId, _) =>
                {
                    var response = hideoutService.GetCrafts(sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/crafts/detail/",
                (url, _, sessionId, _) =>
                {
                    var craftKey = GetTail(url, "/hermes/crafts/detail/");
                    var response = hideoutService.GetCraftDetail(craftKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                })
        ])
{
    private static string GetTail(string url, string prefix)
    {
        var index = url.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var tail = url[(index + prefix.Length)..];
        var queryIndex = tail.IndexOf('?');
        return queryIndex >= 0 ? tail[..queryIndex] : tail;
    }
}
