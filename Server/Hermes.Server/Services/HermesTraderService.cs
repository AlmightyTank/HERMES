using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesTraderService(
    DatabaseService databaseService,
    HermesPreparedProfileSnapshotService preparedProfiles,
    TraderAssortHelper traderAssortHelper,
    HandbookHelper handbookHelper,
    ItemHelper itemHelper,
    HermesCatalogService catalogService,
    HermesStashService stashService,
    HermesMarketPriceService marketPriceService,
    JsonUtil jsonUtil)
{
    private static readonly HashSet<string> VanillaTraderNames = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly MongoId RoublesTpl = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId DollarsTpl = new("5696686a4bdc2da3298b456a");
    private static readonly MongoId EurosTpl = new("569668774bdc2da2298b4568");
    private static readonly MongoId GpCoinTpl = new("5d235b4d86f7742e017bc88a");

    public HermesTraderSummaryResponse GetSummary(
        string? itemKey,
        string? instanceKey,
        MongoId sessionId)
    {
        var item = catalogService.ResolveItem(itemKey);
        if (item is null)
        {
            return NotFound(itemKey, "The selected HERMES item is no longer available. Search for it again.");
        }

        var preparedProfile = preparedProfiles.Get(sessionId);
        if (preparedProfile is null)
        {
            return NotFound(item.ItemKey, "HERMES could not read the active PMC profile.");
        }

        var profileJson = preparedProfile.ProfileJson;
        var referencePrice = catalogService.GetReferencePrice(item.TemplateId);
        var selectedInstance = stashService.ResolveSelectedInstance(item.ItemKey, instanceKey, sessionId);
        IReadOnlyList<HermesTraderSaleComponent> saleComponents = selectedInstance?.Components
            ?? (referencePrice is > 0
                ? [new HermesTraderSaleComponent(
                    item.TemplateId,
                    referencePrice.Value,
                    HermesSaleComponentKind.Root)]
                : []);
        var sellOffers = BuildSellOffers(item.TemplateId, saleComponents, profileJson);
        var purchaseOffers = BuildPurchaseOffers(item.TemplateId, sessionId, profileJson);

        var best = sellOffers.FirstOrDefault();
        if (best is not null)
        {
            sellOffers = sellOffers
                .Select((offer, index) => offer with { IsBest = index == 0 })
                .ToList();
            best = sellOffers[0];
        }

        var instanceSummary = selectedInstance?.Summary;
        var salePriceBasis = instanceSummary is null
            ? "This estimate assumes a full-condition base item. Select one of your owned copies above to include its current condition and installed child-item value."
            : $"Using {instanceSummary.Label} from {instanceSummary.Location}: root RUB {instanceSummary.RootConditionAdjustedReferenceValue:N0} plus child items RUB {instanceSummary.InstalledComponentReferenceValue:N0}. Each trader only pays for child items it accepts.";

        return new HermesTraderSummaryResponse(
            true,
            selectedInstance is null && !string.IsNullOrWhiteSpace(instanceKey)
                ? "The selected owned copy is no longer available. HERMES returned the base-item estimate instead."
                : null,
            item.ItemKey,
            item.Name,
            item.ShortName,
            referencePrice,
            item.AcceptedBySupportedTrader,
            instanceSummary is not null,
            instanceSummary?.InstanceKey,
            instanceSummary?.Label,
            instanceSummary?.ConditionPercent,
            instanceSummary?.Quantity ?? 1d,
            instanceSummary?.ChildItemCount ?? 0,
            instanceSummary?.WeaponAttachmentCount ?? 0,
            instanceSummary?.ArmorInsertCount ?? 0,
            instanceSummary?.RootConditionAdjustedReferenceValue,
            instanceSummary?.InstalledComponentReferenceValue,
            instanceSummary?.ConditionAdjustedReferenceValue,
            salePriceBasis,
            best,
            sellOffers,
            purchaseOffers);
    }

    internal HermesSellOffer? GetBestBaseItemSellOffer(
        MongoId itemTpl,
        MongoId sessionId)
    {
        var preparedProfile = preparedProfiles.Get(sessionId);
        var referencePrice = catalogService.GetReferencePrice(itemTpl);
        if (preparedProfile is null || referencePrice is null or <= 0)
        {
            return null;
        }

        // Craft profitability only needs the best trader purchase value for a pristine base
        // output. Avoid building trader purchase assortments, owned-copy models, and item-detail
        // view models for every unique craft output.
        return GetBestSellOfferForComponents(
            itemTpl,
            [new HermesTraderSaleComponent(
                itemTpl,
                referencePrice.Value,
                HermesSaleComponentKind.Root)],
            preparedProfile.ProfileJson);
    }

    internal HermesSellOffer? GetBestSellOfferForComponents(
        MongoId rootItemTpl,
        IReadOnlyList<HermesTraderSaleComponent> components,
        string profileJson)
    {
        var best = BuildSellOffers(rootItemTpl, components, profileJson).FirstOrDefault();
        return best is null ? null : best with { IsBest = true };
    }

    private List<HermesSellOffer> BuildSellOffers(
        MongoId rootItemTpl,
        IReadOnlyList<HermesTraderSaleComponent> components,
        string profileJson)
    {
        if (components.Count == 0)
        {
            return [];
        }

        var output = new List<HermesSellOffer>();

        foreach (var (traderId, trader) in databaseService.GetTraders())
        {
            var traderBase = trader.Base;
            if (!IsVanillaTrader(traderBase) || !CanTraderBuyItem(traderBase, rootItemTpl))
            {
                continue;
            }

            var playerState = ReadPlayerTraderState(profileJson, traderId, traderBase.UnlockedByDefault ?? true);
            if (!playerState.Unlocked)
            {
                continue;
            }

            var loyaltyLevels = traderBase.LoyaltyLevels;
            if (loyaltyLevels is null || loyaltyLevels.Count == 0)
            {
                continue;
            }

            var playerLoyalty = Math.Clamp(playerState.LoyaltyLevel, 1, loyaltyLevels.Count);
            var coefficient = loyaltyLevels[playerLoyalty - 1].BuyPriceCoefficient ?? 100d;
            var traderMultiplier = Math.Max(0d, 100d - coefficient) / 100d;

            long rootValue = 0L;
            long installedValue = 0L;
            long ignoredInstalledReferenceValue = 0L;
            var includedInstalledCount = 0;
            var includedWeaponAttachmentCount = 0;
            var includedArmorInsertCount = 0;
            var ignoredInstalledCount = 0;

            foreach (var component in components)
            {
                var isRoot = component.Kind == HermesSaleComponentKind.Root;
                var accepted = isRoot || CanTraderBuyItem(traderBase, component.TemplateId);
                if (!accepted)
                {
                    ignoredInstalledCount++;
                    ignoredInstalledReferenceValue += component.ConditionAdjustedReferenceValue;
                    continue;
                }

                var componentValue = Math.Max(
                    0L,
                    Convert.ToInt64(Math.Floor(
                        component.ConditionAdjustedReferenceValue * traderMultiplier)));

                if (isRoot)
                {
                    rootValue += componentValue;
                    continue;
                }

                installedValue += componentValue;
                includedInstalledCount++;
                if (component.Kind == HermesSaleComponentKind.ArmorInsert)
                {
                    includedArmorInsertCount++;
                }
                else
                {
                    includedWeaponAttachmentCount++;
                }
            }

            var roubleEquivalent = Math.Max(0L, rootValue + installedValue);
            if (roubleEquivalent <= 0)
            {
                continue;
            }

            var currency = GetTraderCurrency(traderBase.Currency);
            var amount = ConvertFromRoubles(roubleEquivalent, currency);

            output.Add(new HermesSellOffer(
                GetTraderName(traderBase),
                playerLoyalty,
                amount,
                currency,
                roubleEquivalent,
                rootValue,
                installedValue,
                includedInstalledCount,
                includedWeaponAttachmentCount,
                includedArmorInsertCount,
                ignoredInstalledCount,
                ignoredInstalledReferenceValue,
                false));
        }

        return output
            .OrderByDescending(offer => offer.RoubleEquivalent)
            .ThenBy(offer => offer.TraderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<HermesPurchaseOffer> BuildPurchaseOffers(
        MongoId itemTpl,
        MongoId sessionId,
        string profileJson)
    {
        var output = new List<HermesPurchaseOffer>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var marketPriceCache = new Dictionary<string, HermesMarketUnitValuation>(StringComparer.OrdinalIgnoreCase);

        foreach (var (traderId, trader) in databaseService.GetTraders())
        {
            var traderBase = trader.Base;
            if (!IsVanillaTrader(traderBase))
            {
                continue;
            }

            TraderAssort allAssort;
            TraderAssort availableAssort;

            try
            {
                allAssort = traderAssortHelper.GetAssort(sessionId, traderId, true);
                availableAssort = traderAssortHelper.GetAssort(sessionId, traderId, false);
            }
            catch
            {
                continue;
            }

            var roots = allAssort.Items
                .Where(assortItem => string.Equals(assortItem.SlotId, "hideout", StringComparison.OrdinalIgnoreCase)
                                     && assortItem.Template == itemTpl)
                .ToList();

            if (roots.Count == 0)
            {
                continue;
            }

            var availableRootIds = availableAssort.Items
                .Where(assortItem => string.Equals(assortItem.SlotId, "hideout", StringComparison.OrdinalIgnoreCase))
                .Select(assortItem => assortItem.Id)
                .ToHashSet();

            var questUnlocks = BuildQuestUnlockLookup(allAssort);
            var playerState = ReadPlayerTraderState(profileJson, traderId, traderBase.UnlockedByDefault ?? true);

            foreach (var root in roots)
            {
                var requiredLoyalty = allAssort.LoyalLevelItems.TryGetValue(root.Id, out var loyalty)
                    ? Math.Max(1, loyalty)
                    : 1;

                var unlimited = root.Upd?.UnlimitedCount ?? false;
                int? stock = unlimited
                    ? null
                    : ToNullableInt(root.Upd?.StackObjectsCount);

                var purchaseLimit = root.Upd?.BuyRestrictionMax;
                var purchaseCurrent = root.Upd?.BuyRestrictionCurrent ?? 0;
                int? purchaseRemaining = purchaseLimit.HasValue
                    ? Math.Max(0, purchaseLimit.Value - purchaseCurrent)
                    : null;

                var visibleToPlayer = availableRootIds.Contains(root.Id);
                questUnlocks.TryGetValue(root.Id.ToString(), out var questUnlock);
                var hasStock = unlimited || (stock ?? 0) > 0;
                var underLimit = !purchaseRemaining.HasValue || purchaseRemaining.Value > 0;
                var loyaltyMet = playerState.LoyaltyLevel >= requiredLoyalty;
                var isAvailable = playerState.Unlocked && loyaltyMet && visibleToPlayer && hasStock && underLimit;

                var reason = GetAvailabilityReason(
                    playerState.Unlocked,
                    loyaltyMet,
                    visibleToPlayer,
                    hasStock,
                    underLimit,
                    requiredLoyalty,
                    questUnlock);

                long? secondsUntilRestock = allAssort.NextResupply.HasValue
                    ? Math.Max(0L, Convert.ToInt64(Math.Floor(allAssort.NextResupply.Value)) - now)
                    : null;

                output.Add(new HermesPurchaseOffer(
                    GetTraderName(traderBase),
                    requiredLoyalty,
                    Math.Max(1, playerState.LoyaltyLevel),
                    isAvailable,
                    reason,
                    questUnlock?.QuestName,
                    questUnlock?.RequiredState,
                    questUnlock?.RequirementText,
                    unlimited,
                    stock,
                    purchaseLimit,
                    purchaseRemaining,
                    1,
                    secondsUntilRestock,
                    BuildPaymentOptions(allAssort, root.Id, marketPriceCache)));
            }
        }

        return output
            .OrderByDescending(offer => offer.IsAvailable)
            .ThenBy(offer => offer.RequiredLoyaltyLevel)
            .ThenBy(offer => offer.TraderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<HermesPaymentOption> BuildPaymentOptions(
        TraderAssort assort,
        MongoId assortId,
        IDictionary<string, HermesMarketUnitValuation> marketPriceCache)
    {
        if (!assort.BarterScheme.TryGetValue(assortId, out var schemes) || schemes is null)
        {
            return [];
        }

        var output = new List<HermesPaymentOption>();

        foreach (var scheme in schemes)
        {
            if (scheme is null || scheme.Count == 0)
            {
                continue;
            }

            var requirements = new List<HermesPaymentRequirement>();
            var estimatedRoubles = 0d;
            var allCurrency = true;
            var estimateAvailable = true;
            var usedHandbookFallback = false;

            foreach (var requirement in scheme)
            {
                var count = requirement.Count ?? 0d;
                var currency = GetCurrencyCode(requirement.Template);
                var isCurrency = currency is not null;
                allCurrency &= isCurrency;

                var requirementName = isCurrency
                    ? GetCurrencyName(currency!)
                    : catalogService.GetPlayerFacingName(requirement.Template);

                if (count <= 0d)
                {
                    estimateAvailable = false;
                    requirements.Add(new HermesPaymentRequirement(
                        requirementName,
                        count,
                        currency,
                        null,
                        null,
                        "Invalid required quantity",
                        false,
                        false));
                    continue;
                }

                if (isCurrency)
                {
                    var unitCurrencyValue = handbookHelper.InRoubles(1d, requirement.Template);
                    var currencyValue = handbookHelper.InRoubles(count, requirement.Template);
                    var currencyAvailable = unitCurrencyValue > 0d && currencyValue > 0d;
                    if (!currencyAvailable)
                    {
                        estimateAvailable = false;
                    }
                    else
                    {
                        estimatedRoubles += currencyValue;
                    }

                    requirements.Add(new HermesPaymentRequirement(
                        requirementName,
                        count,
                        currency,
                        currencyAvailable ? Convert.ToInt64(Math.Round(unitCurrencyValue)) : null,
                        currencyAvailable ? Convert.ToInt64(Math.Round(currencyValue)) : null,
                        currencyAvailable ? "Trader currency conversion" : "Currency conversion unavailable",
                        false,
                        currencyAvailable));
                    continue;
                }

                var marketValue = marketPriceService.GetBestUnitValue(
                    requirement.Template,
                    marketPriceCache);
                var requirementAvailable = marketValue.UnitValue > 0L;
                if (!requirementAvailable)
                {
                    estimateAvailable = false;
                }
                else
                {
                    estimatedRoubles += marketValue.UnitValue * count;
                    usedHandbookFallback |= marketValue.UsedHandbookFallback;
                }

                requirements.Add(new HermesPaymentRequirement(
                    requirementName,
                    count,
                    currency,
                    requirementAvailable ? marketValue.UnitValue : null,
                    requirementAvailable
                        ? Convert.ToInt64(Math.Round(marketValue.UnitValue * count))
                        : null,
                    requirementAvailable ? marketValue.Source : "Market value unavailable",
                    marketValue.UsedHandbookFallback,
                    requirementAvailable));
            }

            var displayPrice = allCurrency && requirements.Count == 1
                ? FormatCurrency(requirements[0].Count, requirements[0].Currency ?? "RUB")
                : string.Join(" + ", requirements.Select(requirement =>
                    $"{FormatCount(requirement.Count)} × {requirement.Name}"));

            string estimateSource;
            if (allCurrency)
            {
                estimateSource = "Trader currency conversion";
            }
            else if (!estimateAvailable)
            {
                estimateSource = "Market estimate unavailable for one or more requirements";
            }
            else
            {
                var marketSources = requirements
                    .Where(requirement => requirement.Currency is null && requirement.EstimateAvailable)
                    .Select(requirement => requirement.EstimateSource)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                estimateSource = marketSources.Count switch
                {
                    0 => usedHandbookFallback ? "Handbook fallback" : "Market-value chain",
                    1 => marketSources[0],
                    _ => "Mixed market sources — " + string.Join(" + ", marketSources)
                };
            }

            output.Add(new HermesPaymentOption(
                allCurrency,
                displayPrice,
                estimateAvailable ? Convert.ToInt64(Math.Round(estimatedRoubles)) : 0L,
                estimateSource,
                usedHandbookFallback,
                estimateAvailable,
                requirements));
        }

        return output;
    }

    private IReadOnlyDictionary<string, QuestUnlockInfo> BuildQuestUnlockLookup(TraderAssort assort)
    {
        var output = new Dictionary<string, QuestUnlockInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = JsonNode.Parse(jsonUtil.Serialize(assort) ?? "{}");
            var questAssort = FindProperty(root, "QuestAssort") as JsonObject;
            if (questAssort is null)
            {
                return output;
            }

            AddQuestUnlockState(questAssort, "Success", "Completed", "Complete", output);
            AddQuestUnlockState(questAssort, "Started", "Started", "Start", output);
            AddQuestUnlockState(questAssort, "Fail", "Failed", "Fail", output);
        }
        catch
        {
            // A missing or malformed quest-assort section should not break trader lookup.
        }

        return output;
    }

    private void AddQuestUnlockState(
        JsonObject questAssort,
        string stateProperty,
        string requiredState,
        string instructionVerb,
        IDictionary<string, QuestUnlockInfo> output)
    {
        var stateNode = ReadProperty(questAssort, stateProperty) as JsonObject;
        if (stateNode is null)
        {
            return;
        }

        foreach (var pair in stateNode)
        {
            AddQuestUnlockEntry(pair.Key, pair.Value, requiredState, instructionVerb, output);
        }
    }

    private void AddQuestUnlockEntry(
        string key,
        JsonNode? valueNode,
        string requiredState,
        string instructionVerb,
        IDictionary<string, QuestUnlockInfo> output)
    {
        var valueIds = ReadMongoIds(valueNode).ToList();
        var keyQuestName = TryGetQuestName(key);

        // Standard SPT layout: offer ID -> quest ID.
        foreach (var valueId in valueIds)
        {
            var valueQuestName = TryGetQuestName(valueId);
            if (!string.IsNullOrWhiteSpace(valueQuestName))
            {
                output[key] = CreateQuestUnlockInfo(valueQuestName, requiredState, instructionVerb);
                return;
            }
        }

        // Defensive support for reverse layouts: quest ID -> offer ID or offer ID array.
        if (!string.IsNullOrWhiteSpace(keyQuestName))
        {
            foreach (var offerId in valueIds)
            {
                output[offerId] = CreateQuestUnlockInfo(keyQuestName, requiredState, instructionVerb);
            }
        }
    }

    private string? TryGetQuestName(string? questId)
    {
        if (string.IsNullOrWhiteSpace(questId) || !MongoId.IsValidMongoId(questId))
        {
            return null;
        }

        return catalogService.GetQuestName(new MongoId(questId));
    }

    private static QuestUnlockInfo CreateQuestUnlockInfo(
        string questName,
        string requiredState,
        string instructionVerb)
    {
        return new QuestUnlockInfo(
            questName,
            requiredState,
            $"{instructionVerb} quest \"{questName}\"");
    }

    private static IEnumerable<string> ReadMongoIds(JsonNode? node)
    {
        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && MongoId.IsValidMongoId(text))
        {
            yield return text;
            yield break;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                foreach (var id in ReadMongoIds(child))
                {
                    yield return id;
                }
            }

            yield break;
        }

        if (node is JsonObject obj)
        {
            foreach (var name in new[] { "Id", "id", "_id", "QuestId", "questId", "OfferId", "offerId" })
            {
                var nested = ReadProperty(obj, name);
                foreach (var id in ReadMongoIds(nested))
                {
                    yield return id;
                }
            }
        }
    }

    private static JsonNode? ReadProperty(JsonObject obj, params string[] names)
    {
        foreach (var pair in obj)
        {
            if (names.Any(name => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private bool CanTraderBuyItem(TraderBase traderBase, MongoId itemTpl)
    {
        if (!MatchesRules(traderBase.ItemsBuy, itemTpl))
        {
            return false;
        }

        return !MatchesRules(traderBase.ItemsBuyProhibited, itemTpl);
    }

    private bool MatchesRules(ItemBuyData? rules, MongoId itemTpl)
    {
        if (rules is null)
        {
            return false;
        }

        if (rules.IdList.Contains(itemTpl))
        {
            return true;
        }

        return rules.Category.Count > 0 && itemHelper.IsOfBaseclasses(itemTpl, rules.Category);
    }

    private static bool IsVanillaTrader(TraderBase traderBase)
    {
        var nickname = traderBase.Nickname?.Trim();
        var name = traderBase.Name?.Trim();
        return (!string.IsNullOrWhiteSpace(nickname) && VanillaTraderNames.Contains(nickname))
               || (!string.IsNullOrWhiteSpace(name) && VanillaTraderNames.Contains(name));
    }

    private static string GetTraderName(TraderBase traderBase)
    {
        return !string.IsNullOrWhiteSpace(traderBase.Nickname)
            ? traderBase.Nickname
            : traderBase.Name;
    }

    private long ConvertFromRoubles(long roubles, string currency)
    {
        var currencyTpl = currency switch
        {
            "USD" => DollarsTpl,
            "EUR" => EurosTpl,
            "GP" => GpCoinTpl,
            _ => RoublesTpl
        };

        return Convert.ToInt64(Math.Floor(handbookHelper.FromRoubles(roubles, currencyTpl)));
    }

    private static string GetTraderCurrency(CurrencyType? currency)
    {
        return currency?.ToString() switch
        {
            "USD" => "USD",
            "EUR" => "EUR",
            "GP" => "GP",
            _ => "RUB"
        };
    }

    private static string? GetCurrencyCode(MongoId templateId)
    {
        if (templateId == RoublesTpl)
        {
            return "RUB";
        }

        if (templateId == DollarsTpl)
        {
            return "USD";
        }

        if (templateId == EurosTpl)
        {
            return "EUR";
        }

        if (templateId == GpCoinTpl)
        {
            return "GP";
        }

        return null;
    }

    private static string GetCurrencyName(string currency)
    {
        return currency switch
        {
            "USD" => "US dollars",
            "EUR" => "Euros",
            "GP" => "GP coins",
            _ => "Roubles"
        };
    }

    private static string FormatCurrency(double value, string currency)
    {
        var rounded = Math.Round(value);
        return currency switch
        {
            "USD" => $"${rounded:N0}",
            "EUR" => $"€{rounded:N0}",
            "GP" => $"{rounded:N0} GP",
            _ => $"₽{rounded:N0}"
        };
    }

    private static string FormatCount(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001
            ? Math.Round(value).ToString("N0")
            : value.ToString("N2");
    }

    private static string GetAvailabilityReason(
        bool traderUnlocked,
        bool loyaltyMet,
        bool visibleToPlayer,
        bool hasStock,
        bool underLimit,
        int requiredLoyalty,
        QuestUnlockInfo? questUnlock)
    {
        if (!traderUnlocked)
        {
            return "Trader is locked";
        }

        if (!loyaltyMet)
        {
            return $"Requires loyalty level {requiredLoyalty}";
        }

        if (!visibleToPlayer)
        {
            return questUnlock?.RequirementText ?? "Locked by trader progression";
        }

        if (!hasStock)
        {
            return "Out of stock";
        }

        if (!underLimit)
        {
            return "Purchase limit reached";
        }

        return "Available now";
    }

    private static int? ToNullableInt(double? value)
    {
        return value.HasValue
            ? Math.Max(0, Convert.ToInt32(Math.Floor(value.Value)))
            : null;
    }

    private static PlayerTraderState ReadPlayerTraderState(
        string profileJson,
        MongoId traderId,
        bool unlockedByDefault)
    {
        var state = new PlayerTraderState(1, unlockedByDefault);

        try
        {
            var root = JsonNode.Parse(profileJson);
            var tradersInfo = FindProperty(root, "TradersInfo") as JsonObject;
            if (tradersInfo is null
                || !tradersInfo.TryGetPropertyValue(traderId.ToString(), out var traderNode)
                || traderNode is not JsonObject traderObject)
            {
                return state;
            }

            var loyalty = ReadInt(traderObject, "loyaltyLevel", "LoyaltyLevel") ?? 1;
            var unlocked = ReadBool(traderObject, "unlocked", "Unlocked") ?? unlockedByDefault;
            return new PlayerTraderState(Math.Max(1, loyalty), unlocked);
        }
        catch
        {
            return state;
        }
    }

    private static JsonNode? FindProperty(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }

                var nested = FindProperty(pair.Value, propertyName);
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
                var nested = FindProperty(child, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static int? ReadInt(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return Convert.ToInt32(doubleValue);
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var node)
                && node is JsonValue value
                && value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }
        }

        return null;
    }

    private static HermesTraderSummaryResponse NotFound(string? itemKey, string message)
    {
        return new HermesTraderSummaryResponse(
            false,
            message,
            itemKey ?? string.Empty,
            string.Empty,
            string.Empty,
            null,
            false,
            false,
            null,
            null,
            null,
            1d,
            0,
            0,
            0,
            null,
            null,
            null,
            "Trader sale valuation is unavailable.",
            null,
            [],
            []);
    }

    private sealed record PlayerTraderState(int LoyaltyLevel, bool Unlocked);

    private sealed record QuestUnlockInfo(
        string QuestName,
        string RequiredState,
        string RequirementText);
}
