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
    HermesTraderService traderService,
    HermesMarketService marketService)
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
                "/hermes/traders/",
                (url, _, sessionId, _) =>
                {
                    var itemKey = GetTail(url, "/hermes/traders/");
                    var response = traderService.GetSummary(itemKey, sessionId);
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                }),
            new RouteAction(
                "/hermes/market/",
                (url, _, sessionId, _) =>
                {
                    var itemKey = GetTail(url, "/hermes/market/");
                    var response = marketService.GetSummary(itemKey, sessionId);
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
