using Hermes.Client.Models;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Hermes.Client;

internal static class HermesApiClient
{
    public static async Task<HermesSearchResponse> SearchAsync(string query)
    {
        var route = "/hermes/search/" + Uri.EscapeDataString(query);
        return await GetDataAsync(
            route,
            () => new HermesSearchResponse { Query = query });
    }

    public static async Task<HermesTraderSummaryResponse> GetTraderSummaryAsync(string itemKey)
    {
        var route = "/hermes/traders/" + Uri.EscapeDataString(itemKey);
        return await GetDataAsync(
            route,
            () => new HermesTraderSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no trader information for this item."
            });
    }

    public static async Task<HermesMarketSummaryResponse> GetMarketSummaryAsync(string itemKey)
    {
        var route = "/hermes/market/" + Uri.EscapeDataString(itemKey);
        return await GetDataAsync(
            route,
            () => new HermesMarketSummaryResponse
            {
                Found = false,
                Message = "HERMES returned no local flea information for this item."
            });
    }

    private static async Task<T> GetDataAsync<T>(string route, Func<T> fallback)
    {
        var json = await RequestHandler.GetJsonAsync(route);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback();
        }

        var token = JToken.Parse(json);
        var data = token["data"] ?? token["Data"] ?? token;
        return data.ToObject<T>() ?? fallback();
    }
}
