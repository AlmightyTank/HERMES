using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

internal sealed record HermesCatalogItem(
    string ItemKey,
    MongoId TemplateId,
    string Name,
    string ShortName,
    bool AcceptedBySupportedTrader);

[Injectable(InjectionType.Singleton)]
public sealed class HermesCatalogService(DatabaseService databaseService, ItemHelper itemHelper, JsonUtil jsonUtil)
{
    private static readonly HashSet<string> SupportedTraderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prapor",
        "therapist",
        "fence",
        "skier",
        "peacekeeper",
        "mechanic",
        "ragman",
        "jaeger",
        "lightkeeper",
        "ref"
    };
    private readonly object _sync = new();
    private IReadOnlyList<HermesCatalogItem>? _catalog;
    private Dictionary<string, HermesCatalogItem>? _byKey;
    private Dictionary<string, HermesCatalogItem>? _byTemplate;
    private string? _traderJson;
    private string? _hideoutJson;
    private string? _questJson;
    private Dictionary<string, long> _referencePrices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _questNames = new(StringComparer.OrdinalIgnoreCase);

    public HermesStatusResponse GetStatus()
    {
        EnsureIndex();

        return new HermesStatusResponse(
            "HERMES",
            HermesVersionInfo.DisplayVersion,
            "4.0.13",
            true,
            [
                "player-facing-item-search",
                "handbook-reference-price",
                "trader-buy-and-sell-intelligence",
                "selected-stash-instance-trader-valuation",
                "ask-hermes-inventory-context-action",
                "ask-hermes-trader-and-flea-preview-context-action",
                "player-loyalty-awareness",
                "cash-and-barter-offers",
                "stock-and-purchase-limits",
                "trader-restock-timers",
                "named-quest-unlock-requirements",
                "quest-only-item-filtering",
                "handbook-value-required-filtering",
                "local-flea-market-analysis",
                "normalized-flea-unit-prices",
                "condition-aware-flea-comparisons",
                "component-adjusted-flea-assembly-valuation",
                "converted-flea-barter-valuation",
                "market-priced-trader-barter-valuation",
                "flea-outlier-filtering",
                "estimated-flea-listing-fees",
                "trader-versus-flea-recommendations",
                "player-aware-quest-item-progress",
                "player-aware-hideout-item-progress",
                "hideout-area-planning",
                "hideout-upgrade-requirements",
                "active-production-monitoring",
                "generator-fuel-estimation",
                "craft-readiness-analysis",
                "available-crafts-filter",
                "inventory-aware-craft-costing",
                "cash-and-economic-craft-profitability",
                "trader-flea-and-owned-ingredient-sourcing",
                "item-quest-hideout-and-craft-usage",
                "named-quest-locked-craft-detection",
                "in-raid-route-key-requirements",
                "active-hideout-station-recipe-filtering",
                "shared-short-lived-market-cache",
                "manual-market-cache-invalidation",
                "generation-safe-cache-writes",
                "client-request-timeouts",
                "stale-response-protection",
                "parallel-item-detail-requests",
                "cached-stash-snapshot",
                "stash-item-and-cell-counts",
                "stash-handbook-valuation",
                "stash-trader-liquidation-valuation",
                "player-aware-stash-quest-reservations",
                "player-aware-stash-hideout-reservations",
                "fir-aware-stash-reservations",
                "partial-stack-keep-and-sell-quantities",
                "conservative-safe-to-sell-recommendations",
                "stash-flea-net-valuation",
                "stash-best-sale-destination",
                "stash-duplicate-review",
                "stash-damaged-and-depleted-report",
                "stash-space-recovery-estimation",
                "profile-aware-stash-cache-invalidation",
                "exact-equipped-loadout-analysis",
                "weapon-ammunition-readiness",
                "armor-insert-readiness",
                "medical-treatment-coverage",
                "pmc-vitals-readiness",
                "active-quest-equipment-checks",
                "active-quest-raid-item-checks",
                "active-quest-map-association",
                "quest-marker-and-plant-item-checks",
                "fir-aware-carried-quest-item-checks",
                "nested-quest-weapon-and-equipment-checks",
                "map-grouped-active-quest-raid-plans",
                "combined-raid-gear-checklists",
                "duplicate-quest-requirement-merging",
                "quest-objective-completion-summaries",
                "multi-restriction-raid-plan-warnings",
                "localized-quest-objective-text",
                "custom-quest-condition-locale-support",
                "exact-carried-item-valuation",
                "loadout-market-replacement-cost",
                "loadout-trader-liquidation-value",
                "loadout-insurance-state-detection",
                "protected-slot-risk-separation",
                "ask-hermes-equipped-item-context-action",
                "exact-pmc-inventory-instance-analysis",
                "meaningful-medical-template-classification",
                "attachment-insurance-classification",
                "insured-parent-assembly-inheritance",
                "configurable-loadout-readiness-thresholds",
                "bepinex-loadout-settings",
                "profile-aware-loadout-summary-cache",
                "automatic-loadout-refresh",
                "collapsible-loadout-warning-groups",
                "optional-value-and-insurance-analysis",
                "configurable-uninsured-warning-threshold",
                "hide-completed-raid-objectives",
                "shared-client-ui-components",
                "global-bepinex-interface-settings",
                "configurable-client-request-timeout",
                "remembered-main-tab",
                "remembered-window-position",
                "configurable-window-size-opacity-and-scale",
                "shared-row-render-limits",
                "shared-panel-status-and-empty-states",
                "configurable-item-search",
                "debounced-search-while-typing",
                "market-reliability-display",
                "market-display-controls",
                "hideout-area-filters",
                "hideout-requirement-display-controls",
                "craft-filter-and-sort-controls",
                "craft-profit-thresholds",
                "craft-duration-settings",
                "configurable-stash-reservation-sources",
                "configurable-stash-duplicate-baseline",
                "configurable-stash-condition-thresholds",
                "configurable-cleanup-value-thresholds",
                "settings-aware-stash-cache",
                "stash-category-destination-and-fir-filters",
                "stash-sort-controls",
                "stash-value-per-cell-analysis",
                "exact-stash-item-ask-hermes-actions",
                "stash-summary-clipboard-export",
                "configurable-spare-ammunition-threshold",
                "configurable-medical-treatment-requirements",
                "configurable-sustainment-requirements",
                "loadout-readiness-score-bar",
                "loadout-critical-and-advisory-filters",
                "loadout-condition-and-coverage-bars",
                "raid-planner-map-search-and-status-filters",
                "configurable-raid-plan-sorting",
                "grouped-raid-bring-equip-key-and-acquire-checklists",
                "configurable-route-key-and-handover-visibility",
                "raid-planner-loadout-context-warnings",
                "collapsible-map-plan-cards",
                "shared-in-flight-read-requests",
                "strict-server-response-envelope-validation",
                "client-request-health-diagnostics",
                "market-stash-and-loadout-cache-diagnostics",
                "clipboard-diagnostics-report",
                "configurable-slow-request-warning-threshold",
                "configurable-cache-status-refresh",
                "local-conversational-assistant",
                "assistant-conversation-history",
                "assistant-suggested-prompts",
                "assistant-selected-item-context",
                "assistant-cross-panel-navigation",
                "deterministic-profile-backed-answers",
                "craft-and-hideout-item-ask-hermes-actions",
                "assistant-local-intent-engine",
                "assistant-item-quest-craft-map-entity-recognition",
                "assistant-crafting-station-and-hideout-area-recognition",
                "assistant-entity-ambiguity-handling",
                "assistant-specific-quest-and-map-answers",
                "assistant-specific-craft-blocker-explanations",
                "assistant-specific-hideout-area-explanations",
                "configurable-assistant-entity-confidence",
                "assistant-cross-system-reasoning",
                "ranked-next-step-recommendations",
                "quest-density-and-readiness-raid-ranking",
                "map-specific-preparation-explanations",
                "craft-versus-raid-comparison",
                "configurable-economic-assistant-recommendations",
                "assistant-follow-up-conversation-context",
                "assistant-pronoun-and-shorthand-resolution",
                "assistant-recent-subject-memory",
                "assistant-ambiguity-ordinal-selection",
                "assistant-profile-context-invalidation",
                "configurable-assistant-context-memory",
                "assistant-proactive-notice-inbox",
                "assistant-loadout-readiness-notices",
                "assistant-insurance-risk-notices",
                "assistant-hideout-production-notices",
                "assistant-ready-upgrade-notices",
                "assistant-profitable-craft-notices",
                "assistant-optional-stash-opportunity-notices",
                "assistant-notice-change-deduplication",
                "assistant-notice-cooldowns",
                "assistant-persistent-eft-style-notice-cards",
                "assistant-clickable-notice-navigation",
                "native-character-screen-hermes-tab",
                "native-in-raid-inventory-hermes-tab",
                "shared-native-screen-assistant-state",
                "native-screen-item-and-notice-navigation",
                "assistant-raid-suppression"
            ]);
    }

    public HermesItemSelectionResponse GetTemplateSelection(string? rawTemplateId)
    {
        var templateIdText = Uri.UnescapeDataString(rawTemplateId ?? string.Empty).Trim();
        if (!MongoId.IsValidMongoId(templateIdText))
        {
            return new HermesItemSelectionResponse(
                false,
                "HERMES could not identify the selected preview item.",
                null);
        }

        var summary = GetSummary(new MongoId(templateIdText));
        return summary is null
            ? new HermesItemSelectionResponse(
                false,
                "HERMES does not list this item. Quest-only and handbook-less items are excluded.",
                null)
            : new HermesItemSelectionResponse(true, null, summary);
    }

    public HermesSearchResponse Search(string? rawQuery, int maximumResults = 30)
    {
        EnsureIndex();

        var query = NormalizePlayerQuery(Uri.UnescapeDataString(rawQuery ?? string.Empty));
        if (query.Length == 0)
        {
            return new HermesSearchResponse(query, 0, []);
        }

        maximumResults = Math.Clamp(maximumResults, 1, 50);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = _catalog!
            .Where(item => TokensMatch(item, tokens))
            .Select(item => new { Item = item, Score = MatchScore(item, query, tokens) })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HermesSearchResponse(
            query,
            matches.Count,
            matches.Take(maximumResults).Select(result => ToSummary(result.Item)).ToList());
    }

    internal HermesCatalogItem? ResolveItem(string? itemKey)
    {
        EnsureIndex();
        if (string.IsNullOrWhiteSpace(itemKey))
        {
            return null;
        }

        return _byKey!.GetValueOrDefault(itemKey.Trim());
    }

    internal HermesCatalogItem? ResolveTemplate(MongoId templateId)
    {
        EnsureIndex();
        return _byTemplate!.GetValueOrDefault(templateId.ToString());
    }

    internal string? GetItemKey(MongoId templateId)
    {
        return ResolveTemplate(templateId)?.ItemKey;
    }

    internal HermesItemSummary? GetSummary(MongoId templateId)
    {
        var item = ResolveTemplate(templateId);
        return item is null ? null : ToSummary(item);
    }

    internal string GetPlayerFacingName(MongoId templateId)
    {
        EnsureIndex();
        return _byTemplate!.TryGetValue(templateId.ToString(), out var item)
            ? item.Name
            : "Unknown item";
    }

    internal long? GetReferencePrice(MongoId templateId)
    {
        EnsureIndex();
        return _referencePrices.TryGetValue(templateId.ToString(), out var price) && price > 0
            ? price
            : null;
    }

    internal string? GetQuestName(MongoId questId)
    {
        EnsureIndex();
        return _questNames.TryGetValue(questId.ToString(), out var name)
            ? name
            : null;
    }

    private HermesItemSummary ToSummary(HermesCatalogItem item)
    {
        var templateId = item.TemplateId.ToString();
        return new HermesItemSummary(
            item.ItemKey,
            item.Name,
            item.ShortName,
            GetReferencePrice(item.TemplateId),
            item.AcceptedBySupportedTrader || CountOccurrences(_traderJson, templateId) > 0,
            CountOccurrences(_hideoutJson, templateId) > 0,
            CountOccurrences(_questJson, templateId) > 0);
    }

    private static bool TokensMatch(HermesCatalogItem item, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var searchable = item.Name + " " + item.ShortName;
        return tokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static int MatchScore(HermesCatalogItem item, string query, IReadOnlyList<string> tokens)
    {
        if (item.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (item.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 950;
        }

        if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 800;
        }

        if (item.ShortName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 750;
        }

        if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 650;
        }

        if (item.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 600;
        }

        return 400 + tokens.Count * 10;
    }

    private static string NormalizePlayerQuery(string query)
    {
        var normalized = query.Trim().Trim('?', '.', '!', ':', ';');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        string[] prefixes =
        [
            "who pays the most for ",
            "where can i buy ",
            "where do i buy ",
            "who buys ",
            "show trader offers for ",
            "show offers for ",
            "can i buy ",
            "find trader offers for ",
            "find offers for ",
            "search for ",
            "search "
        ];

        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..].Trim();
                break;
            }
        }

        return normalized.Trim('?', '.', '!', ':', ';');
    }

    private void EnsureIndex()
    {
        if (_catalog is not null)
        {
            return;
        }

        lock (_sync)
        {
            if (_catalog is not null)
            {
                return;
            }

            var itemsJson = SerializeDatabase(databaseService.GetItems());
            var handbookJson = SerializeDatabase(databaseService.GetHandbook());
            _traderJson = SerializeDatabase(databaseService.GetTraders());
            _hideoutJson = SerializeDatabase(databaseService.GetHideout());
            _questJson = SerializeDatabase(databaseService.GetQuests());
            var localesJson = SerializeDatabase(databaseService.GetLocales());

            var localeStrings = BuildLocaleLookup(localesJson);
            _referencePrices = BuildReferencePriceLookup(handbookJson);
            _questNames = BuildQuestNameLookup(_questJson, localeStrings);
            _catalog = BuildCatalog(itemsJson, localeStrings);
            _byKey = _catalog.ToDictionary(item => item.ItemKey, StringComparer.OrdinalIgnoreCase);
            _byTemplate = _catalog.ToDictionary(item => item.TemplateId.ToString(), StringComparer.OrdinalIgnoreCase);
        }
    }

    private string SerializeDatabase<T>(T value)
    {
        return jsonUtil.Serialize(value) ?? "{}";
    }

    private IReadOnlyList<HermesCatalogItem> BuildCatalog(
        string itemsJson,
        IReadOnlyDictionary<string, string> localeStrings)
    {
        var root = JsonNode.Parse(itemsJson) as JsonObject;
        if (root is null)
        {
            return [];
        }

        var entries = new List<HermesCatalogItem>(root.Count);
        var supportedTraderBases = databaseService.GetTraders()
            .Values
            .Select(trader => trader.Base)
            .Where(IsSupportedTrader)
            .ToArray();

        foreach (var pair in root)
        {
            if (pair.Value is not JsonObject itemObject)
            {
                continue;
            }

            // Dedicated quest-only objects must never be exposed through the
            // player-facing HERMES catalog. This intentionally does not hide
            // normal items merely because a quest requires them.
            if (IsQuestOnlyItem(itemObject))
            {
                continue;
            }

            var templateText = ReadString(itemObject, "_id", "Id", "id");
            if (string.IsNullOrWhiteSpace(templateText))
            {
                templateText = pair.Key;
            }

            if (!MongoId.IsValidMongoId(templateText))
            {
                continue;
            }

            var templateId = new MongoId(templateText);
            var hasHandbookValue = _referencePrices.TryGetValue(templateText, out var referencePrice)
                                   && referencePrice > 0;

            // HERMES only exposes items with a valid positive handbook value.
            // Trader acceptance does not make a handbook-less item searchable.
            if (!hasHandbookValue)
            {
                continue;
            }

            var acceptedBySupportedTrader = CanAnySupportedTraderBuy(templateId, supportedTraderBases);

            var name = GetLocale(localeStrings, templateText, " Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var shortName = GetLocale(localeStrings, templateText, " ShortName");
            if (string.IsNullOrWhiteSpace(shortName))
            {
                shortName = name;
            }

            entries.Add(new HermesCatalogItem(
                CreateOpaqueItemKey(templateText),
                templateId,
                name,
                shortName,
                acceptedBySupportedTrader));
        }

        return entries;
    }


    private bool CanAnySupportedTraderBuy(
        MongoId itemTemplate,
        IReadOnlyList<TraderBase> supportedTraders)
    {
        foreach (var traderBase in supportedTraders)
        {
            try
            {
                if (MatchesBuyRules(traderBase.ItemsBuy, itemTemplate)
                    && !MatchesBuyRules(traderBase.ItemsBuyProhibited, itemTemplate))
                {
                    return true;
                }
            }
            catch
            {
                // A malformed rule from one trader must not break the catalog.
            }
        }

        return false;
    }

    private bool MatchesBuyRules(ItemBuyData? rules, MongoId itemTemplate)
    {
        if (rules is null)
        {
            return false;
        }

        if (rules.IdList.Contains(itemTemplate))
        {
            return true;
        }

        return rules.Category.Count > 0
               && itemHelper.IsOfBaseclasses(itemTemplate, rules.Category);
    }

    private static bool IsSupportedTrader(TraderBase traderBase)
    {
        var nickname = traderBase.Nickname?.Trim();
        var name = traderBase.Name?.Trim();

        return (!string.IsNullOrWhiteSpace(nickname) && SupportedTraderNames.Contains(nickname))
               || (!string.IsNullOrWhiteSpace(name) && SupportedTraderNames.Contains(name));
    }

    private static string CreateOpaqueItemKey(string templateId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("HERMES:" + templateId));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildLocaleLookup(string localesJson)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var node = JsonNode.Parse(localesJson);
        var englishNode = FindObjectProperty(node, "en") ?? node;
        FlattenStrings(englishNode, lookup);
        return lookup;
    }

    private static JsonNode? FindObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && pair.Value is JsonObject)
                {
                    return pair.Value;
                }

                var nested = FindObjectProperty(pair.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nested = FindObjectProperty(child, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void FlattenStrings(JsonNode? node, IDictionary<string, string> output)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    output.TryAdd(pair.Key, text);
                }
                else
                {
                    FlattenStrings(pair.Value, output);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                FlattenStrings(child, output);
            }
        }
    }

    private static string? GetLocale(
        IReadOnlyDictionary<string, string> localeStrings,
        string templateId,
        string suffix)
    {
        return localeStrings.TryGetValue(templateId + suffix, out var value)
            ? value
            : null;
    }


    private static Dictionary<string, string> BuildQuestNameLookup(
        string questsJson,
        IReadOnlyDictionary<string, string> localeStrings)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = JsonNode.Parse(questsJson);
        CollectQuestNames(root, localeStrings, names, null);
        return names;
    }

    private static void CollectQuestNames(
        JsonNode? node,
        IReadOnlyDictionary<string, string> localeStrings,
        IDictionary<string, string> output,
        string? candidateId)
    {
        if (node is JsonObject obj)
        {
            var questId = ReadString(obj, "Id", "id", "_id") ?? candidateId;
            if (!string.IsNullOrWhiteSpace(questId) && MongoId.IsValidMongoId(questId))
            {
                var localizedName = GetFirstLocale(
                    localeStrings,
                    questId,
                    " name",
                    " Name",
                    " title",
                    " Title");

                if (string.IsNullOrWhiteSpace(localizedName))
                {
                    localizedName = ReadString(obj, "QuestName", "questName", "Name", "name");
                }

                if (!string.IsNullOrWhiteSpace(localizedName))
                {
                    output[questId] = localizedName.Trim();
                }
            }

            foreach (var pair in obj)
            {
                var childCandidate = MongoId.IsValidMongoId(pair.Key) ? pair.Key : null;
                CollectQuestNames(pair.Value, localeStrings, output, childCandidate);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectQuestNames(child, localeStrings, output, null);
            }
        }
    }

    private static string? GetFirstLocale(
        IReadOnlyDictionary<string, string> localeStrings,
        string id,
        params string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (localeStrings.TryGetValue(id + suffix, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static Dictionary<string, long> BuildReferencePriceLookup(string handbookJson)
    {
        var prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var root = JsonNode.Parse(handbookJson);
        CollectReferencePrices(root, prices);
        return prices;
    }

    private static void CollectReferencePrices(JsonNode? node, IDictionary<string, long> prices)
    {
        if (node is JsonObject obj)
        {
            var id = ReadString(obj, "Id", "id", "_id");
            var priceNode = ReadNode(obj, "Price", "price");

            if (!string.IsNullOrWhiteSpace(id) && priceNode is JsonValue priceValue)
            {
                if (priceValue.TryGetValue<long>(out var longPrice))
                {
                    prices[id] = longPrice;
                }
                else if (priceValue.TryGetValue<int>(out var intPrice))
                {
                    prices[id] = intPrice;
                }
                else if (priceValue.TryGetValue<double>(out var doublePrice))
                {
                    prices[id] = Convert.ToInt64(doublePrice);
                }
            }

            foreach (var child in obj)
            {
                CollectReferencePrices(child.Value, prices);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectReferencePrices(child, prices);
            }
        }
    }

    private static int CountOccurrences(string? source, string value)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var index = 0;

        while ((index = source.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }


    private static bool IsQuestOnlyItem(JsonObject itemObject)
    {
        var props = ReadNode(itemObject, "_props", "Props", "props") as JsonObject;
        var questItemNode = ReadNode(props, "QuestItem", "questItem");

        if (questItemNode is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue != 0;
        }

        if (value.TryGetValue<string>(out var textValue))
        {
            return bool.TryParse(textValue, out var parsedBool)
                ? parsedBool
                : int.TryParse(textValue, out var parsedInt) && parsedInt != 0;
        }

        return false;
    }

    private static JsonNode? ReadNode(JsonObject? obj, params string[] names)
    {
        if (obj is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var node))
            {
                return node;
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject? obj, params string[] names)
    {
        var node = ReadNode(obj, names);
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }
}
