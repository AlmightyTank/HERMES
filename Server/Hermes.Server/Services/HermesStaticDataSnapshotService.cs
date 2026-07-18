using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

/// <summary>
/// Materializes the static SPT database sections used by HERMES once per server process.
/// Hideout definitions, quest definitions, locales, and trader display names do not change
/// during a normal SPT session, so repeatedly serializing them inside workspace calculations
/// only adds CPU time and large temporary allocations.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesStaticDataSnapshotService(
    DatabaseService databaseService,
    JsonUtil jsonUtil)
{
    private readonly object _sync = new();
    private string? _hideoutJson;
    private string? _questsJson;
    private string? _localesJson;
    private JsonNode? _hideoutRoot;
    private JsonNode? _questsRoot;
    private JsonNode? _localesRoot;
    private IReadOnlyDictionary<string, string>? _traderNames;

    public string GetHideoutJson()
    {
        if (_hideoutJson is not null)
        {
            return _hideoutJson;
        }

        lock (_sync)
        {
            return _hideoutJson ??= Serialize(databaseService.GetHideout());
        }
    }

    public string GetQuestsJson()
    {
        if (_questsJson is not null)
        {
            return _questsJson;
        }

        lock (_sync)
        {
            return _questsJson ??= Serialize(databaseService.GetQuests());
        }
    }

    public string GetLocalesJson()
    {
        if (_localesJson is not null)
        {
            return _localesJson;
        }

        lock (_sync)
        {
            return _localesJson ??= Serialize(databaseService.GetLocales());
        }
    }

    public JsonNode GetHideoutRoot()
    {
        if (_hideoutRoot is not null)
        {
            return _hideoutRoot;
        }

        lock (_sync)
        {
            return _hideoutRoot ??= Parse(GetHideoutJson());
        }
    }

    public JsonNode GetQuestsRoot()
    {
        if (_questsRoot is not null)
        {
            return _questsRoot;
        }

        lock (_sync)
        {
            return _questsRoot ??= Parse(GetQuestsJson());
        }
    }

    public JsonNode GetLocalesRoot()
    {
        if (_localesRoot is not null)
        {
            return _localesRoot;
        }

        lock (_sync)
        {
            return _localesRoot ??= Parse(GetLocalesJson());
        }
    }

    public IReadOnlyDictionary<string, string> GetTraderNames()
    {
        if (_traderNames is not null)
        {
            return _traderNames;
        }

        lock (_sync)
        {
            if (_traderNames is not null)
            {
                return _traderNames;
            }

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, trader) in databaseService.GetTraders())
            {
                var name = trader.Base.Nickname;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = trader.Base.Name;
                }

                names[id.ToString()] = string.IsNullOrWhiteSpace(name)
                    ? "Trader"
                    : name;
            }

            _traderNames = names;
            return _traderNames;
        }
    }

    private string Serialize<T>(T value)
    {
        try
        {
            return jsonUtil.Serialize(value) ?? "{}";
        }
        catch
        {
            return "{}";
        }
    }

    private static JsonNode Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch
        {
            // A malformed modded database section must not prevent the server from starting.
            // Consumers already treat an empty root as no matching definitions.
            return new JsonObject();
        }
    }
}
