using Hermes.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Routers;

[Injectable]
public sealed class HermesStaticRouter(
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    HermesCatalogService catalogService,
    HermesCacheService cacheService,
    HermesStashAnalysisService stashAnalysisService,
    HermesLoadoutService loadoutService)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction(
                "/hermes/status",
                (_, _, _, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(catalogService.GetStatus()))),
            new RouteAction(
                "/hermes/cache/status",
                (_, _, _, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(cacheService.GetStatus(
                        stashAnalysisService.GetCacheDiagnostics(),
                        loadoutService.GetCacheDiagnostics())))),
            new RouteAction(
                "/hermes/cache/clear",
                (_, _, _, _) =>
                {
                    stashAnalysisService.Clear("Manual refresh from HERMES client");
                    loadoutService.Clear("Manual refresh from HERMES client");
                    var cleared = cacheService.Clear("Manual refresh from HERMES client");
                    var response = cleared with
                    {
                        Message = "HERMES market, stash-analysis, and loadout-analysis caches were cleared. New requests will read the current profile, quest, hideout, trader, and flea data.",
                        Status = cacheService.GetStatus(
                            stashAnalysisService.GetCacheDiagnostics(),
                            loadoutService.GetCacheDiagnostics())
                    };
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                })
        ])
{
}
