using Hermes.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Routers;

[Injectable]
public sealed class HermesStaticRouter(
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    HermesCatalogService catalogService)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction(
                "/hermes/status",
                (_, _, _, _) => ValueTask.FromResult<object>(
                    httpResponseUtil.GetBody(catalogService.GetStatus())))
        ])
{
}
