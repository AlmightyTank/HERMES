using System.Globalization;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

public sealed partial class HermesLoadoutService
{
    private TemplateInfo GetTemplate(string templateId)
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
            var missing = TemplateInfo.Missing(templateId);
            lock (_templateSync)
            {
                _templateCache[templateId] = missing;
            }

            return missing;
        }

        var root = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
        var props = GetProperty(root, "_props", "Properties", "properties") as JsonObject;
        var name = ResolveTemplateName(templateId);
        var serializedProps = props?.ToJsonString() ?? string.Empty;
        var lowerName = name.ToLowerInvariant();
        var caliber = ReadString(props, "Caliber", "caliber", "AmmoCaliber", "ammoCaliber") ?? string.Empty;
        var weaponClass = ReadString(props, "weapClass", "WeapClass") ?? string.Empty;
        var cartridges = GetArray(GetProperty(props, "Cartridges", "cartridges"));
        var magazineCapacity = cartridges
            .Select(cartridge => ReadInt(cartridge, 0, "_max_count", "MaxCount", "max_count"))
            .DefaultIfEmpty(0)
            .Max();
        var armorClass = ReadInt(props, 0, "ArmorClass", "armorClass");
        var maximumMedicalResource = ReadDouble(props, 0d, "MaxHpResource", "maxHpResource");
        var maximumResource = ReadDouble(props, 0d, "MaxResource", "maxResource");
        var isAmmo = ReadDouble(props, 0d, "Damage", "damage") > 0d
                     || ReadDouble(props, 0d, "PenetrationPower", "penetrationPower") > 0d;
        var isMagazine = cartridges.Count > 0
                         || serializedProps.Contains("magAnimationIndex", StringComparison.OrdinalIgnoreCase);
        var isWeapon = !string.IsNullOrWhiteSpace(weaponClass)
                       || GetArray(GetProperty(props, "weapFireType", "WeapFireType")).Count > 0;
        var damageEffects = GetProperty(props, "effects_damage", "EffectsDamage");
        var stimulatorBuffs = GetProperty(props, "StimulatorBuffs", "stimulatorBuffs", "Buffs", "buffs");
        var hydrationEffect = FindNamedEffectValue(props, "Hydration");
        var energyEffect = FindNamedEffectValue(props, "Energy");
        var foodUseTime = ReadDouble(props, 0d, "FoodUseTime", "foodUseTime");
        var isFoodDrink = foodUseTime > 0d
                          || maximumResource > 0d
                             && (hydrationEffect.HasValue
                                 || energyEffect.HasValue
                                 || serializedProps.Contains("FoodDrink", StringComparison.OrdinalIgnoreCase))
                          || ContainsAny(
                              lowerName,
                              "water",
                              "juice",
                              "milk",
                              "drink",
                              "cola",
                              "tea",
                              "coffee",
                              "kvass",
                              "aquamari",
                              "ration",
                              "mre",
                              "iskra",
                              "crackers",
                              "sausage",
                              "tushonka",
                              "stew",
                              "sugar",
                              "chocolate");
        // Food can legitimately contain Buffs/StimulatorBuffs. Provision identity takes
        // precedence so ration packs are not counted as medicine merely because they grant buffs.
        var isMedical = !isFoodDrink
                        && (maximumMedicalResource > 0d
                            || HasEntries(damageEffects)
                            || ReadBool(props, false, "UseStimulatorBuffs", "useStimulatorBuffs")
                            || HasEntries(stimulatorBuffs));
        var isSurgeryKit = isMedical
                           && (lowerName.Contains("cms", StringComparison.Ordinal)
                           || lowerName.Contains("surv12", StringComparison.Ordinal)
                           || lowerName.Contains("surgical", StringComparison.Ordinal)
                           || serializedProps.Contains("Surgery", StringComparison.OrdinalIgnoreCase));
        var isGeneralBleedMedkit = ContainsAny(
            lowerName,
            "ifak",
            "afak",
            "salewa",
            "car first aid",
            "car kit",
            "grizzly");
        var treatsLightBleed = isMedical
                               && (ContainsAny(serializedProps, "LightBleeding", "Light bleed")
                                   || ContainsAny(lowerName, "bandage", "army bandage")
                                   || isGeneralBleedMedkit);
        var treatsHeavyBleed = isMedical
                               && (ContainsAny(serializedProps, "HeavyBleeding", "Heavy bleed")
                                   || ContainsAny(lowerName, "esmarch", "hemostat", "cat tourniquet", "calok")
                                   || isGeneralBleedMedkit);
        var treatsFracture = isMedical
                             && (ContainsAny(serializedProps, "Fracture")
                                 || ContainsAny(lowerName, "splint", "surv12"));
        var treatsPain = isMedical
                         && (ContainsAny(serializedProps, "Pain")
                             || ContainsAny(lowerName, "painkiller", "analgin", "ibuprofen", "golden star", "vaseline"));
        var providesHydration = isFoodDrink
                                && (hydrationEffect is > 0d
                                    || !hydrationEffect.HasValue
                                    && ContainsAny(
                                        lowerName,
                                        "water",
                                        "juice",
                                        "milk",
                                        "drink",
                                        "cola",
                                        "tea",
                                        "coffee",
                                        "kvass",
                                        "aquamari",
                                        "thermos"));
        var providesEnergy = isFoodDrink
                             && (energyEffect is > 0d
                                 || !energyEffect.HasValue
                                 && ContainsAny(
                                     lowerName,
                                     "ration",
                                     "mre",
                                     "iskra",
                                     "crackers",
                                     "sausage",
                                     "tushonka",
                                     "stew",
                                     "sugar",
                                     "chocolate",
                                     "oat",
                                     "peas",
                                     "squash",
                                     "herring",
                                     "sprats",
                                     "mayo",
                                     "condensed milk",
                                     "energy drink",
                                     "hot rod",
                                     "max energy"));
        var isArmor = armorClass > 0 || serializedProps.Contains("ArmorType", StringComparison.OrdinalIgnoreCase);
        var keyId = ReadString(props, "KeyId", "keyId") ?? string.Empty;
        var isKey = ReadDouble(props, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage") > 0d
                    || !string.IsNullOrWhiteSpace(keyId)
                    || lowerName.EndsWith(" key", StringComparison.Ordinal)
                    || lowerName.Contains("marked key", StringComparison.Ordinal);
        var armorSlots = ParseArmorSlots(props);
        var internalMagazine = isWeapon
                               && magazineCapacity > 0
                               && !GetArray(GetProperty(props, "Slots", "slots"))
                                   .Any(slot => IsMagazineSlot(ReadString(slot, "_name", "Name", "name")));

        var info = new TemplateInfo(
            true,
            templateId,
            name,
            props,
            serializedProps,
            caliber,
            isWeapon,
            isMagazine,
            isAmmo,
            isArmor,
            isKey,
            keyId,
            armorClass,
            magazineCapacity,
            internalMagazine,
            isMedical,
            maximumMedicalResource,
            treatsLightBleed,
            treatsHeavyBleed,
            treatsFracture,
            treatsPain,
            isSurgeryKit,
            maximumResource,
            providesHydration,
            providesEnergy,
            armorSlots);
        lock (_templateSync)
        {
            _templateCache[templateId] = info;
        }

        return info;
    }

    private string ResolveTemplateName(string templateId)
    {
        if (MongoId.IsValidMongoId(templateId))
        {
            var catalogName = catalogService.GetPlayerFacingName(new MongoId(templateId));
            if (!string.IsNullOrWhiteSpace(catalogName)
                && !catalogName.Equals("Unknown item", StringComparison.OrdinalIgnoreCase))
            {
                return catalogName;
            }
        }

        EnsureLocaleStrings();
        lock (_localeSync)
        {
            if (_localeStrings is not null
                && _localeStrings.TryGetValue(templateId + " Name", out var localized)
                && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        return "Unknown quest item";
    }

    private void EnsureLocaleStrings()
    {
        if (_localeStrings is not null)
        {
            return;
        }

        lock (_localeSync)
        {
            if (_localeStrings is not null)
            {
                return;
            }

            var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = staticData.GetLocalesRoot();
                var englishFound = false;
                CollectLanguageLocaleStrings(root, "en", output, ref englishFound);
                if (!englishFound)
                {
                    FlattenLocaleStrings(root, output);
                }
            }
            catch
            {
                // Missing locale data should not block loadout analysis.
            }

            _localeStrings = output;
        }
    }

    private static void CollectLanguageLocaleStrings(
        JsonNode? node,
        string language,
        IDictionary<string, string> output,
        ref bool found)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    FlattenLocaleStrings(pair.Value, output);
                    continue;
                }

                CollectLanguageLocaleStrings(pair.Value, language, output, ref found);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectLanguageLocaleStrings(child, language, output, ref found);
            }
        }
    }

    private static JsonNode? FindObjectPropertyRecursive(JsonNode? node, string propertyName)
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

                var nested = FindObjectPropertyRecursive(pair.Value, propertyName);
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
                var nested = FindObjectPropertyRecursive(child, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void FlattenLocaleStrings(JsonNode? node, IDictionary<string, string> output)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    output[pair.Key] = text;
                }
                else
                {
                    FlattenLocaleStrings(pair.Value, output);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                FlattenLocaleStrings(child, output);
            }
        }
    }

    private static IReadOnlyList<ArmorSlotDefinition> ParseArmorSlots(JsonObject? props)
    {
        var output = new List<ArmorSlotDefinition>();
        foreach (var slot in GetArray(GetProperty(props, "Slots", "slots")))
        {
            var name = ReadString(slot, "_name", "Name", "name") ?? string.Empty;
            if (!IsArmorSlot(name))
            {
                continue;
            }

            output.Add(new ArmorSlotDefinition(
                name,
                ReadBool(slot, false, "_required", "Required", "required")));
        }

        return output;
    }

    private IReadOnlySet<string> CollectAllowedMagazineTemplates(IReadOnlyList<InventoryNode> weaponTree)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in weaponTree)
        {
            var props = GetTemplate(node.TemplateId).Properties;
            foreach (var slot in GetArray(GetProperty(props, "Slots", "slots")))
            {
                var slotName = ReadString(slot, "_name", "Name", "name");
                if (!IsMagazineSlot(slotName))
                {
                    continue;
                }

                CollectMongoIds(GetProperty(slot, "_props", "Props", "props", "filters", "Filters"), output);
            }
        }

        return output;
    }

    private bool IsMagazineCompatible(
        InventoryNode magazine,
        string weaponCaliber,
        IReadOnlySet<string> allowedTemplates,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        if (allowedTemplates.Count > 0)
        {
            return allowedTemplates.Contains(magazine.TemplateId);
        }

        var loadedCalibers = CollectTree(magazine, children)
            .Where(item => IsLoadedAmmoSlot(item.SlotId) && GetTemplate(item.TemplateId).IsAmmo)
            .Select(item => GetTemplate(item.TemplateId).Caliber)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (loadedCalibers.Count > 0)
        {
            return loadedCalibers.All(caliber => CaliberMatches(weaponCaliber, caliber));
        }

        var supportedCalibers = GetMagazineSupportedCalibers(GetTemplate(magazine.TemplateId));
        return supportedCalibers.Count == 0
               || supportedCalibers.Any(caliber => CaliberMatches(weaponCaliber, caliber));
    }

    private IReadOnlyList<string> GetMagazineSupportedCalibers(TemplateInfo magazine)
    {
        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMongoIds(GetProperty(magazine.Properties, "Cartridges", "cartridges"), templateIds);
        return templateIds
            .Select(GetTemplate)
            .Where(template => template.IsAmmo && !string.IsNullOrWhiteSpace(template.Caliber))
            .Select(template => template.Caliber)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<InventoryNode> ParseInventoryItems(JsonNode? inventory)
    {
        var output = new List<InventoryNode>();
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

            output.Add(new InventoryNode(
                id,
                templateId,
                ReadString(item, "parentId", "ParentId"),
                ReadString(item, "slotId", "SlotId"),
                GetProperty(item, "upd", "Upd") as JsonObject));
        }

        return output;
    }

    private static IReadOnlyList<InventoryNode> CollectDescendants(
        IReadOnlyList<InventoryNode> roots,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        var output = new List<InventoryNode>();
        var queue = new Queue<InventoryNode>(roots);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Id))
            {
                continue;
            }

            output.Add(current);
            if (!children.TryGetValue(current.Id, out var childItems))
            {
                continue;
            }

            foreach (var child in childItems)
            {
                queue.Enqueue(child);
            }
        }

        return output;
    }

    private static IReadOnlyList<InventoryNode> CollectTree(
        InventoryNode root,
        IReadOnlyDictionary<string, List<InventoryNode>> children)
    {
        return CollectDescendants([root], children);
    }

    private ConditionInfo GetCondition(InventoryNode item, TemplateInfo template)
    {
        var repairable = GetProperty(item.Upd, "Repairable", "repairable");
        if (repairable is not null)
        {
            var current = ReadDouble(repairable, 0d, "Durability", "durability");
            var maximum = ReadDouble(repairable, current, "MaxDurability", "maxDurability");
            return new ConditionInfo(
                ToPercent(current, maximum),
                $"Durability {FormatNumber(current)}/{FormatNumber(maximum)}",
                true);
        }

        return new ConditionInfo(100, "Full condition", false);
    }

    private static bool HasUsableConsumableResource(
        InventoryNode item,
        TemplateInfo template)
    {
        if (!template.ProvidesHydration && !template.ProvidesEnergy)
        {
            return false;
        }

        var foodDrink = GetProperty(item.Upd, "FoodDrink", "foodDrink");
        if (foodDrink is null)
        {
            return true;
        }

        var fallback = template.MaximumConsumableResource > 0d
            ? template.MaximumConsumableResource
            : 1d;
        var remaining = ReadDouble(
            foodDrink,
            fallback,
            "HpPercent",
            "hpPercent",
            "Resource",
            "resource",
            "Value",
            "value");
        return remaining > 0d;
    }

    private static double? FindNamedEffectValue(JsonNode? node, string effectName)
    {
        if (node is JsonObject obj)
        {
            var effectType = ReadString(
                obj,
                "BuffType",
                "buffType",
                "EffectType",
                "effectType",
                "Type",
                "type");
            if (!string.IsNullOrWhiteSpace(effectType)
                && effectType.Equals(effectName, StringComparison.OrdinalIgnoreCase))
            {
                var typedValue = ReadNullableDouble(
                    GetProperty(obj, "Value", "value", "Amount", "amount"));
                if (typedValue.HasValue)
                {
                    return typedValue;
                }
            }

            foreach (var pair in obj)
            {
                if (pair.Key.Equals(effectName, StringComparison.OrdinalIgnoreCase))
                {
                    var directValue = ReadEffectValue(pair.Value);
                    if (directValue.HasValue)
                    {
                        return directValue;
                    }
                }

                var nestedValue = FindNamedEffectValue(pair.Value, effectName);
                if (nestedValue.HasValue)
                {
                    return nestedValue;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var nestedValue = FindNamedEffectValue(child, effectName);
                if (nestedValue.HasValue)
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }

    private static double? ReadEffectValue(JsonNode? node)
    {
        var direct = ReadNullableDouble(node);
        if (direct.HasValue)
        {
            return direct;
        }

        if (node is JsonObject obj)
        {
            var named = ReadNullableDouble(
                GetProperty(obj, "Value", "value", "Amount", "amount"));
            if (named.HasValue)
            {
                return named;
            }
        }

        return null;
    }

    private static double? ReadNullableDouble(JsonNode? node)
    {
        if (node is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<double>(out var number))
        {
            return number;
        }

        if (jsonValue.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (jsonValue.TryGetValue<string>(out var text)
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string BuildMedicalCoverageText(TemplateInfo template)
    {
        var coverage = new List<string>();
        if (template.MaximumMedicalResource > 0d)
        {
            coverage.Add("healing");
        }

        if (template.TreatsLightBleed)
        {
            coverage.Add("light bleed");
        }

        if (template.TreatsHeavyBleed)
        {
            coverage.Add("heavy bleed");
        }

        if (template.TreatsFracture)
        {
            coverage.Add("fracture");
        }

        if (template.TreatsPain)
        {
            coverage.Add("pain");
        }

        if (template.IsSurgeryKit)
        {
            coverage.Add("surgery");
        }

        return coverage.Count == 0 ? "medical utility" : string.Join(", ", coverage);
    }
}
