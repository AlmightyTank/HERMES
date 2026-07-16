using System;
using System.Linq;
using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Corrects the native HERMES inventory-tab icon after the tab is cloned from
/// Achievements. The old implementation added a second centered image which
/// covered the HERMES label. This patch removes that overlay and replaces the
/// inherited Achievements icon in the cloned tab's normal and selected states.
/// </summary>
internal sealed class HermesNativeTabIconFixPatch : ModulePatch
{
    private static readonly BindingFlags InstanceFields = BindingFlags.Instance
                                                               | BindingFlags.Public
                                                               | BindingFlags.NonPublic
                                                               | BindingFlags.FlattenHierarchy;

    private static readonly FieldInfo? NormalVersionField =
        typeof(global::Tab).GetField("_normalVersion", InstanceFields);

    private static readonly FieldInfo? SelectedVersionField =
        typeof(global::Tab).GetField("_selectedVersion", InstanceFields);

    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesNativeScreenHost).GetMethod(
                   "CreateHermesTab",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(
                   typeof(HermesNativeScreenHost).FullName,
                   "CreateHermesTab");
    }

    [PatchPostfix]
    private static void Postfix(global::Tab __result)
    {
        try
        {
            Apply(__result);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES could not correct the native tab icon: {ex}");
        }
    }

    private static void Apply(global::Tab? hermesTab)
    {
        if (hermesTab == null)
        {
            return;
        }

        RemoveAddedOverlay(hermesTab.transform);

        var sprite = HermesIconService.AskHermesIcon;
        if (sprite == null)
        {
            Plugin.Log?.LogWarning("HERMES could not replace the native tab icon because the embedded icon was unavailable.");
            return;
        }

        var normalReplaced = ReplaceStateIcon(
            NormalVersionField?.GetValue(hermesTab) as GameObject,
            sprite,
            Color.white);
        var selectedReplaced = ReplaceStateIcon(
            SelectedVersionField?.GetValue(hermesTab) as GameObject,
            sprite,
            Color.black);

        if (!normalReplaced && !selectedReplaced)
        {
            Plugin.Log?.LogWarning(
                "HERMES removed the overlapping tab icon, but could not locate the cloned Achievements icon slot to replace.");
            return;
        }

        Plugin.Log?.LogDebug(
            $"HERMES native tab icon corrected. Normal white: {normalReplaced}; selected inverted: {selectedReplaced}.");
    }

    private static void RemoveAddedOverlay(Transform tabRoot)
    {
        var overlays = tabRoot
            .GetComponentsInChildren<Transform>(true)
            .Where(transform => transform != null
                                && transform != tabRoot
                                && (transform.name.Equals(
                                        "HERMES_Icon",
                                        StringComparison.OrdinalIgnoreCase)
                                    || transform.name.Contains(
                                        "HermesIcon",
                                        StringComparison.OrdinalIgnoreCase)
                                    || transform.name.Contains(
                                        "IconOverlay",
                                        StringComparison.OrdinalIgnoreCase)))
            .Select(transform => transform.gameObject)
            .Distinct()
            .ToArray();

        foreach (var overlay in overlays)
        {
            overlay.SetActive(false);
            UnityEngine.Object.Destroy(overlay);
        }
    }

    private static bool ReplaceStateIcon(
        GameObject? stateRoot,
        Sprite sprite,
        Color tint)
    {
        if (stateRoot == null)
        {
            return false;
        }

        var images = stateRoot
            .GetComponentsInChildren<Image>(true)
            .Where(image => image != null
                            && image.sprite != null
                            && !IsBackgroundImage(image, stateRoot.transform))
            .ToList();

        if (images.Count == 0)
        {
            return false;
        }

        var icon = images
                       .Where(IsLikelyNamedIcon)
                       .OrderBy(GetIconScore)
                       .FirstOrDefault()
                   ?? images
                       .Where(IsLikelySizedIcon)
                       .OrderBy(GetIconScore)
                       .FirstOrDefault();

        if (icon == null)
        {
            return false;
        }

        icon.sprite = sprite;
        icon.type = Image.Type.Simple;
        icon.preserveAspect = true;
        icon.color = tint;
        icon.raycastTarget = false;
        return true;
    }

    private static bool IsBackgroundImage(Image image, Transform stateRoot)
    {
        if (image.transform == stateRoot)
        {
            return true;
        }

        var name = image.gameObject.name.ToLowerInvariant();
        return name.Contains("background")
               || name.Equals("bg", StringComparison.Ordinal)
               || name.Contains("frame")
               || name.Contains("glow")
               || name.Contains("hover")
               || name.Contains("plate")
               || name.Contains("button");
    }

    private static bool IsLikelyNamedIcon(Image image)
    {
        var name = image.gameObject.name.ToLowerInvariant();
        return name.Contains("icon")
               || name.Contains("achievement")
               || name.Contains("symbol")
               || name.Contains("glyph")
               || name.Contains("pic");
    }

    private static bool IsLikelySizedIcon(Image image)
    {
        var rect = image.rectTransform.rect;
        var width = Mathf.Abs(rect.width);
        var height = Mathf.Abs(rect.height);
        return width >= 8f
               && height >= 8f
               && width <= 64f
               && height <= 64f;
    }

    private static float GetIconScore(Image image)
    {
        var rect = image.rectTransform.rect;
        var area = Mathf.Abs(rect.width * rect.height);
        var positionPenalty = Mathf.Max(0f, image.rectTransform.anchoredPosition.x) * 4f;

        // Prefer the compact image already occupying the left-side native icon slot.
        return area + positionPenalty;
    }
}
