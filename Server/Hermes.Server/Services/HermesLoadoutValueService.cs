using System.Globalization;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesLoadoutValueService(
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    HermesCatalogService catalogService,
    HermesMarketPriceService marketPriceService,
    HermesTraderService traderService,
    JsonUtil jsonUtil)
{
    private const long HighValueUninsuredThreshold = 100_000L;
    private const int MaximumReturnedItems = 300;

    private static readonly HashSet<string> ProtectedEquipmentSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecuredContainer",
        "Scabbard",
        "Armband",
        "Compass",
        "SpecialSlot1",
        "SpecialSlot2",
        "SpecialSlot3"
    };

    private static readonly HashSet<string> StructuralEquipmentSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pockets",
        "SecuredContainer"
    };

    private readonly object _templateSync = new();
    private readonly Dictionary<string, ValueTemplateInfo> _templateCache = new(StringComparer.OrdinalIgnoreCase);

    internal HermesLoadoutValueSummary GetSummary(
        MongoId sessionId,
        ICollection<HermesLoadoutWarning> warnings)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return Unavailable("HERMES could not read the active PMC profile for loadout valuation.");
        }

        var profileJson = jsonUtil.Serialize(profile) ?? "{}";
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(profileJson) as JsonObject;
        }
        catch
        {
            return Unavailable("HERMES could not parse the active PMC profile for loadout valuation.");
        }

        if (root is null)
        {
            return Unavailable("HERMES could not parse the active PMC profile for loadout valuation.");
        }

        var inventory = GetProperty(root, "Inventory", "inventory");
        var equipmentId = ReadString(inventory, "Equipment", "equipment");
        var items = ParseItems(inventory);
        if (string.IsNullOrWhiteSpace(equipmentId) || items.Count == 0)
        {
            return Unavailable("HERMES could not locate the active PMC equipment inventory for valuation.");
        }

        var byId = items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var equipmentItems = items
            .Where(item => IsDescendantOfEquipment(item, equipmentId, byId))
            .ToList();
        var insuredIds = ReadInsuredItemIds(root);
        var marketCache = new Dictionary<string, HermesMarketUnitValuation>(StringComparer.OrdinalIgnoreCase);
        var replacementQuoteCache = new Dictionary<string, ReplacementQuote>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<HermesLoadoutValueItem>();
        var unsupported = 0;

        foreach (var item in equipmentItems)
        {
            var topLevel = FindTopLevelEquipmentItem(item, equipmentId, byId);
            if (topLevel is null)
            {
                continue;
            }

            if (item.Id.Equals(topLevel.Id, StringComparison.OrdinalIgnoreCase)
                && StructuralEquipmentSlots.Contains(topLevel.SlotId ?? string.Empty))
            {
                continue;
            }

            if (!MongoId.IsValidMongoId(item.TemplateId))
            {
                unsupported++;
                continue;
            }

            var template = GetTemplate(item.TemplateId);
            if (!template.Exists)
            {
                unsupported++;
                continue;
            }

            var ancestorTemplates = GetAncestorTemplates(item, equipmentId, byId);
            var category = GetCategory(template, ancestorTemplates, item.SlotId);
            var slotName = FriendlySlotName(topLevel.SlotId);
            var isProtected = ProtectedEquipmentSlots.Contains(topLevel.SlotId ?? string.Empty);
            var isAtRisk = !isProtected;
            var quantity = GetStackCount(item);
            var condition = GetCondition(item, template);
            var market = marketPriceService.GetBestUnitValue(new MongoId(item.TemplateId), marketCache);
            var marketReplacement = market.UnitValue > 0L
                ? ScaleValue(market.UnitValue, quantity)
                : 0L;
            var catalogItem = catalogService.ResolveTemplate(new MongoId(item.TemplateId));
            var traderReplacement = GetTraderReplacementQuote(
                catalogItem,
                sessionId,
                replacementQuoteCache);
            var traderReplacementTotal = traderReplacement.UnitValue > 0L
                ? ScaleValue(traderReplacement.UnitValue, quantity)
                : 0L;
            var bestReplacement = SelectBestReplacement(
                marketReplacement,
                market.Source,
                traderReplacementTotal,
                traderReplacement.Source);

            var referencePrice = catalogService.GetReferencePrice(new MongoId(item.TemplateId)) ?? 0L;
            var conditionAdjustedReference = referencePrice > 0L
                ? Math.Max(0L, Convert.ToInt64(Math.Floor(referencePrice * quantity * condition.SaleModifier)))
                : 0L;
            var bestTraderSale = conditionAdjustedReference > 0L
                ? traderService.GetBestSellOfferForComponents(
                    new MongoId(item.TemplateId),
                    [new HermesTraderSaleComponent(
                        new MongoId(item.TemplateId),
                        conditionAdjustedReference,
                        HermesSaleComponentKind.Root)],
                    profileJson)
                : null;
            var traderLiquidation = bestTraderSale?.RoubleEquivalent ?? 0L;
            var isInsurable = IsInsurable(category, template, isAtRisk);
            var isInsured = isInsurable && IsItemInsured(item, insuredIds);
            var insuranceStatus = isProtected
                ? "Protected"
                : !isInsurable
                    ? "Not insurable"
                    : isInsured
                        ? "Insured"
                        : "Uninsured";
            var highValueUninsured = isAtRisk
                                     && isInsurable
                                     && !isInsured
                                     && bestReplacement.UnitValue >= HighValueUninsuredThreshold;

            if (bestReplacement.UnitValue <= 0L && traderLiquidation <= 0L)
            {
                unsupported++;
            }

            lines.Add(new HermesLoadoutValueItem(
                item.Id,
                template.Name,
                category,
                slotName,
                quantity,
                condition.Percent,
                condition.Description,
                traderLiquidation > 0L ? traderLiquidation : null,
                bestTraderSale?.TraderName,
                marketReplacement > 0L ? marketReplacement : null,
                market.Source,
                market.UsedHandbookFallback,
                traderReplacementTotal > 0L ? traderReplacementTotal : null,
                traderReplacement.Source,
                bestReplacement.UnitValue > 0L ? bestReplacement.UnitValue : null,
                bestReplacement.Source,
                isAtRisk,
                isProtected,
                isInsurable,
                insuranceStatus,
                highValueUninsured));
        }

        var valuedLines = lines
            .Where(line => line.BestReplacementValue is > 0 || line.TraderLiquidationValue is > 0)
            .ToList();
        var categoryRows = valuedLines
            .GroupBy(line => line.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HermesLoadoutValueCategory(
                group.Key,
                group.Count(),
                group.Sum(line => line.TraderLiquidationValue ?? 0L),
                group.Sum(line => line.MarketReplacementValue ?? 0L),
                group.Sum(line => line.BestReplacementValue ?? 0L),
                group.Where(line => line.IsAtRisk).Sum(line => line.BestReplacementValue ?? 0L),
                group.Where(line => line.InsuranceStatus.Equals("Uninsured", StringComparison.OrdinalIgnoreCase))
                    .Sum(line => line.BestReplacementValue ?? 0L)))
            .OrderByDescending(row => row.AtRiskReplacementValue)
            .ThenBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var insured = valuedLines.Where(line => line.InsuranceStatus == "Insured").ToList();
        var uninsured = valuedLines.Where(line => line.InsuranceStatus == "Uninsured").ToList();
        var protectedItems = valuedLines.Where(line => line.IsProtected).ToList();
        var atRisk = valuedLines.Where(line => line.IsAtRisk).ToList();
        var insuranceRate = FindLowestConfiguredInsuranceRate();
        long? estimatedInsuranceCost = uninsured.Count == 0
            ? 0L
            : insuranceRate.Rate is > 0d
                ? Math.Max(0L, Convert.ToInt64(Math.Round(
                    uninsured.Sum(line => line.BestReplacementValue ?? 0L) * insuranceRate.Rate.Value)))
                : null;
        var notes = BuildNotes(
            marketCache.Values,
            insuranceRate,
            lines.Count,
            unsupported,
            protectedItems.Count);

        foreach (var item in uninsured
                     .Where(line => line.IsHighValueUninsured)
                     .OrderByDescending(line => line.BestReplacementValue)
                     .Take(5))
        {
            warnings.Add(new HermesLoadoutWarning(
                "Warning",
                "Insurance",
                $"{item.Name} is uninsured with an estimated replacement value of ₽{item.BestReplacementValue!.Value:N0}."));
        }

        var orderedItems = lines
            .OrderByDescending(line => line.IsHighValueUninsured)
            .ThenByDescending(line => line.IsAtRisk)
            .ThenByDescending(line => line.BestReplacementValue ?? 0L)
            .ThenBy(line => line.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumReturnedItems)
            .ToList();

        return new HermesLoadoutValueSummary(
            true,
            null,
            valuedLines.Sum(line => line.TraderLiquidationValue ?? 0L),
            valuedLines.Sum(line => line.MarketReplacementValue ?? 0L),
            valuedLines.Sum(line => line.BestReplacementValue ?? 0L),
            atRisk.Sum(line => line.BestReplacementValue ?? 0L),
            protectedItems.Sum(line => line.BestReplacementValue ?? 0L),
            insured.Sum(line => line.BestReplacementValue ?? 0L),
            uninsured.Sum(line => line.BestReplacementValue ?? 0L),
            estimatedInsuranceCost,
            insuranceRate.Source,
            valuedLines.Count,
            unsupported,
            atRisk.Count,
            protectedItems.Count,
            insured.Count,
            uninsured.Count,
            uninsured.Count == 0 && insured.Count == 0
                ? "NO INSURABLE ITEMS"
                : uninsured.Count == 0
                    ? "FULLY INSURED"
                    : insured.Count == 0
                        ? "UNINSURED"
                        : "PARTIALLY INSURED",
            categoryRows,
            orderedItems,
            notes);
    }

    private ReplacementQuote GetTraderReplacementQuote(
        HermesCatalogItem? catalogItem,
        MongoId sessionId,
        IDictionary<string, ReplacementQuote> cache)
    {
        if (catalogItem is null)
        {
            return ReplacementQuote.Unavailable;
        }

        if (cache.TryGetValue(catalogItem.TemplateId.ToString(), out var cached))
        {
            return cached;
        }

        try
        {
            var summary = traderService.GetSummary(catalogItem.ItemKey, null, sessionId);
            var quote = summary.PurchaseOffers
                .Where(offer => offer.IsAvailable)
                .SelectMany(offer => offer.PaymentOptions
                    .Where(payment => payment.EstimateAvailable && payment.EstimatedRoubleValue > 0L)
                    .Select(payment => new ReplacementQuote(
                        Math.Max(1L, Convert.ToInt64(Math.Round(
                            payment.EstimatedRoubleValue / (double)Math.Max(1, offer.PackSize)))),
                        payment.IsCash ? offer.TraderName : $"{offer.TraderName} barter")))
                .OrderBy(candidate => candidate.UnitValue)
                .ThenBy(candidate => candidate.Source, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? ReplacementQuote.Unavailable;
            cache[catalogItem.TemplateId.ToString()] = quote;
            return quote;
        }
        catch
        {
            cache[catalogItem.TemplateId.ToString()] = ReplacementQuote.Unavailable;
            return ReplacementQuote.Unavailable;
        }
    }

    private static ReplacementQuote SelectBestReplacement(
        long marketValue,
        string marketSource,
        long traderValue,
        string traderSource)
    {
        if (traderValue > 0L && (marketValue <= 0L || traderValue < marketValue))
        {
            return new ReplacementQuote(traderValue, traderSource);
        }

        return marketValue > 0L
            ? new ReplacementQuote(marketValue, marketSource)
            : ReplacementQuote.Unavailable;
    }

    private InsuranceRate FindLowestConfiguredInsuranceRate()
    {
        var candidates = new List<InsuranceRate>();
        foreach (var trader in databaseService.GetTraders().Values)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(jsonUtil.Serialize(trader.Base) ?? "{}");
            }
            catch
            {
                continue;
            }

            var enabled = FindNamedBoolean(node, name =>
                name.Equals("insurance", StringComparison.OrdinalIgnoreCase)
                || name.Equals("insuranceAvailable", StringComparison.OrdinalIgnoreCase));
            if (enabled == false)
            {
                continue;
            }

            var coefficient = FindNamedNumber(node, name =>
                name.Contains("insurance", StringComparison.OrdinalIgnoreCase)
                && (name.Contains("price", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("coef", StringComparison.OrdinalIgnoreCase)));
            if (coefficient is null or <= 0d)
            {
                continue;
            }

            var rate = coefficient.Value > 1d
                ? coefficient.Value / 100d
                : coefficient.Value;
            if (rate is <= 0d or > 1d)
            {
                continue;
            }

            var traderName = trader.Base.Nickname
                             ?? trader.Base.Name
                             ?? "configured insurer";
            candidates.Add(new InsuranceRate(
                rate,
                $"Estimated from {traderName}'s configured base insurance coefficient."));
        }

        return candidates
            .OrderBy(candidate => candidate.Rate)
            .FirstOrDefault()
            ?? InsuranceRate.Unavailable;
    }

    private static IReadOnlyList<string> BuildNotes(
        IEnumerable<HermesMarketUnitValuation> marketValues,
        InsuranceRate insuranceRate,
        int totalItems,
        int unsupported,
        int protectedCount)
    {
        var values = marketValues.ToList();
        var notes = new List<string>
        {
            "Market replacement uses the shared order: active cash flea offer, converted flea barter, SPT dynamic flea-market price, then handbook fallback.",
            "Trader liquidation is condition-adjusted for the exact carried item instance. Replacement estimates use a full replacement item at current market/trader value.",
            "Secure-container contents, melee/scabbard equipment, armbands, compass, and special-slot items are separated from at-risk raid value."
        };

        var handbookCount = values.Count(value => value.UsedHandbookFallback);
        var dynamicCount = values.Count(value => value.Source.Contains("dynamic flea", StringComparison.OrdinalIgnoreCase));
        if (handbookCount > 0)
        {
            notes.Add($"{handbookCount:N0} template valuation(s) required handbook fallback because no market price was available.");
        }

        if (dynamicCount > 0)
        {
            notes.Add($"{dynamicCount:N0} template valuation(s) used SPT dynamic flea-market pricing.");
        }

        if (unsupported > 0)
        {
            notes.Add($"{unsupported:N0} carried item instance(s) could not be valued reliably.");
        }

        if (protectedCount > 0)
        {
            notes.Add($"{protectedCount:N0} valued item instance(s) are carried in protected equipment slots and excluded from raid risk.");
        }

        notes.Add(insuranceRate.Rate is > 0d
            ? insuranceRate.Source + " Actual checkout cost can differ because of loyalty, item condition, and server mods."
            : "Insurance-cost estimation is unavailable because HERMES could not resolve a configured insurer price coefficient; insurance state and uninsured value remain accurate from the profile.");
        notes.Add($"The detailed list returns the top {MaximumReturnedItems:N0} valued instances; all {totalItems:N0} parsed carried instances are included in totals when supported.");
        return notes;
    }

    private ValueTemplateInfo GetTemplate(string templateId)
    {
        lock (_templateSync)
        {
            if (_templateCache.TryGetValue(templateId, out var cached))
            {
                return cached;
            }
        }

        if (!MongoId.IsValidMongoId(templateId)
            || !databaseService.GetItems().TryGetValue(new MongoId(templateId), out var template))
        {
            var missing = ValueTemplateInfo.Missing(templateId);
            lock (_templateSync)
            {
                _templateCache[templateId] = missing;
            }

            return missing;
        }

        var node = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
        var props = GetProperty(node, "_props", "Properties", "properties") as JsonObject;
        var serialized = props?.ToJsonString() ?? string.Empty;
        var name = catalogService.GetPlayerFacingName(new MongoId(templateId));
        var cartridges = GetArray(GetProperty(props, "Cartridges", "cartridges"));
        var isAmmo = ReadDouble(props, 0d, "Damage", "damage") > 0d
                     || ReadDouble(props, 0d, "PenetrationPower", "penetrationPower") > 0d;
        var isMagazine = cartridges.Count > 0
                         || serialized.Contains("magAnimationIndex", StringComparison.OrdinalIgnoreCase);
        var isWeapon = !string.IsNullOrWhiteSpace(ReadString(props, "weapClass", "WeapClass"))
                       || GetArray(GetProperty(props, "weapFireType", "WeapFireType")).Count > 0;
        var isArmor = ReadInt(props, 0, "ArmorClass", "armorClass") > 0
                      || serialized.Contains("ArmorType", StringComparison.OrdinalIgnoreCase);
        var maximumMedicalResource = ReadDouble(props, 0d, "MaxHpResource", "maxHpResource");
        var isMedical = maximumMedicalResource > 0d
                        || serialized.Contains("effects_damage", StringComparison.OrdinalIgnoreCase)
                        || serialized.Contains("EffectsDamage", StringComparison.OrdinalIgnoreCase)
                        || serialized.Contains("MedUseType", StringComparison.OrdinalIgnoreCase);
        var maximumResource = ReadDouble(props, 0d, "MaxResource", "maxResource");
        var maximumKeyUses = ReadDouble(props, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage");
        var isProvision = maximumResource > 0d
                          && (serialized.Contains("Hydration", StringComparison.OrdinalIgnoreCase)
                              || serialized.Contains("Energy", StringComparison.OrdinalIgnoreCase));

        var info = new ValueTemplateInfo(
            true,
            templateId,
            name,
            props,
            isWeapon,
            isMagazine,
            isAmmo,
            isArmor,
            isMedical,
            isProvision,
            maximumMedicalResource,
            maximumResource,
            maximumKeyUses);
        lock (_templateSync)
        {
            _templateCache[templateId] = info;
        }

        return info;
    }

    private string GetCategory(
        ValueTemplateInfo template,
        IReadOnlyList<ValueTemplateInfo> ancestors,
        string? slotId)
    {
        if (template.IsAmmo || template.IsMagazine)
        {
            return "Ammunition & magazines";
        }

        if (template.IsMedical)
        {
            return "Medical";
        }

        if (template.IsProvision)
        {
            return "Provisions";
        }

        if (template.IsWeapon || ancestors.Any(ancestor => ancestor.IsWeapon))
        {
            return "Weapons & attachments";
        }

        if (template.IsArmor
            || ancestors.Any(ancestor => ancestor.IsArmor)
            || IsArmorInsertSlot(slotId))
        {
            return "Armor & plates";
        }

        return "Other equipment";
    }

    private static bool IsInsurable(
        string category,
        ValueTemplateInfo template,
        bool isAtRisk)
    {
        if (!isAtRisk || template.IsAmmo || template.IsMedical || template.IsProvision)
        {
            return false;
        }

        return category is "Weapons & attachments" or "Armor & plates" or "Other equipment"
               || template.IsMagazine;
    }

    private static ConditionValue GetCondition(
        LoadoutInventoryItem item,
        ValueTemplateInfo template)
    {
        var repairable = GetProperty(item.Upd, "Repairable", "repairable");
        if (repairable is not null)
        {
            var current = ReadDouble(repairable, 0d, "Durability", "durability");
            var maximum = Math.Max(0.01d, ReadDouble(repairable, current, "MaxDurability", "maxDurability"));
            var ratio = Math.Clamp(current / maximum, 0d, 1d);
            return new ConditionValue(
                ToPercent(current, maximum),
                Math.Max(0.01d, Math.Sqrt(ratio)),
                $"Durability {FormatNumber(current)}/{FormatNumber(maximum)}");
        }

        var medKit = GetProperty(item.Upd, "MedKit", "medKit");
        if (medKit is not null && template.MaximumMedicalResource > 0d)
        {
            var current = ReadDouble(medKit, 0d, "HpResource", "hpResource");
            var ratio = Math.Clamp(current / template.MaximumMedicalResource, 0d, 1d);
            return new ConditionValue(
                ToPercent(current, template.MaximumMedicalResource),
                Math.Max(0.01d, ratio),
                $"Medical resource {FormatNumber(current)}/{FormatNumber(template.MaximumMedicalResource)}");
        }

        var foodDrink = GetProperty(item.Upd, "FoodDrink", "foodDrink");
        if (foodDrink is not null && template.MaximumResource > 0d)
        {
            var current = ReadDouble(foodDrink, 0d, "HpPercent", "hpPercent");
            var ratio = Math.Clamp(current / template.MaximumResource, 0d, 1d);
            return new ConditionValue(
                ToPercent(current, template.MaximumResource),
                Math.Max(0.01d, ratio),
                $"Resource {FormatNumber(current)}/{FormatNumber(template.MaximumResource)}");
        }

        var resource = GetProperty(item.Upd, "Resource", "resource");
        if (resource is not null && template.MaximumResource > 0d)
        {
            var current = ReadDouble(resource, 0d, "Value", "value");
            var ratio = Math.Clamp(current / template.MaximumResource, 0d, 1d);
            return new ConditionValue(
                ToPercent(current, template.MaximumResource),
                Math.Max(0.01d, ratio),
                $"Resource {FormatNumber(current)}/{FormatNumber(template.MaximumResource)}");
        }

        var key = GetProperty(item.Upd, "Key", "key");
        if (key is not null && template.MaximumKeyUses > 0d)
        {
            var used = ReadDouble(key, 0d, "NumberOfUsages", "numberOfUsages");
            var remaining = Math.Max(0d, template.MaximumKeyUses - used);
            var ratio = Math.Clamp(remaining / template.MaximumKeyUses, 0d, 1d);
            return new ConditionValue(
                ToPercent(remaining, template.MaximumKeyUses),
                Math.Max(0.01d, ratio),
                $"Uses remaining {FormatNumber(remaining)}/{FormatNumber(template.MaximumKeyUses)}");
        }

        return ConditionValue.Full;
    }

    private static HashSet<string> ReadInsuredItemIds(JsonObject root)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectInsuranceIds(root, false, output);
        return output;
    }

    private static void CollectInsuranceIds(
        JsonNode? node,
        bool insideInsuranceNode,
        ISet<string> output)
    {
        if (node is JsonValue value)
        {
            if (insideInsuranceNode
                && value.TryGetValue<string>(out var text)
                && MongoId.IsValidMongoId(text))
            {
                output.Add(text);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectInsuranceIds(child, insideInsuranceNode, output);
            }

            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var pair in obj)
        {
            var insuranceNode = insideInsuranceNode
                                || pair.Key.Contains("insured", StringComparison.OrdinalIgnoreCase)
                                || pair.Key.Equals("insurance", StringComparison.OrdinalIgnoreCase);
            if (insuranceNode && MongoId.IsValidMongoId(pair.Key))
            {
                output.Add(pair.Key);
            }

            CollectInsuranceIds(pair.Value, insuranceNode, output);
        }
    }

    private static bool IsItemInsured(
        LoadoutInventoryItem item,
        IReadOnlySet<string> insuredIds)
    {
        if (insuredIds.Contains(item.Id))
        {
            return true;
        }

        var insuredBy = ReadString(item.Upd, "InsuredBy", "insuredBy", "Insurance", "insurance");
        if (!string.IsNullOrWhiteSpace(insuredBy))
        {
            return true;
        }

        return ReadBool(item.Upd, false, "Insured", "insured", "IsInsured", "isInsured");
    }

    private static bool IsDescendantOfEquipment(
        LoadoutInventoryItem item,
        string equipmentId,
        IReadOnlyDictionary<string, LoadoutInventoryItem> byId)
    {
        var current = item;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (string.Equals(current.ParentId, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !byId.TryGetValue(current.ParentId, out var parent))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private static LoadoutInventoryItem? FindTopLevelEquipmentItem(
        LoadoutInventoryItem item,
        string equipmentId,
        IReadOnlyDictionary<string, LoadoutInventoryItem> byId)
    {
        var current = item;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (string.Equals(current.ParentId, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !byId.TryGetValue(current.ParentId, out var parent))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private IReadOnlyList<ValueTemplateInfo> GetAncestorTemplates(
        LoadoutInventoryItem item,
        string equipmentId,
        IReadOnlyDictionary<string, LoadoutInventoryItem> byId)
    {
        var output = new List<ValueTemplateInfo>();
        var parentId = item.ParentId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(parentId)
               && !parentId.Equals(equipmentId, StringComparison.OrdinalIgnoreCase)
               && visited.Add(parentId)
               && byId.TryGetValue(parentId, out var parent))
        {
            output.Add(GetTemplate(parent.TemplateId));
            parentId = parent.ParentId;
        }

        return output;
    }

    private static IReadOnlyList<LoadoutInventoryItem> ParseItems(JsonNode? inventory)
    {
        var output = new List<LoadoutInventoryItem>();
        foreach (var node in GetArray(GetProperty(inventory, "items", "Items")))
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var id = ReadString(item, "_id", "Id", "id");
            var templateId = ReadString(item, "_tpl", "Template", "template");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }

            output.Add(new LoadoutInventoryItem(
                id,
                templateId,
                ReadString(item, "parentId", "ParentId"),
                ReadString(item, "slotId", "SlotId"),
                GetProperty(item, "upd", "Upd") as JsonObject));
        }

        return output;
    }

    private static long ScaleValue(long unitValue, double quantity)
    {
        return Math.Max(0L, Convert.ToInt64(Math.Round(unitValue * Math.Max(1d, quantity))));
    }

    private static double GetStackCount(LoadoutInventoryItem item)
    {
        return Math.Max(1d, ReadDouble(item.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
    }

    private static string FriendlySlotName(string? slotId)
    {
        return slotId switch
        {
            "FirstPrimaryWeapon" => "Primary weapon",
            "SecondPrimaryWeapon" => "Secondary weapon",
            "ArmorVest" => "Body armor",
            "TacticalVest" => "Tactical rig",
            "Headwear" => "Headwear",
            "Earpiece" => "Headset",
            "FaceCover" => "Face cover",
            "SecuredContainer" => "Secure container",
            "SpecialSlot1" or "SpecialSlot2" or "SpecialSlot3" => "Special slot",
            null or "" => "Equipment",
            _ => SplitPascalCase(slotId)
        };
    }

    private static bool IsArmorInsertSlot(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("plate", StringComparison.Ordinal)
               || normalized.Contains("soft_armor", StringComparison.Ordinal)
               || normalized.Contains("softarmor", StringComparison.Ordinal)
               || normalized.Contains("armor_front", StringComparison.Ordinal)
               || normalized.Contains("armor_back", StringComparison.Ordinal)
               || normalized.Contains("armor_side", StringComparison.Ordinal);
    }

    private static double? FindNamedNumber(JsonNode? node, Func<string, bool> namePredicate)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (namePredicate(pair.Key))
                {
                    var value = ReadDouble(pair.Value, double.NaN);
                    if (!double.IsNaN(value))
                    {
                        return value;
                    }
                }

                var nested = FindNamedNumber(pair.Value, namePredicate);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nested = FindNamedNumber(child, namePredicate);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool? FindNamedBoolean(JsonNode? node, Func<string, bool> namePredicate)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (namePredicate(pair.Key))
                {
                    var parsed = ReadNullableBool(pair.Value);
                    if (parsed.HasValue)
                    {
                        return parsed;
                    }
                }

                var nested = FindNamedBoolean(pair.Value, namePredicate);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nested = FindNamedBoolean(child, namePredicate);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool? ReadNullableBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
        {
            return boolean;
        }

        return null;
    }

    private static int ToPercent(double current, double maximum)
    {
        return maximum <= 0d
            ? 0
            : Math.Clamp(Convert.ToInt32(Math.Round(current / maximum * 100d)), 0, 100);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var output = new List<char>(value.Length + 8) { value[0] };
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsWhiteSpace(value[index - 1]))
            {
                output.Add(' ');
            }

            output.Add(value[index]);
        }

        return new string(output.ToArray());
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var direct))
            {
                return direct;
            }

            var pair = obj.FirstOrDefault(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonNode?> GetArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.ToList();
        }

        if (node is JsonObject obj)
        {
            return obj.Select(pair => pair.Value).ToList();
        }

        return [];
    }

    private static string? ReadString(JsonNode? node, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value?.ToString();
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (jsonValue.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonNode? node, int fallback, params string[] names)
    {
        return Convert.ToInt32(Math.Round(ReadDouble(node, fallback, names)));
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static HermesLoadoutValueSummary Unavailable(string message)
    {
        return new HermesLoadoutValueSummary(
            false,
            message,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            "Unavailable",
            0,
            0,
            0,
            0,
            0,
            0,
            "UNAVAILABLE",
            [],
            [],
            []);
    }

    private sealed record LoadoutInventoryItem(
        string Id,
        string TemplateId,
        string? ParentId,
        string? SlotId,
        JsonObject? Upd);

    private sealed record ValueTemplateInfo(
        bool Exists,
        string TemplateId,
        string Name,
        JsonObject? Properties,
        bool IsWeapon,
        bool IsMagazine,
        bool IsAmmo,
        bool IsArmor,
        bool IsMedical,
        bool IsProvision,
        double MaximumMedicalResource,
        double MaximumResource,
        double MaximumKeyUses)
    {
        public static ValueTemplateInfo Missing(string templateId) => new(
            false,
            templateId,
            "Unknown item",
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            0d,
            0d,
            0d);
    }

    private sealed record ConditionValue(
        int Percent,
        double SaleModifier,
        string Description)
    {
        public static ConditionValue Full { get; } = new(100, 1d, "Full condition");
    }

    private sealed record ReplacementQuote(long UnitValue, string Source)
    {
        public static ReplacementQuote Unavailable { get; } = new(0L, "Unavailable");
    }

    private sealed record InsuranceRate(double? Rate, string Source)
    {
        public static InsuranceRate Unavailable { get; } = new(null, "Insurance-cost estimate unavailable.");
    }
}
