using System;
using System.Linq;
using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Corrects the native HERMES inventory-tab icon and selected-tab depth while
/// preserving the original EFT Tab as the clickable control.
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
            Plugin.Log?.LogError($"HERMES could not correct the native tab presentation: {ex}");
        }
    }

    private static void Apply(global::Tab? hermesTab)
    {
        if (hermesTab == null)
        {
            return;
        }

        RemoveAddedOverlay(hermesTab.transform);

        var normalVersion = NormalVersionField?.GetValue(hermesTab) as GameObject;
        var selectedVersion = SelectedVersionField?.GetValue(hermesTab) as GameObject;

        var sprite = HermesIconService.AskHermesIcon;
        if (sprite != null)
        {
            var normalReplaced = ReplaceStateIcon(normalVersion, sprite, Color.white);
            var selectedReplaced = ReplaceStateIcon(selectedVersion, sprite, Color.black);

            if (!normalReplaced && !selectedReplaced)
            {
                Plugin.Log?.LogWarning(
                    "HERMES removed the overlapping tab icon, but could not locate the cloned Achievements icon slot to replace.");
            }
            else
            {
                Plugin.Log?.LogDebug(
                    $"HERMES native tab icon corrected. Normal white: {normalReplaced}; selected inverted: {selectedReplaced}.");
            }
        }
        else
        {
            Plugin.Log?.LogWarning(
                "HERMES could not replace the native tab icon because the embedded icon was unavailable.");
        }

        if (selectedVersion == null)
        {
            Plugin.Log?.LogWarning(
                "HERMES could not apply the selected-tab forward fix because the selected state was unavailable.");
            return;
        }

        var referenceTab = FindPreviousNativeTab(hermesTab);
        var referenceSelected = referenceTab == null
            ? null
            : SelectedVersionField?.GetValue(referenceTab) as GameObject;

        RemoveLegacyRootCanvas(hermesTab, referenceTab);
        EnsureTransparentClickTarget(hermesTab);

        var controller = hermesTab.gameObject.GetComponent<HermesSelectedTabForwardController>()
                         ?? hermesTab.gameObject.AddComponent<HermesSelectedTabForwardController>();
        controller.Initialize(selectedVersion, referenceSelected);

        Plugin.Log?.LogDebug(
            $"HERMES selected tab renders forward without replacing the native tab raycast path. Reference found: {referenceSelected != null}.");
    }

    private static global::Tab? FindPreviousNativeTab(global::Tab hermesTab)
    {
        var parent = hermesTab.transform.parent;
        if (parent == null)
        {
            return null;
        }

        for (var index = hermesTab.transform.GetSiblingIndex() - 1; index >= 0; index--)
        {
            var candidate = parent.GetChild(index).GetComponent<global::Tab>();
            if (candidate != null && candidate != hermesTab)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Earlier builds placed a Canvas on the whole HERMES tab. That made the
    /// overlap work, but detached the cloned Tab from the native parent raycaster.
    /// Keep any Canvas that is genuinely part of the EFT template; remove only the
    /// HERMES-only root Canvas/raycaster pair.
    /// </summary>
    private static void RemoveLegacyRootCanvas(global::Tab hermesTab, global::Tab? referenceTab)
    {
        var rootCanvas = hermesTab.GetComponent<Canvas>();
        var referenceCanvas = referenceTab?.GetComponent<Canvas>();
        if (rootCanvas != null && referenceCanvas == null)
        {
            var raycaster = hermesTab.GetComponent<GraphicRaycaster>();
            if (raycaster != null)
            {
                UnityEngine.Object.Destroy(raycaster);
            }

            UnityEngine.Object.Destroy(rootCanvas);
        }
    }

    /// <summary>
    /// Provides a full-rect native raycast graphic. Pointer events bubble to the
    /// original EFT Tab component on the parent, so its existing selection callback,
    /// hover state, sounds, and controller remain authoritative.
    /// </summary>
    private static void EnsureTransparentClickTarget(global::Tab hermesTab)
    {
        const string targetName = "HERMES_ClickTarget";
        var existing = hermesTab.transform.Find(targetName);
        GameObject targetObject;
        if (existing != null)
        {
            targetObject = existing.gameObject;
        }
        else
        {
            targetObject = new GameObject(targetName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            targetObject.transform.SetParent(hermesTab.transform, false);
        }

        var rect = (RectTransform)targetObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;
        rect.localScale = Vector3.one;
        rect.SetAsLastSibling();

        var image = targetObject.GetComponent<Image>() ?? targetObject.AddComponent<Image>();
        image.sprite = null;
        image.type = Image.Type.Simple;
        image.color = new Color(1f, 1f, 1f, 0.001f);
        image.raycastTarget = true;
        targetObject.SetActive(true);
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

        return area + positionPenalty;
    }
}

/// <summary>
/// Applies forward rendering only to the selected-state visual. The cloned EFT
/// Tab root stays under the original parent Canvas and therefore keeps native
/// click, hover, and selection handling.
/// </summary>
internal sealed class HermesSelectedTabForwardController : MonoBehaviour
{
    private GameObject? _selectedVersion;
    private RectTransform? _selectedRect;
    private RectTransform? _referenceSelectedRect;
    private Canvas? _selectedCanvas;
    private Canvas? _parentCanvas;
    private bool _lastSelected;
    private bool _initialized;

    public void Initialize(GameObject selectedVersion, GameObject? referenceSelectedVersion)
    {
        _selectedVersion = selectedVersion;
        _selectedRect = selectedVersion.transform as RectTransform;
        _referenceSelectedRect = referenceSelectedVersion?.transform as RectTransform;
        _parentCanvas = transform.parent?.GetComponentInParent<Canvas>();
        _selectedCanvas = selectedVersion.GetComponent<Canvas>()
                          ?? selectedVersion.AddComponent<Canvas>();

        // This selected-state Canvas is visual only. The parent Tab remains the
        // clickable control through HERMES_ClickTarget and the native raycaster.
        foreach (var graphic in selectedVersion.GetComponentsInChildren<Graphic>(true))
        {
            graphic.raycastTarget = false;
        }

        var selectedRaycaster = selectedVersion.GetComponent<GraphicRaycaster>();
        if (selectedRaycaster != null)
        {
            UnityEngine.Object.Destroy(selectedRaycaster);
        }

        _initialized = true;
        ApplyForwardGeometry();
        RefreshDepth(force: true);
    }

    private void OnEnable()
    {
        if (_initialized)
        {
            ApplyForwardGeometry();
            RefreshDepth(force: true);
        }
    }

    private void LateUpdate()
    {
        if (_initialized)
        {
            RefreshDepth(force: false);
        }
    }

    private void RefreshDepth(bool force)
    {
        if (_selectedVersion == null || _selectedCanvas == null)
        {
            return;
        }

        var selected = _selectedVersion.activeInHierarchy;
        if (!force && selected == _lastSelected)
        {
            return;
        }

        _lastSelected = selected;
        if (!selected)
        {
            _selectedCanvas.overrideSorting = false;
            return;
        }

        ApplyForwardGeometry();
        _parentCanvas ??= transform.parent?.GetComponentInParent<Canvas>();
        _selectedCanvas.overrideSorting = true;
        if (_parentCanvas != null)
        {
            _selectedCanvas.sortingLayerID = _parentCanvas.sortingLayerID;
            _selectedCanvas.sortingOrder = _parentCanvas.sortingOrder + 50;
        }
        else
        {
            _selectedCanvas.sortingOrder = 50;
        }
    }

    private void ApplyForwardGeometry()
    {
        if (_selectedRect == null || _referenceSelectedRect == null)
        {
            return;
        }

        _selectedRect.anchorMin = _referenceSelectedRect.anchorMin;
        _selectedRect.anchorMax = _referenceSelectedRect.anchorMax;
        _selectedRect.pivot = _referenceSelectedRect.pivot;
        _selectedRect.anchoredPosition = _referenceSelectedRect.anchoredPosition;
        _selectedRect.sizeDelta = _referenceSelectedRect.sizeDelta;
        _selectedRect.localRotation = _referenceSelectedRect.localRotation;
        _selectedRect.localScale = _referenceSelectedRect.localScale;
    }
}
