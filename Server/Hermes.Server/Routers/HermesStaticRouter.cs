using Hermes.Server.Models;
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
    HermesLoadoutService loadoutService,
    HermesChangeTrackingService changeTrackingService)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction(
                "/hermes/status",
                (_, _, _, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(catalogService.GetStatus()))),
            new RouteAction(
                "/hermes/assistant/alerts",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.GetAssistantAlerts(sessionId)))),
            new RouteAction(
                "/hermes/cache/status",
                (_, _, _, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(cacheService.GetStatus(
                        stashAnalysisService.GetCacheDiagnostics(),
                        loadoutService.GetCacheDiagnostics())))),
            new RouteAction(
                "/hermes/recheck",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.RequestRecheck(
                        sessionId,
                        "Manual HERMES workspace recheck")))),
            new RouteAction(
                "/hermes/workspace/invalidate/hideout",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.InvalidatePreparedWorkspace(
                        sessionId,
                        "Hideout",
                        "Manual Hideout workspace refresh")))),
            new RouteAction(
                "/hermes/workspace/invalidate/crafts",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.InvalidatePreparedWorkspace(
                        sessionId,
                        "Crafts",
                        "Manual Crafts workspace refresh")))),
            new RouteAction(
                "/hermes/workspace/invalidate/stash",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.InvalidatePreparedWorkspace(
                        sessionId,
                        "Stash",
                        "Manual Stash workspace refresh")))),
            new RouteAction(
                "/hermes/workspace/invalidate/loadout",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.InvalidatePreparedWorkspace(
                        sessionId,
                        "Loadout",
                        "Manual Loadout workspace refresh")))),
            new RouteAction(
                "/hermes/workspace/invalidate/assistant",
                (_, _, sessionId, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(changeTrackingService.InvalidatePreparedWorkspace(
                        sessionId,
                        "Assistant",
                        "Manual Assistant workspace refresh")))),
            new RouteAction(
                "/hermes/cache/clear",
                (_, _, _, _) =>
                {
                    stashAnalysisService.Clear("Manual refresh from HERMES client");
                    loadoutService.Clear("Manual refresh from HERMES client");
                    var cleared = cacheService.Clear("Manual refresh from HERMES client");
                    changeTrackingService.RequestRecheckAllSessions("Manual refresh from HERMES client");
                    var response = cleared with
                    {
                        Message = "HERMES caches were cleared and the server was asked to recheck current sources. Workspace revisions advance only when the underlying profile, hideout, quest, inventory, trader, or market data truly changed.",
                        Status = cacheService.GetStatus(
                            stashAnalysisService.GetCacheDiagnostics(),
                            loadoutService.GetCacheDiagnostics())
                    };
                    return ValueTask.FromResult<object>(httpResponseUtil.GetBody(response));
                })
        ])
{
}
