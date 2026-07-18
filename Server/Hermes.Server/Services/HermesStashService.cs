using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

internal enum HermesSaleComponentKind
{
    Root,
    WeaponAttachment,
    ArmorInsert
}

internal sealed record HermesTraderSaleComponent(
    MongoId TemplateId,
    long ConditionAdjustedReferenceValue,
    HermesSaleComponentKind Kind);

internal sealed record HermesSelectedStashInstance(
    HermesStashInstanceSummary Summary,
    IReadOnlyList<HermesTraderSaleComponent> Components);

internal sealed record HermesStashAnalysisEntry(
    MongoId RootTemplateId,
    string ItemKey,
    string Name,
    string ShortName,
    string Category,
    HermesStashInstanceSummary Instance,
    IReadOnlyList<HermesTraderSaleComponent> Components,
    long FullHandbookReferenceValue,
    int OccupiedCells,
    int ContainedItemCount,
    bool IsProtectedCurrency);

internal sealed record HermesStashAnalysisSnapshot(
    int TotalItemInstances,
    int IndependentItemCount,
    int UnsupportedIndependentItemCount,
    int OccupiedCells,
    IReadOnlyList<HermesStashAnalysisEntry> Entries);

[Injectable(InjectionType.Singleton)]
public sealed class HermesStashService(
    DatabaseService databaseService,
    HermesPreparedProfileSnapshotService preparedProfiles,
    HermesCatalogService catalogService,
    ItemHelper itemHelper,
    JsonUtil jsonUtil)
{
    private static readonly HashSet<string> ProtectedCurrencyTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        "5449016a4bdc2d6f028b456f", // Roubles
        "5696686a4bdc2da3298b456a", // US dollars
        "569668774bdc2da2298b4568"  // Euros
    };

    private readonly Dictionary<string, HashSet<string>> _gridSlotNames = new(StringComparer.OrdinalIgnoreCase);

    public HermesStashInstanceSelectionResponse GetInstanceSelection(
        string? profileItemId,
        MongoId sessionId)
    {
        return GetInventoryInstanceSelection(profileItemId, sessionId);
    }

    public HermesStashInstanceSelectionResponse GetInventoryInstanceSelection(
        string? profileItemId,
        MongoId sessionId)
    {
        if (string.IsNullOrWhiteSpace(profileItemId))
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "HERMES did not receive a valid inventory item.",
                null,
                null,
                null);
        }

        var snapshot = BuildInventorySnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "HERMES could not read the active PMC inventory.",
                null,
                null,
                null);
        }

        if (!snapshot.ById.TryGetValue(profileItemId, out var item))
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "This item is not present in the active PMC inventory.",
                null,
                null,
                null);
        }

        var isInStash = IsInStash(item, snapshot);
        var isEquipped = IsEquipped(item, snapshot);
        if (!isInStash && !isEquipped)
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "This item is not currently stored in the PMC stash or carried on the PMC.",
                null,
                null,
                null);
        }

        if (!MongoId.IsValidMongoId(item.TemplateId))
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "This inventory item has an unsupported template.",
                null,
                null,
                null);
        }

        var templateId = new MongoId(item.TemplateId);
        var itemSummary = catalogService.GetSummary(templateId);
        if (itemSummary is null)
        {
            return new HermesStashInstanceSelectionResponse(
                false,
                "HERMES does not list this item. Quest-only and handbook-less items are excluded.",
                null,
                null,
                null);
        }

        var tree = GetItemTree(item, snapshot);
        var components = BuildSaleComponents(tree);
        var instance = BuildInstanceSummary(item, tree, components, sessionId);
        var location = isInStash
            ? "PMC stash"
            : DescribeEquipmentLocation(item, snapshot);

        return new HermesStashInstanceSelectionResponse(
            true,
            null,
            itemSummary,
            instance,
            location);
    }

    public HermesStashInstancesResponse GetInstances(string? itemKey, MongoId sessionId)
    {
        var catalogItem = catalogService.ResolveItem(itemKey);
        if (catalogItem is null)
        {
            return new HermesStashInstancesResponse(
                false,
                "The selected HERMES item is no longer available. Search for it again.",
                itemKey ?? string.Empty,
                string.Empty,
                []);
        }

        var snapshot = BuildInventorySnapshot(sessionId);
        if (snapshot is null)
        {
            return new HermesStashInstancesResponse(
                false,
                "HERMES could not read the active PMC stash.",
                catalogItem.ItemKey,
                catalogItem.Name,
                []);
        }

        var instances = snapshot.Items
            .Where(item => item.TemplateId.Equals(catalogItem.TemplateId.ToString(), StringComparison.OrdinalIgnoreCase))
            .Where(item => IsInStash(item, snapshot))
            .Select(item =>
            {
                var tree = GetItemTree(item, snapshot);
                var components = BuildSaleComponents(tree);
                return BuildInstanceSummary(item, tree, components, sessionId);
            })
            .OrderByDescending(instance => instance.ConditionPercent)
            .ThenByDescending(instance => instance.Quantity)
            .ThenBy(instance => instance.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HermesStashInstancesResponse(
            true,
            instances.Count == 0 ? "No matching copy is currently stored in the PMC stash." : null,
            catalogItem.ItemKey,
            catalogItem.Name,
            instances);
    }

    internal HermesSelectedStashInstance? ResolveSelectedInstance(
        string? itemKey,
        string? instanceKey,
        MongoId sessionId)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
        {
            return null;
        }

        var catalogItem = catalogService.ResolveItem(itemKey);
        var snapshot = BuildInventorySnapshot(sessionId);
        if (catalogItem is null || snapshot is null)
        {
            return null;
        }

        foreach (var item in snapshot.Items)
        {
            if (!item.TemplateId.Equals(catalogItem.TemplateId.ToString(), StringComparison.OrdinalIgnoreCase)
                || (!IsInStash(item, snapshot) && !IsEquipped(item, snapshot))
                || !CreateInstanceKey(sessionId, item.Id).Equals(instanceKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tree = GetItemTree(item, snapshot);
            var components = BuildSaleComponents(tree);
            var summary = BuildInstanceSummary(item, tree, components, sessionId);
            return new HermesSelectedStashInstance(summary, components);
        }

        return null;
    }

    internal HermesStashAnalysisSnapshot? BuildAnalysisSnapshot(MongoId sessionId)
    {
        var snapshot = BuildInventorySnapshot(sessionId);
        if (snapshot is null)
        {
            return null;
        }

        var stashItems = snapshot.Items
            .Where(item => IsInStash(item, snapshot))
            .ToList();
        var independentItems = stashItems
            .Where(item => IsIndependentItem(item, snapshot))
            .ToList();

        var occupiedCells = independentItems
            .Where(item => !IsLoadedAmmunition(item.SlotId))
            .Sum(GetOccupiedCells);
        var entries = new List<HermesStashAnalysisEntry>();
        var unsupported = 0;

        foreach (var item in independentItems)
        {
            if (!MongoId.IsValidMongoId(item.TemplateId))
            {
                unsupported++;
                continue;
            }

            var templateId = new MongoId(item.TemplateId);
            var catalogItem = catalogService.ResolveTemplate(templateId);
            if (catalogItem is null)
            {
                unsupported++;
                continue;
            }

            var tree = GetItemTree(item, snapshot);
            var components = BuildSaleComponents(tree);
            if (components.Count == 0)
            {
                unsupported++;
                continue;
            }

            var instance = BuildInstanceSummary(item, tree, components, sessionId);
            var isProtectedCurrency = ProtectedCurrencyTemplates.Contains(item.TemplateId);
            entries.Add(new HermesStashAnalysisEntry(
                templateId,
                catalogItem.ItemKey,
                catalogItem.Name,
                catalogItem.ShortName,
                GetStashCategory(templateId, isProtectedCurrency),
                instance,
                components,
                GetFullHandbookReferenceValue(tree),
                IsLoadedAmmunition(item.SlotId) ? 0 : GetOccupiedCells(item),
                CountContainedItems(item, snapshot),
                isProtectedCurrency));
        }

        return new HermesStashAnalysisSnapshot(
            stashItems.Count,
            independentItems.Count,
            unsupported,
            Math.Max(0, occupiedCells),
            entries);
    }

    private bool IsIndependentItem(InventoryItemNode item, InventorySnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.StashId)
            && string.Equals(item.ParentId, snapshot.StashId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(item.ParentId)
            || !snapshot.ById.TryGetValue(item.ParentId, out var parent))
        {
            return false;
        }

        return IsLoadedAmmunition(item.SlotId)
               || IsGridContent(parent.TemplateId, item.SlotId);
    }

    private int GetOccupiedCells(InventoryItemNode item)
    {
        if (!MongoId.IsValidMongoId(item.TemplateId)
            || !databaseService.GetItems().TryGetValue(new MongoId(item.TemplateId), out var template))
        {
            return 0;
        }

        var width = Math.Max(1, template.Properties?.Width ?? 1);
        var height = Math.Max(1, template.Properties?.Height ?? 1);
        return width * height;
    }

    private long GetFullHandbookReferenceValue(IReadOnlyList<InventoryItemNode> tree)
    {
        long total = 0;
        foreach (var node in tree)
        {
            if (!MongoId.IsValidMongoId(node.TemplateId))
            {
                continue;
            }

            var referencePrice = catalogService.GetReferencePrice(new MongoId(node.TemplateId));
            if (referencePrice is null or <= 0)
            {
                continue;
            }

            var quantity = Math.Max(1d, ReadDouble(node.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
            total += Math.Max(0L, Convert.ToInt64(Math.Floor(referencePrice.Value * quantity)));
        }

        return total;
    }

    private HermesStashInstanceSummary BuildInstanceSummary(
        InventoryItemNode root,
        IReadOnlyList<InventoryItemNode> tree,
        IReadOnlyList<HermesTraderSaleComponent> components,
        MongoId sessionId)
    {
        var quantity = Math.Max(1d, ReadDouble(root.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
        var condition = GetCondition(root);
        var rootValue = components
            .Where(component => component.Kind == HermesSaleComponentKind.Root)
            .Sum(component => component.ConditionAdjustedReferenceValue);
        var installedValue = components
            .Where(component => component.Kind != HermesSaleComponentKind.Root)
            .Sum(component => component.ConditionAdjustedReferenceValue);
        var totalValue = Math.Max(0L, rootValue + installedValue);
        var childCount = Math.Max(0, tree.Count - 1);
        var weaponAttachmentCount = components.Count(component => component.Kind == HermesSaleComponentKind.WeaponAttachment);
        var armorInsertCount = components.Count(component => component.Kind == HermesSaleComponentKind.ArmorInsert);
        var foundInRaid = ReadBool(root.Upd, false, "SpawnedInSession", "spawnedInSession");
        var label = BuildLabel(
            quantity,
            condition,
            weaponAttachmentCount,
            armorInsertCount,
            childCount,
            foundInRaid);

        return new HermesStashInstanceSummary(
            CreateInstanceKey(sessionId, root.Id),
            label,
            quantity,
            condition.DisplayPercent,
            condition.Description,
            condition.Kind,
            condition.Current,
            condition.Maximum,
            foundInRaid,
            childCount,
            weaponAttachmentCount,
            armorInsertCount,
            rootValue,
            installedValue,
            totalValue);
    }

    private IReadOnlyList<HermesTraderSaleComponent> BuildSaleComponents(
        IReadOnlyList<InventoryItemNode> tree)
    {
        var output = new List<HermesTraderSaleComponent>();

        for (var index = 0; index < tree.Count; index++)
        {
            var node = tree[index];
            if (!MongoId.IsValidMongoId(node.TemplateId))
            {
                continue;
            }

            var templateId = new MongoId(node.TemplateId);
            var referencePrice = catalogService.GetReferencePrice(templateId);
            if (referencePrice is null or <= 0)
            {
                continue;
            }

            var quantity = Math.Max(1d, ReadDouble(node.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
            var quality = GetCondition(node).SaleQualityModifier;
            var adjustedValue = Math.Max(
                0L,
                Convert.ToInt64(Math.Floor(
                    referencePrice.Value * quantity * Math.Clamp(quality, 0.01d, 1d))));
            if (adjustedValue <= 0)
            {
                continue;
            }

            var kind = index == 0
                ? HermesSaleComponentKind.Root
                : IsArmorInsert(node)
                    ? HermesSaleComponentKind.ArmorInsert
                    : HermesSaleComponentKind.WeaponAttachment;

            output.Add(new HermesTraderSaleComponent(templateId, adjustedValue, kind));
        }

        return output;
    }

    private string GetStashCategory(MongoId templateId, bool isProtectedCurrency)
    {
        if (isProtectedCurrency)
        {
            return "Currency";
        }

        if (!databaseService.GetItems().TryGetValue(templateId, out var template))
        {
            return "Other";
        }

        var node = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
        var properties = GetProperty(node, "_props", "Properties", "properties");
        var serialized = properties?.ToJsonString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ReadString(properties, "weapClass", "WeapClass"))
            || GetArray(GetProperty(properties, "weapFireType", "WeapFireType")).Any())
        {
            return "Weapons";
        }

        if (ReadDouble(properties, 0d, "Damage", "damage") > 0d
            || ReadDouble(properties, 0d, "PenetrationPower", "penetrationPower") > 0d)
        {
            return "Ammunition";
        }

        if (GetArray(GetProperty(properties, "Cartridges", "cartridges")).Any()
            || serialized.Contains("magAnimationIndex", StringComparison.OrdinalIgnoreCase))
        {
            return "Magazines";
        }

        if (ReadDouble(properties, 0d, "ArmorClass", "armorClass") > 0d
            || serialized.Contains("ArmorType", StringComparison.OrdinalIgnoreCase))
        {
            return "Armor";
        }

        if (ReadDouble(properties, 0d, "MaxHpResource", "maxHpResource") > 0d
            || HasMeaningfulObject(GetProperty(properties, "effects_damage", "EffectsDamage"))
            || HasMeaningfulArray(GetProperty(properties, "Buffs", "buffs")))
        {
            return "Medical";
        }

        if (ReadDouble(properties, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage") > 0d)
        {
            return "Keys";
        }

        if (ReadDouble(properties, 0d, "MaxResource", "maxResource") > 0d
            && (serialized.Contains("Hydration", StringComparison.OrdinalIgnoreCase)
                || serialized.Contains("Energy", StringComparison.OrdinalIgnoreCase)))
        {
            return "Provisions";
        }

        if (GetArray(GetProperty(properties, "Grids", "grids")).Any())
        {
            return "Containers";
        }

        return "Other";
    }

    private static bool HasMeaningfulObject(JsonNode? node)
    {
        return node is JsonObject obj && obj.Count > 0;
    }

    private static bool HasMeaningfulArray(JsonNode? node)
    {
        return node is JsonArray array && array.Count > 0;
    }

    private bool IsArmorInsert(InventoryItemNode item)
    {
        var slotId = item.SlotId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (slotId.Contains("plate", StringComparison.Ordinal)
            || slotId.Contains("soft_armor", StringComparison.Ordinal)
            || itemHelper.IsSoftInsertId(slotId))
        {
            return true;
        }

        return MongoId.IsValidMongoId(item.TemplateId)
               && databaseService.GetItems().TryGetValue(new MongoId(item.TemplateId), out var template)
               && (template.Properties?.ArmorClass ?? 0) > 0;
    }

    private ConditionInfo GetCondition(InventoryItemNode item)
    {
        if (!MongoId.IsValidMongoId(item.TemplateId)
            || !databaseService.GetItems().TryGetValue(new MongoId(item.TemplateId), out var template))
        {
            return ConditionInfo.Full;
        }

        var templateNode = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
        var properties = GetProperty(templateNode, "_props", "Properties", "properties");
        var upd = item.Upd;
        if (upd is null)
        {
            return ConditionInfo.Full;
        }

        var repairable = GetProperty(upd, "Repairable", "repairable");
        if (repairable is not null)
        {
            var current = ReadDouble(repairable, 0d, "Durability", "durability");
            var currentMaximum = ReadDouble(repairable, current, "MaxDurability", "maxDurability");
            var originalMaximum = ReadDouble(properties, currentMaximum, "MaxDurability", "maxDurability");
            var divisor = Math.Max(0.01d, originalMaximum > 0d ? originalMaximum : currentMaximum);
            var physicalRatio = Math.Clamp(current / divisor, 0d, 1d);
            var saleModifier = physicalRatio <= 0d ? 0.01d : Math.Sqrt(physicalRatio);
            var armorClass = ReadDouble(properties, 0d, "ArmorClass", "armorClass");
            var weaponClass = ReadString(properties, "weapClass", "WeapClass");
            var kind = armorClass > 0d
                ? "Armor durability"
                : !string.IsNullOrWhiteSpace(weaponClass)
                    ? "Weapon durability"
                    : "Durability";
            return new ConditionInfo(
                ToPercent(physicalRatio),
                saleModifier,
                $"Durability {FormatNumber(current)}/{FormatNumber(currentMaximum)}",
                kind,
                current,
                currentMaximum);
        }

        var medKit = GetProperty(upd, "MedKit", "medKit");
        if (medKit is not null)
        {
            var current = ReadDouble(medKit, 0d, "HpResource", "hpResource");
            var maximum = Math.Max(0.01d, ReadDouble(properties, current, "MaxHpResource", "maxHpResource"));
            var ratio = Math.Clamp(current / maximum, 0d, 1d);
            return new ConditionInfo(ToPercent(ratio), Math.Max(0.01d, ratio), $"Medical resource {FormatNumber(current)}/{FormatNumber(maximum)}", "Medical resource", current, maximum);
        }

        var foodDrink = GetProperty(upd, "FoodDrink", "foodDrink");
        if (foodDrink is not null)
        {
            var current = ReadDouble(foodDrink, 0d, "HpPercent", "hpPercent");
            var maximum = Math.Max(0.01d, ReadDouble(properties, current, "MaxResource", "maxResource"));
            var ratio = Math.Clamp(current / maximum, 0d, 1d);
            return new ConditionInfo(ToPercent(ratio), Math.Max(0.01d, ratio), $"Resource {FormatNumber(current)}/{FormatNumber(maximum)}", "Consumable resource", current, maximum);
        }

        var resource = GetProperty(upd, "Resource", "resource");
        if (resource is not null)
        {
            var current = ReadDouble(resource, 0d, "Value", "value");
            var maximum = Math.Max(0.01d, ReadDouble(properties, current, "MaxResource", "maxResource"));
            var ratio = Math.Clamp(current / maximum, 0d, 1d);
            return new ConditionInfo(ToPercent(ratio), Math.Max(0.01d, ratio), $"Resource {FormatNumber(current)}/{FormatNumber(maximum)}", "Resource", current, maximum);
        }

        var repairKit = GetProperty(upd, "RepairKit", "repairKit");
        if (repairKit is not null)
        {
            var current = ReadDouble(repairKit, 0d, "Resource", "resource");
            var maximum = Math.Max(0.01d, ReadDouble(properties, current, "MaxRepairResource", "maxRepairResource"));
            var ratio = Math.Clamp(current / maximum, 0d, 1d);
            return new ConditionInfo(ToPercent(ratio), Math.Max(0.01d, ratio), $"Repair resource {FormatNumber(current)}/{FormatNumber(maximum)}", "Repair resource", current, maximum);
        }

        var key = GetProperty(upd, "Key", "key");
        var maximumUses = ReadDouble(properties, 0d, "MaximumNumberOfUsage", "maximumNumberOfUsage");
        if (key is not null && maximumUses > 0d)
        {
            var used = ReadDouble(key, 0d, "NumberOfUsages", "numberOfUsages");
            var remaining = Math.Max(0d, maximumUses - used);
            var ratio = Math.Clamp(remaining / maximumUses, 0d, 1d);
            return new ConditionInfo(ToPercent(ratio), Math.Max(0.01d, ratio), $"Uses remaining {FormatNumber(remaining)}/{FormatNumber(maximumUses)}", "Key uses", remaining, maximumUses);
        }

        return ConditionInfo.Full;
    }

    private static string BuildLabel(
        double quantity,
        ConditionInfo condition,
        int weaponAttachmentCount,
        int armorInsertCount,
        int childCount,
        bool foundInRaid)
    {
        var parts = new List<string>();

        if (quantity > 1d)
        {
            parts.Add($"Stack ×{FormatNumber(quantity)}");
        }

        parts.Add(condition.Description);

        if (weaponAttachmentCount > 0)
        {
            parts.Add($"{weaponAttachmentCount} attachment{(weaponAttachmentCount == 1 ? string.Empty : "s")}");
        }

        if (armorInsertCount > 0)
        {
            parts.Add($"{armorInsertCount} armor insert{(armorInsertCount == 1 ? string.Empty : "s")}");
        }

        var classifiedChildren = weaponAttachmentCount + armorInsertCount;
        if (childCount > classifiedChildren)
        {
            var otherCount = childCount - classifiedChildren;
            parts.Add($"{otherCount} unpriced installed item{(otherCount == 1 ? string.Empty : "s")}");
        }

        if (foundInRaid)
        {
            parts.Add("Found in raid");
        }

        return string.Join(" • ", parts);
    }

    private InventorySnapshot? BuildInventorySnapshot(MongoId sessionId)
    {
        var preparedProfile = preparedProfiles.Get(sessionId);
        if (preparedProfile is null)
        {
            return null;
        }

        var inventory = GetProperty(preparedProfile.Root, "Inventory", "inventory");
        if (inventory is null)
        {
            return null;
        }

        var stashId = ReadString(inventory, "Stash", "stash");
        var equipmentId = ReadString(inventory, "Equipment", "equipment");
        var items = new List<InventoryItemNode>();

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

            items.Add(new InventoryItemNode(
                id,
                templateId,
                ReadString(item, "parentId", "ParentId"),
                ReadString(item, "slotId", "SlotId"),
                GetProperty(item, "upd", "Upd") as JsonObject));
        }

        var byId = items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var children = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentId))
            .GroupBy(item => item.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return new InventorySnapshot(stashId, equipmentId, items, byId, children);
    }

    private static bool IsInStash(InventoryItemNode item, InventorySnapshot snapshot)
    {
        var current = item;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (!visited.Add(current.Id))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.StashId)
                && string.Equals(current.ParentId, snapshot.StashId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(snapshot.StashId)
                && string.Equals(current.SlotId, "hideout", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !snapshot.ById.TryGetValue(current.ParentId, out var parent))
            {
                return false;
            }

            current = parent;
        }
    }

    private static bool IsEquipped(InventoryItemNode item, InventorySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.EquipmentId))
        {
            return false;
        }

        var current = item;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (string.Equals(current.ParentId, snapshot.EquipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !snapshot.ById.TryGetValue(current.ParentId, out var parent))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private static string DescribeEquipmentLocation(
        InventoryItemNode item,
        InventorySnapshot snapshot)
    {
        var topLevel = GetTopLevelEquipmentItem(item, snapshot);
        if (topLevel is null)
        {
            return "PMC equipment";
        }

        var friendly = FriendlyEquipmentSlot(topLevel.SlotId);
        if (topLevel.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
        {
            return $"Equipped — {friendly}";
        }

        return (topLevel.SlotId ?? string.Empty) switch
        {
            "TacticalVest" => "Carried in tactical rig",
            "Backpack" => "Carried in backpack",
            "Pockets" => "Carried in pockets",
            "SecuredContainer" => "Carried in secure container",
            "FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster" => $"Equipped on {friendly}",
            _ => $"Installed on {friendly}"
        };
    }

    private static InventoryItemNode? GetTopLevelEquipmentItem(
        InventoryItemNode item,
        InventorySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.EquipmentId))
        {
            return null;
        }

        var current = item;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (string.Equals(current.ParentId, snapshot.EquipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !snapshot.ById.TryGetValue(current.ParentId, out var parent))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static string FriendlyEquipmentSlot(string? slotId)
    {
        return slotId switch
        {
            "FirstPrimaryWeapon" => "primary weapon",
            "SecondPrimaryWeapon" => "secondary weapon",
            "Holster" => "holstered weapon",
            "ArmorVest" => "body armor",
            "TacticalVest" => "tactical rig",
            "Headwear" => "headwear",
            "Earpiece" => "headset",
            "FaceCover" => "face cover",
            "Backpack" => "backpack",
            "Pockets" => "pockets",
            "SecuredContainer" => "secure container",
            "Scabbard" => "scabbard",
            null or "" => "equipment",
            _ => slotId.Replace('_', ' ').ToLowerInvariant()
        };
    }

    private int CountContainedItems(
        InventoryItemNode root,
        InventorySnapshot snapshot)
    {
        var count = 0;
        var queue = new Queue<(InventoryItemNode Item, bool InsideGrid)>();
        queue.Enqueue((root, false));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!snapshot.Children.TryGetValue(current.Item.Id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                var insideGrid = current.InsideGrid || IsGridContent(current.Item.TemplateId, child.SlotId);
                if (insideGrid)
                {
                    count++;
                }

                queue.Enqueue((child, insideGrid));
            }
        }

        return Math.Max(0, count);
    }

    private IReadOnlyList<InventoryItemNode> GetItemTree(
        InventoryItemNode root,
        InventorySnapshot snapshot)
    {
        var output = new List<InventoryItemNode> { root };
        var queue = new Queue<InventoryItemNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!snapshot.Children.TryGetValue(current.Id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                // Grid children are contents stored inside a backpack, rig, case, or other container.
                // They are not part of the selected item when that item is sold to a trader.
                if (IsGridContent(current.TemplateId, child.SlotId)
                    || IsLoadedAmmunition(child.SlotId))
                {
                    continue;
                }

                output.Add(child);
                queue.Enqueue(child);
            }
        }

        return output;
    }

    private static bool IsLoadedAmmunition(string? slotId)
    {
        var normalized = slotId?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Contains("cartridge", StringComparison.Ordinal)
               || normalized.Contains("cartridges", StringComparison.Ordinal)
               || normalized.Contains("chamber", StringComparison.Ordinal)
               || normalized.Contains("patron", StringComparison.Ordinal)
               || normalized.Equals("ammo", StringComparison.Ordinal);
    }

    private bool IsGridContent(string parentTemplateId, string? childSlotId)
    {
        if (string.IsNullOrWhiteSpace(childSlotId) || !MongoId.IsValidMongoId(parentTemplateId))
        {
            return false;
        }

        if (!_gridSlotNames.TryGetValue(parentTemplateId, out var gridNames))
        {
            gridNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (databaseService.GetItems().TryGetValue(new MongoId(parentTemplateId), out var template))
            {
                var templateNode = JsonNode.Parse(jsonUtil.Serialize(template) ?? "{}") as JsonObject;
                var properties = GetProperty(templateNode, "_props", "Properties", "properties");
                foreach (var gridNode in GetArray(GetProperty(properties, "Grids", "grids")))
                {
                    var name = ReadString(gridNode, "_name", "Name", "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        gridNames.Add(name);
                    }
                }
            }

            _gridSlotNames[parentTemplateId] = gridNames;
        }

        return gridNames.Contains(childSlotId);
    }

    private static string CreateInstanceKey(MongoId sessionId, string itemId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"HERMES:{sessionId}:{itemId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static int ToPercent(double ratio)
    {
        return Math.Clamp(Convert.ToInt32(Math.Round(ratio * 100d)), 0, 100);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("N1");
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var pair in obj)
        {
            if (names.Any(name => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IEnumerable<JsonNode?> GetArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        return [];
    }

    private static string? ReadString(JsonNode? node, params string[] names)
    {
        var value = GetProperty(node, names);
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value?.ToString().Trim('"');
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        var value = GetProperty(node, names);
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
                && double.TryParse(text, out number))
            {
                return number;
            }
        }

        return fallback;
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        var value = GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && bool.TryParse(text, out boolean))
            {
                return boolean;
            }
        }

        return fallback;
    }

    private sealed record InventoryItemNode(
        string Id,
        string TemplateId,
        string? ParentId,
        string? SlotId,
        JsonObject? Upd);

    private sealed record InventorySnapshot(
        string? StashId,
        string? EquipmentId,
        IReadOnlyList<InventoryItemNode> Items,
        IReadOnlyDictionary<string, InventoryItemNode> ById,
        IReadOnlyDictionary<string, List<InventoryItemNode>> Children);

    private sealed record ConditionInfo(
        int DisplayPercent,
        double SaleQualityModifier,
        string Description,
        string Kind,
        double Current,
        double Maximum)
    {
        public static ConditionInfo Full { get; } = new(100, 1d, "Full condition", "Full", 1d, 1d);
    }
}
