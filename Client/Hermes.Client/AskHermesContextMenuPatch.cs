using System.Collections;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;

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
            var isHideoutContext = IsHideoutContext(itemContext, viewTypeName);

            // Keep raid/world-loot and unrelated context menus untouched. Hideout item
            // rows are an additional source, but every Ask HERMES item action opens Items & Market.
            if (!isOwnedInventoryItem && previewSource is null && !isHideoutContext)
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
            if (isHideoutContext)
            {
                Plugin.Log?.LogDebug(
                    $"Ask HERMES classified item context as Hideout (view type: {viewTypeName}).");
                var templateId = TryGetTemplateId(itemContext.Item);
                if (string.IsNullOrWhiteSpace(templateId))
                {
                    Plugin.Log?.LogWarning(
                        "Ask HERMES could not resolve the template id for a Hideout item.");
                    return;
                }

                action = () => Plugin.Instance?.OpenForPreviewItem(templateId, "Hideout");
            }
            else if (isOwnedInventoryItem)
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

    private static bool IsHideoutContext(ItemContextClass itemContext, string viewTypeName)
    {
        if (ContainsHideoutMarker(viewTypeName))
        {
            return true;
        }

        // Hideout requirement and production rows commonly report EItemViewType.Inventory.
        // Inspect the context owner before falling back to a global active-screen probe so
        // the native Hideout origin wins over the generic owned-inventory classification.
        if (ObjectGraphContainsHideoutMarker(itemContext))
        {
            return true;
        }

        // This runs only when EFT creates an item context menu, never from Update. EFT's
        // obfuscated Hideout components are not guaranteed to use an EFT.Hideout namespace,
        // so inspect both component identities and their active GameObject hierarchy.
        return Resources.FindObjectsOfTypeAll<MonoBehaviour>().Any(IsActiveNativeHideoutUiComponent);
    }

    private static bool ObjectGraphContainsHideoutMarker(object source)
    {
        const BindingFlags flags = BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.Instance
                                   | BindingFlags.FlattenHierarchy;

        try
        {
            foreach (var field in source.GetType().GetFields(flags))
            {
                if (ContainsHideoutMarker(field.Name)
                    || ValueContainsHideoutMarker(field.GetValue(source)))
                {
                    return true;
                }
            }

            foreach (var property in source.GetType().GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (ContainsHideoutMarker(property.Name))
                {
                    return true;
                }

                object? value;
                try
                {
                    value = property.GetValue(source);
                }
                catch
                {
                    continue;
                }

                if (ValueContainsHideoutMarker(value))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Source classification must never prevent EFT from opening its context menu.
        }

        return false;
    }

    private static bool ValueContainsHideoutMarker(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value is Component component)
        {
            return IsActiveNativeHideoutUiComponent(component, requireVisibleCanvas: false);
        }

        if (value is GameObject gameObject)
        {
            return IsActiveRuntimeObject(gameObject)
                   && ContainsHideoutMarker(BuildHierarchyDescriptor(gameObject.transform));
        }

        if (value is Transform transform)
        {
            return transform != null
                   && IsActiveRuntimeObject(transform.gameObject)
                   && ContainsHideoutMarker(BuildHierarchyDescriptor(transform));
        }

        var type = value.GetType();
        return ContainsHideoutMarker(type.FullName)
               && !IsHermesType(type);
    }

    private static bool IsActiveNativeHideoutUiComponent(MonoBehaviour component)
    {
        return IsActiveNativeHideoutUiComponent(component, requireVisibleCanvas: true);
    }

    private static bool IsActiveNativeHideoutUiComponent(Component component, bool requireVisibleCanvas)
    {
        if (component == null
            || component.gameObject == null
            || !IsActiveRuntimeObject(component.gameObject))
        {
            return false;
        }

        var type = component.GetType();
        if (IsHermesType(type))
        {
            return false;
        }

        if (requireVisibleCanvas && !IsVisibleNativeUi(component.transform))
        {
            return false;
        }

        var descriptor = string.Join(
            "/",
            type.Namespace ?? string.Empty,
            type.Name,
            component.gameObject.name,
            BuildHierarchyDescriptor(component.transform));

        return ContainsHideoutMarker(descriptor);
    }

    private static bool IsVisibleNativeUi(Transform transform)
    {
        var canvas = transform.GetComponentInParent<Canvas>(true);
        if (canvas == null || !canvas.isActiveAndEnabled)
        {
            return false;
        }

        return transform.GetComponentsInParent<CanvasGroup>(true)
            .All(group => group == null || group.alpha > 0.01f);
    }

    private static bool IsActiveRuntimeObject(GameObject gameObject)
    {
        return gameObject.activeInHierarchy
               && gameObject.scene.IsValid()
               && gameObject.scene.isLoaded;
    }

    private static bool IsHermesType(Type type)
    {
        return (type.Namespace ?? string.Empty).StartsWith("Hermes.Client", StringComparison.Ordinal);
    }

    private static string BuildHierarchyDescriptor(Transform transform)
    {
        var names = new List<string>(8);
        var current = transform;
        for (var depth = 0; current != null && depth < 8; depth++, current = current.parent)
        {
            names.Add(current.gameObject.name);
        }

        return string.Join("/", names);
    }

    private static bool ContainsHideoutMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("hideout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("production", StringComparison.OrdinalIgnoreCase)
               || value.Contains("recipe", StringComparison.OrdinalIgnoreCase)
               || value.Contains("scheme", StringComparison.OrdinalIgnoreCase);
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
