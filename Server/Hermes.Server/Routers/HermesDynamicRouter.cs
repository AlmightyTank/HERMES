using Hermes.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
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
    HermesHideoutService hideoutService)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction(
                "/hermes/search/",
                (url, _, _, _) =>
                {
                    var query = GetTail(url, "/hermes/search/");
                    var response = catalogService.Search(query);
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
                (_, _, sessionId, _) =>
                {
                    var response = stashAnalysisService.GetSummary(sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/loadout/summary",
                (_, _, sessionId, _) =>
                {
                    var response = loadoutService.GetSummary(sessionId);
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
                    var response = stashService.GetInventoryInstanceSelection(profileItemId, sessionId);
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
                    var itemKey = GetTail(url, "/hermes/market/");
                    var response = marketService.GetSummary(itemKey, sessionId);
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
