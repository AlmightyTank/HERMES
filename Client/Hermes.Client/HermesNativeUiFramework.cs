using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Shared native-uGUI building blocks for the complete HERMES shell and workspace bodies.
/// These primitives are used for every visible control; the legacy IMGUI controller
/// methods remain data/request owners only and are no longer part of the render path.
/// </summary>
internal static class HermesNativeUiFramework
{
    internal static readonly Color PanelColor = new(0f, 0f, 0f, 0.255f);
    internal static readonly Color RowColor = new(0.032f, 0.043f, 0.045f, 0.62f);
    internal static readonly Color RowAlternateColor = new(0.025f, 0.035f, 0.037f, 0.55f);
    internal static readonly Color SeparatorColor = new(0.13f, 0.16f, 0.16f, 0.72f);
    internal static readonly Color HeaderColor = new(0f, 0f, 0f, 0.38f);
    internal static readonly Color AccentTextColor = new Color32(197, 195, 178, 255);
    internal static readonly Color NormalTextColor = new Color32(224, 226, 216, 255);
    internal static readonly Color MutedTextColor = new Color32(137, 145, 142, 255);

    internal static void NormalizeClonedControl(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        root.hideFlags = HideFlags.None;
        root.SetActive(true);

        foreach (var transform in root.GetComponentsInChildren<Transform>(true))
        {
            var scale = transform.localScale;
            if (!ApproximatelyOne(scale.x) || !ApproximatelyOne(scale.y) || !ApproximatelyOne(scale.z))
            {
                transform.localScale = Vector3.one;
            }
        }

        foreach (var canvasGroup in root.GetComponentsInChildren<CanvasGroup>(true))
        {
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }

        foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            SetBooleanFieldIfPresent(behaviour, "_unavailable", false);
        }
    }

    internal static void AddWorkspaceRowChrome(GameObject toggleObject, int rowIndex)
    {
        if (toggleObject == null || toggleObject.transform.Find("HERMES_RowChrome") != null)
        {
            return;
        }

        var chrome = new GameObject(
            "HERMES_RowChrome",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        chrome.transform.SetParent(toggleObject.transform, false);
        chrome.transform.SetSiblingIndex(0);

        var chromeRect = (RectTransform)chrome.transform;
        Stretch(chromeRect, 0f, 0f, 0f, 0f);
        var image = chrome.GetComponent<Image>();
        image.color = rowIndex % 2 == 0 ? RowColor : RowAlternateColor;
        image.raycastTarget = false;

        var separator = new GameObject(
            "Separator",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        separator.transform.SetParent(chrome.transform, false);
        var separatorRect = (RectTransform)separator.transform;
        separatorRect.anchorMin = new Vector2(0f, 0f);
        separatorRect.anchorMax = new Vector2(1f, 0f);
        separatorRect.pivot = new Vector2(0.5f, 0f);
        separatorRect.offsetMin = Vector2.zero;
        separatorRect.offsetMax = new Vector2(0f, 1f);
        var separatorImage = separator.GetComponent<Image>();
        separatorImage.color = SeparatorColor;
        separatorImage.raycastTarget = false;
    }

    internal static RectTransform CreateStatusBadge(
        Transform parent,
        string name,
        out TMP_Text label,
        out Image spinner)
    {
        var badge = new GameObject(
            name,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement),
            typeof(HorizontalLayoutGroup));
        badge.transform.SetParent(parent, false);

        var rect = (RectTransform)badge.transform;
        var background = badge.GetComponent<Image>();
        background.color = HeaderColor;
        background.raycastTarget = false;

        var layoutElement = badge.GetComponent<LayoutElement>();
        layoutElement.minWidth = 112f;
        layoutElement.preferredWidth = 112f;
        layoutElement.minHeight = 32f;
        layoutElement.preferredHeight = 32f;
        layoutElement.flexibleWidth = 0f;

        var layout = badge.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(9, 9, 5, 5);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var spinnerObject = new GameObject(
            "Spinner",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement),
            typeof(HermesNativeSpinner));
        spinnerObject.transform.SetParent(badge.transform, false);
        var spinnerLayout = spinnerObject.GetComponent<LayoutElement>();
        spinnerLayout.minWidth = 18f;
        spinnerLayout.preferredWidth = 18f;
        spinnerLayout.minHeight = 18f;
        spinnerLayout.preferredHeight = 18f;
        spinner = spinnerObject.GetComponent<Image>();
        spinner.sprite = HermesRagfairNativeAssets.SpinnerSprite;
        spinner.preserveAspect = true;
        spinner.raycastTarget = false;

        label = CreateText("Label", badge.transform, 12f, true, TextAlignmentOptions.Center);
        label.text = "WORKING";
        label.color = AccentTextColor;
        var labelLayout = label.gameObject.AddComponent<LayoutElement>();
        labelLayout.minWidth = 64f;
        labelLayout.preferredWidth = 64f;
        labelLayout.minHeight = 20f;
        labelLayout.preferredHeight = 20f;

        return rect;
    }

    internal static RectTransform CreateSectionHeader(Transform parent, string title, float preferredHeight = 42f)
    {
        var header = new GameObject(
            "SectionHeader",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(LayoutElement));
        header.transform.SetParent(parent, false);
        header.GetComponent<Image>().color = HeaderColor;
        var layout = header.GetComponent<LayoutElement>();
        layout.minHeight = preferredHeight;
        layout.preferredHeight = preferredHeight;
        layout.flexibleHeight = 0f;

        var text = CreateText("Title", header.transform, 16f, true, TextAlignmentOptions.Left);
        text.text = title.ToUpperInvariant();
        text.color = AccentTextColor;
        Stretch(text.rectTransform, 12f, 5f, 12f, 5f);
        return (RectTransform)header.transform;
    }

    internal static RectTransform CreateSummaryStrip(Transform parent, string name = "SummaryStrip")
    {
        var strip = new GameObject(
            name,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(HorizontalLayoutGroup),
            typeof(LayoutElement));
        strip.transform.SetParent(parent, false);
        strip.GetComponent<Image>().color = PanelColor;
        var layout = strip.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 4, 4);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var stripLayout = strip.GetComponent<LayoutElement>();
        stripLayout.minHeight = 48f;
        stripLayout.preferredHeight = 48f;
        stripLayout.flexibleHeight = 0f;
        return (RectTransform)strip.transform;
    }

    internal static (RectTransform Root, RectTransform Left, RectTransform Right) CreateSplitView(
        Transform parent,
        float leftPreferredWidth = 430f)
    {
        var root = new GameObject(
            "SplitView",
            typeof(RectTransform),
            typeof(HorizontalLayoutGroup));
        root.transform.SetParent(parent, false);
        var layout = root.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 7f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        var left = CreatePanel("Left", root.transform, PanelColor);
        var leftLayout = left.gameObject.AddComponent<LayoutElement>();
        leftLayout.minWidth = leftPreferredWidth;
        leftLayout.preferredWidth = leftPreferredWidth;
        leftLayout.flexibleWidth = 0f;
        leftLayout.flexibleHeight = 1f;

        var right = CreatePanel("Right", root.transform, PanelColor);
        var rightLayout = right.gameObject.AddComponent<LayoutElement>();
        rightLayout.flexibleWidth = 1f;
        rightLayout.flexibleHeight = 1f;
        return ((RectTransform)root.transform, left, right);
    }

    internal static (RectTransform Root, RectTransform Viewport, RectTransform Content, ScrollRect ScrollRect) CreateScrollView(
        Transform parent,
        string name = "ScrollView")
    {
        var root = new GameObject(
            name,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(ScrollRect));
        root.transform.SetParent(parent, false);
        root.GetComponent<Image>().color = Color.clear;

        var viewportObject = new GameObject(
            "Viewport",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(RectMask2D));
        viewportObject.transform.SetParent(root.transform, false);
        var viewport = (RectTransform)viewportObject.transform;
        Stretch(viewport, 0f, 0f, 15f, 0f);
        var viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = Color.clear;
        viewportImage.raycastTarget = true;

        var contentObject = new GameObject(
            "Content",
            typeof(RectTransform),
            typeof(VerticalLayoutGroup),
            typeof(ContentSizeFitter));
        contentObject.transform.SetParent(viewport, false);
        var content = (RectTransform)contentObject.transform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = Vector2.zero;
        content.offsetMax = Vector2.zero;
        var contentLayout = contentObject.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 2f;
        contentLayout.childAlignment = TextAnchor.UpperLeft;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scrollbarObject = new GameObject(
            "VerticalScrollbar",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Scrollbar));
        scrollbarObject.transform.SetParent(root.transform, false);
        var scrollbarRect = (RectTransform)scrollbarObject.transform;
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = new Vector2(1f, 1f);
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.offsetMin = new Vector2(-11f, 3f);
        scrollbarRect.offsetMax = new Vector2(-3f, -3f);

        var trackImage = scrollbarObject.GetComponent<Image>();
        trackImage.color = new Color(0.07f, 0.085f, 0.085f, 0.74f);
        trackImage.raycastTarget = true;

        var slidingAreaObject = new GameObject("SlidingArea", typeof(RectTransform));
        slidingAreaObject.transform.SetParent(scrollbarObject.transform, false);
        var slidingArea = (RectTransform)slidingAreaObject.transform;
        Stretch(slidingArea, 1f, 2f, 1f, 2f);

        var handleObject = new GameObject(
            "Handle",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        handleObject.transform.SetParent(slidingArea, false);
        var handle = (RectTransform)handleObject.transform;
        Stretch(handle, 0f, 0f, 0f, 0f);
        var handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(
            AccentTextColor.r,
            AccentTextColor.g,
            AccentTextColor.b,
            0.72f);
        handleImage.raycastTarget = true;

        var scrollbar = scrollbarObject.GetComponent<Scrollbar>();
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };

        var scrollRect = root.GetComponent<ScrollRect>();
        scrollRect.content = content;
        scrollRect.viewport = viewport;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = false;
        scrollRect.scrollSensitivity = 36f;
        scrollRect.verticalScrollbar = scrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scrollRect.verticalScrollbarSpacing = 3f;
        return ((RectTransform)root.transform, viewport, content, scrollRect);
    }

    internal static TMP_Text CreateText(
        string name,
        Transform parent,
        float size,
        bool bold,
        TextAlignmentOptions alignment)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        gameObject.transform.SetParent(parent, false);
        var text = gameObject.GetComponent<TextMeshProUGUI>();
        // Body text uses the normal Bender asset even when bold. The shadowed asset has a
        // baked offset intended for large EFT headers; at body sizes it pushes the readable
        // glyph too far into the dark underlay and makes inputs/section labels look sunken.
        var font = HermesRagfairNativeAssets.NormalFont ?? HermesRagfairNativeAssets.ShadowedFont;
        font ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(candidate => candidate != null);
        if (font != null)
        {
            text.font = font;
        }
        text.fontSize = size;
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.alignment = alignment;
        text.margin = new Vector4(3f, 0f, 3f, 0f);
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    internal static RectTransform CreatePanel(string name, Transform parent, Color color)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(parent, false);
        var image = panel.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = color.a > 0.001f;
        return (RectTransform)panel.transform;
    }

    internal static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static bool ApproximatelyOne(float value) => Mathf.Abs(value - 1f) < 0.001f;

    private static void SetBooleanFieldIfPresent(object owner, string fieldName, bool value)
    {
        var type = owner.GetType();
        while (type != null)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.FieldType == typeof(bool))
            {
                try
                {
                    field.SetValue(owner, value);
                }
                catch
                {
                    // Visual normalization is best effort; the public Interactable setters run afterward.
                }
                return;
            }
            type = type.BaseType;
        }
    }
}

internal sealed class HermesNativeSpinner : MonoBehaviour
{
    [SerializeField]
    private float _degreesPerSecond = 180f;

    private void Update()
    {
        transform.Rotate(0f, 0f, -_degreesPerSecond * Time.unscaledDeltaTime);
    }
}
