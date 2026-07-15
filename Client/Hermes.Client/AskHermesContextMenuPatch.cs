using System.Collections;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;

namespace Hermes.Client;

/// <summary>
/// Adds a normal EFT dynamic interaction to supported inventory, trader, and flea item context menus.
/// Owned inventory items are resolved as exact PMC profile instances. Trader and flea previews are
/// resolved by template and intentionally use the full-condition base-item estimate by default.
/// </summary>
internal sealed class AskHermesContextMenuPatch : ModulePatch
{
    private const string InteractionDictionaryKey = "hermes.ask";

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ItemUiContext).GetMethod(
                   "GetItemContextInteractions",
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   typeof(ItemUiContext).FullName,
                   "GetItemContextInteractions");
    }

    [PatchPostfix]
    private static void Postfix(object __result, ItemContextClass itemContext)
    {
        try
        {
            if (__result is null || itemContext?.Item is null)
            {
                return;
            }

            var viewTypeName = itemContext.ViewType.ToString();
            var isOwnedInventoryItem = IsOwnedPmcItemView(viewTypeName);
            var previewSource = GetPreviewSource(viewTypeName);

            // Keep raid/world-loot and unrelated context menus untouched.
            if (!isOwnedInventoryItem && previewSource is null)
            {
                return;
            }

            var dictionaryField = __result.GetType().GetField(
                "Dictionary_0",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

            if (dictionaryField?.GetValue(__result) is not IDictionary interactions
                || interactions.Contains(InteractionDictionaryKey))
            {
                return;
            }

            Action action;
            if (isOwnedInventoryItem)
            {
                var profileItemId = itemContext.Item.Id;
                if (string.IsNullOrWhiteSpace(profileItemId))
                {
                    return;
                }

                action = () => Plugin.Instance?.OpenForInventoryItem(profileItemId);
            }
            else
            {
                var templateId = TryGetTemplateId(itemContext.Item);
                if (string.IsNullOrWhiteSpace(templateId))
                {
                    Plugin.Log?.LogWarning(
                        $"Ask HERMES could not resolve the template id for a {previewSource} preview item.");
                    return;
                }

                var capturedSource = previewSource!;
                action = () => Plugin.Instance?.OpenForPreviewItem(templateId, capturedSource);
            }

            interactions[InteractionDictionaryKey] = new DynamicInteractionClass(
                InteractionDictionaryKey,
                "Ask HERMES",
                action,
                HermesIconService.AskHermesIcon);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"Failed to add Ask HERMES context action: {ex}");
        }
    }

    private static bool IsOwnedPmcItemView(string viewTypeName)
    {
        if (viewTypeName.Equals(EItemViewType.Inventory.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return viewTypeName.Contains("equipment", StringComparison.OrdinalIgnoreCase)
               || viewTypeName.Contains("gear", StringComparison.OrdinalIgnoreCase)
               || viewTypeName.Contains("character", StringComparison.OrdinalIgnoreCase)
               || viewTypeName.Contains("profile", StringComparison.OrdinalIgnoreCase)
               || viewTypeName.Contains("modding", StringComparison.OrdinalIgnoreCase)
               || viewTypeName.Contains("weapon", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPreviewSource(string viewTypeName)
    {
        if (viewTypeName.Contains("ragfair", StringComparison.OrdinalIgnoreCase)
            || viewTypeName.Contains("flea", StringComparison.OrdinalIgnoreCase)
            || viewTypeName.Contains("market", StringComparison.OrdinalIgnoreCase))
        {
            return "flea market";
        }

        if (viewTypeName.Contains("trader", StringComparison.OrdinalIgnoreCase)
            || viewTypeName.Contains("trading", StringComparison.OrdinalIgnoreCase)
            || viewTypeName.Contains("trade", StringComparison.OrdinalIgnoreCase))
        {
            return "trader";
        }

        return null;
    }

    private static string? TryGetTemplateId(Item item)
    {
        const BindingFlags flags = BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.Instance
                                   | BindingFlags.FlattenHierarchy;

        var itemType = item.GetType();
        foreach (var memberName in new[] { "TemplateId", "Tpl", "_tpl" })
        {
            var property = itemType.GetProperty(memberName, flags);
            var propertyValue = property?.GetValue(item)?.ToString();
            if (!string.IsNullOrWhiteSpace(propertyValue))
            {
                return propertyValue;
            }

            var field = itemType.GetField(memberName, flags);
            var fieldValue = field?.GetValue(item)?.ToString();
            if (!string.IsNullOrWhiteSpace(fieldValue))
            {
                return fieldValue;
            }
        }

        // Defensive fallback for client builds that expose only Item.Template.Id.
        var template = itemType.GetProperty("Template", flags)?.GetValue(item)
                       ?? itemType.GetField("Template", flags)?.GetValue(item);
        if (template is null)
        {
            return null;
        }

        var templateType = template.GetType();
        foreach (var memberName in new[] { "Id", "_id", "TemplateId" })
        {
            var propertyValue = templateType.GetProperty(memberName, flags)?.GetValue(template)?.ToString();
            if (!string.IsNullOrWhiteSpace(propertyValue))
            {
                return propertyValue;
            }

            var fieldValue = templateType.GetField(memberName, flags)?.GetValue(template)?.ToString();
            if (!string.IsNullOrWhiteSpace(fieldValue))
            {
                return fieldValue;
            }
        }

        return null;
    }
}
