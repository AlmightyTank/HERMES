using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT.InventoryLogic;
using EFT.UI;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal static class HermesLiveInventoryTagBridge
{
    private const int MaximumVisitedObjects = 12000;

    private static readonly BindingFlags InstanceMembers = BindingFlags.Public
                                                           | BindingFlags.NonPublic
                                                           | BindingFlags.Instance
                                                           | BindingFlags.FlattenHierarchy;

    private static readonly Dictionary<string, int> TagColorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 0,
        ["orange"] = 1,
        ["yellow"] = 2,
        ["green"] = 3,
        ["blue"] = 4,
        ["violet"] = 5,
        ["purple"] = 5,
        ["grey"] = 6,
        ["gray"] = 6,
        ["black"] = 6,
        ["white"] = 6,
        ["default"] = 6
    };

    private static WeakReference<InventoryController>? _currentInventoryController;

    internal static void SetCurrentInventoryController(InventoryController? controller)
    {
        if (controller is null)
        {
            return;
        }

        _currentInventoryController = new WeakReference<InventoryController>(controller);
    }

    internal static int ApplyTagMutation(
        IEnumerable<HermesStashInstanceSummary> instances,
        string mode,
        string tagName,
        string tagColor)
    {
        var updated = 0;
        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        var effectiveName = normalizedMode == "remove" ? string.Empty : tagName.Trim();
        var effectiveColor = normalizedMode == "remove" ? 0 : TagColorId(tagColor);

        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.ProfileItemId))
            {
                continue;
            }

            try
            {
                var item = FindLiveItem(instance.ProfileItemId);
                if (item is null)
                {
                    continue;
                }

                if (SetLiveTag(item, effectiveName, effectiveColor))
                {
                    updated++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"HERMES could not update live EFT tag for item {instance.ProfileItemId}: {ex.Message}");
            }
        }

        return updated;
    }

    private static Item? FindLiveItem(string itemId)
    {
        if (_currentInventoryController != null
            && _currentInventoryController.TryGetTarget(out var controller)
            && controller != null)
        {
            var fromController = SearchObjectGraphForItem(controller, itemId);
            if (fromController is not null)
            {
                return fromController;
            }
        }

        foreach (var screen in Resources.FindObjectsOfTypeAll<InventoryScreen>())
        {
            if (screen == null
                || screen.gameObject == null
                || !screen.gameObject.scene.IsValid()
                || !screen.gameObject.scene.isLoaded)
            {
                continue;
            }

            var fromScreen = SearchObjectGraphForItem(screen, itemId);
            if (fromScreen is not null)
            {
                return fromScreen;
            }
        }

        return null;
    }

    private static Item? SearchObjectGraphForItem(object root, string itemId)
    {
        var queue = new Queue<object>();
        var visited = new HashSet<object>(ReferenceComparer.Instance);
        queue.Enqueue(root);

        while (queue.Count > 0 && visited.Count < MaximumVisitedObjects)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is Item item && string.Equals(item.Id, itemId, StringComparison.Ordinal))
            {
                return item;
            }

            if (current is IEnumerable enumerable && current is not string)
            {
                foreach (var value in enumerable)
                {
                    if (ShouldTraverse(value))
                    {
                        queue.Enqueue(value!);
                    }
                }

                continue;
            }

            var type = current.GetType();
            if (!ShouldInspectMembers(type))
            {
                continue;
            }

            foreach (var field in type.GetFields(InstanceMembers))
            {
                object? value;
                try
                {
                    value = field.GetValue(current);
                }
                catch
                {
                    continue;
                }

                if (ShouldTraverse(value))
                {
                    queue.Enqueue(value!);
                }
            }

            foreach (var property in type.GetProperties(InstanceMembers))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(current);
                }
                catch
                {
                    continue;
                }

                if (ShouldTraverse(value))
                {
                    queue.Enqueue(value!);
                }
            }
        }

        return null;
    }

    private static bool SetLiveTag(Item item, string tagName, int tagColor)
    {
        var tagComponent = ResolveTagComponent(item);
        if (tagComponent is null)
        {
            return false;
        }

        var setMethod = typeof(TagComponent).GetMethod(
            "Set",
            InstanceMembers,
            null,
            [typeof(string), typeof(int), typeof(bool)],
            null);
        if (setMethod is null)
        {
            return false;
        }

        setMethod.Invoke(tagComponent, [tagName, tagColor, false]);
        return true;
    }

    private static TagComponent? ResolveTagComponent(Item item)
    {
        var itemType = item.GetType();
        foreach (var property in itemType.GetProperties(InstanceMembers))
        {
            if (property.GetIndexParameters().Length != 0
                || !typeof(TagComponent).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            if (property.GetValue(item) is TagComponent component)
            {
                return component;
            }
        }

        foreach (var field in itemType.GetFields(InstanceMembers))
        {
            if (!typeof(TagComponent).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            if (field.GetValue(item) is TagComponent component)
            {
                return component;
            }
        }

        foreach (var method in itemType.GetMethods(InstanceMembers))
        {
            if (!method.IsGenericMethodDefinition
                || method.GetParameters().Length != 0
                || !method.Name.Contains("Component", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (method.MakeGenericMethod(typeof(TagComponent)).Invoke(item, null) is TagComponent component)
                {
                    return component;
                }
            }
            catch
            {
                // Keep probing; EFT obfuscated builds often expose several component helpers.
            }
        }

        return null;
    }

    private static bool ShouldTraverse(object? value)
    {
        if (value is null or string)
        {
            return false;
        }

        var type = value.GetType();
        return !type.IsPrimitive
               && !type.IsEnum
               && type != typeof(decimal)
               && (value is Item
                   || value is IEnumerable
                   || ShouldInspectMembers(type));
    }

    private static bool ShouldInspectMembers(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        if (assemblyName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Equals("Hermes.Client", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var namespaceName = type.Namespace ?? string.Empty;
        return namespaceName.StartsWith("EFT", StringComparison.Ordinal)
               || namespaceName.StartsWith("Comfort", StringComparison.Ordinal);
    }

    private static int TagColorId(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "blue" : value.Trim();
        return TagColorIds.TryGetValue(normalized, out var colorId) ? colorId : TagColorIds["blue"];
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
