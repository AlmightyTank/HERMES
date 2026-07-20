using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

internal sealed partial class HermesNativeWorkspaceBody
{
    #region Native element helpers

    private RectTransform CreateVerticalRoot(RectTransform parent)
    {
        var root = new GameObject("VerticalWorkspace", typeof(RectTransform), typeof(VerticalLayoutGroup));
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        HermesNativeUiFramework.Stretch(rect, 0f, 0f, 0f, 0f);
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return rect;
    }

    private static RectTransform CreatePanel(Transform parent, string name, Color color)
    {
        var panel = HermesNativeUiFramework.CreatePanel(name, parent, color);
        var image = panel.GetComponent<Image>();
        image.raycastTarget = true;
        return panel;
    }

    private static VerticalLayoutGroup AddVerticalLayout(RectTransform panel, int left, int right, int top, int bottom, float spacing)
    {
        var layout = panel.gameObject.GetComponent<VerticalLayoutGroup>() ?? panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(left, right, top, bottom);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    private void AddStatusStrip(
        Transform parent,
        string status,
        bool loading,
        Action refresh,
        string refreshLabel = "REFRESH")
    {
        var strip = CreatePanel(parent, "StatusStrip", new Color(0f, 0f, 0f, 0.22f));
        var layout = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(9, 6, 3, 3);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var stripElement = strip.gameObject.AddComponent<LayoutElement>();
        stripElement.minHeight = CompactToolbarHeight;
        stripElement.preferredHeight = CompactToolbarHeight;
        stripElement.flexibleHeight = 0f;
        var label = AddText(strip, string.IsNullOrWhiteSpace(status) ? "CURRENT PROFILE DATA" : status, 12.5f, false, HermesNativeUiFramework.MutedTextColor);
        label.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        if (loading)
        {
            AddText(strip, "WORKING", 11.5f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Center, 76f);
        }
        AddButton(strip, refreshLabel, () => refresh(), 76f, !loading);
        AddAnchoredBottomRule(strip);
    }

    private static RectTransform CreateToolbar(Transform parent)
    {
        var toolbar = new GameObject("Toolbar", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        toolbar.transform.SetParent(parent, false);
        var rect = (RectTransform)toolbar.transform;
        var layout = toolbar.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(2, 2, 2, 2);
        layout.spacing = 5f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var element = toolbar.GetComponent<LayoutElement>();
        element.minHeight = CompactToolbarHeight;
        element.preferredHeight = CompactToolbarHeight;
        element.flexibleHeight = 0f;
        return rect;
    }

    private static void AddToolbarLabel(Transform parent, string text)
    {
        AddText(parent, text, 13f, true, HermesNativeUiFramework.AccentTextColor, TextAlignmentOptions.Left, 145f);
    }

    private static void AddFlexibleSpace(Transform parent)
    {
        var spacer = new GameObject("FlexibleSpace", typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(parent, false);
        var spacerElement = spacer.GetComponent<LayoutElement>();
        spacerElement.flexibleWidth = 1f;
        spacerElement.flexibleHeight = 0f;
    }

    private static TMP_InputField AddInput(Transform parent, string placeholderText, string value, float preferredWidth)
    {
        var root = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.SearchBorderSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        image.raycastTarget = true;
        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = 120f;
        layout.preferredWidth = preferredWidth;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;

        var viewport = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(root.transform, false);
        var viewportRect = (RectTransform)viewport.transform;
        HermesNativeUiFramework.Stretch(viewportRect, 12f, 5f, 12f, 5f);

        var text = HermesNativeUiFramework.CreateText("Text", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.color = HermesNativeUiFramework.NormalTextColor;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Masking;

        var placeholder = HermesNativeUiFramework.CreateText("Placeholder", viewport.transform, 14f, false, TextAlignmentOptions.Left);
        HermesNativeUiFramework.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.text = placeholderText;
        placeholder.color = HermesNativeUiFramework.MutedTextColor;
        placeholder.enableWordWrapping = false;

        var input = root.GetComponent<TMP_InputField>();
        input.targetGraphic = image;
        input.textViewport = viewportRect;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 160;
        input.SetTextWithoutNotify(value ?? string.Empty);
        return input;
    }

    private static string NormalizeTagColor(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "blue" : value.Trim();
        return TagColorOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized.ToLowerInvariant()
            : "blue";
    }

    private static string TagColorLabel(string? value)
        => TagColorOptions.First(option => string.Equals(option.Value, NormalizeTagColor(value), StringComparison.OrdinalIgnoreCase)).Label;

    private static float AssistantActionButtonWidth(string label)
    {
        var normalized = string.IsNullOrWhiteSpace(label) ? "OPEN" : label.Trim();
        return Mathf.Clamp(68f + normalized.Length * 5.8f, 96f, 210f);
    }

    private static string BuildHideoutRequirementPreview(HermesHideoutAreaSummary area)
    {
        if (!area.TargetLevel.HasValue)
        {
            return "No further upgrade requirements.";
        }

        if (area.RequiredItems.Count == 0)
        {
            return area.Status.Contains("progression", StringComparison.OrdinalIgnoreCase)
                ? "No item materials • blocked by a non-item requirement."
                : "No item materials required for the next level.";
        }

        var items = area.RequiredItems
            .OrderBy(requirement => requirement.IsMet)
            .ThenByDescending(requirement => requirement.Missing)
            .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .Select(requirement =>
            {
                var owned = FormatCount(requirement.Owned);
                var required = FormatCount(requirement.Required);
                var fir = requirement.FoundInRaidRequired ? " FIR" : string.Empty;
                return requirement.IsMet
                    ? $"• {requirement.Name}: {owned}/{required}{fir} ✓"
                    : $"• {requirement.Name}: {owned}/{required}{fir} — missing {FormatCount(requirement.Missing)}";
            });

        return "Required items for next upgrade:\n" + string.Join("\n", items);
    }

    private static Toggle AddCheckbox(
        Transform parent,
        string text,
        bool value,
        UnityAction<bool> action,
        float width)
    {
        var root = new GameObject(
            "Checkbox",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Toggle),
            typeof(LayoutElement),
            typeof(HorizontalLayoutGroup));
        root.transform.SetParent(parent, false);

        var hitArea = root.GetComponent<Image>();
        hitArea.color = new Color(0f, 0f, 0f, 0f);
        hitArea.raycastTarget = true;

        var layout = root.GetComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = CompactControlHeight;
        layout.preferredHeight = CompactControlHeight;
        layout.flexibleHeight = 0f;

        var row = root.GetComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(6, 6, 4, 4);
        row.spacing = 7f;
        row.childAlignment = TextAnchor.MiddleLeft;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = false;

        var style = ResolveNativeFleaCheckboxStyle();
        var box = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        box.transform.SetParent(root.transform, false);
        var boxImage = box.GetComponent<Image>();
        boxImage.sprite = style?.BackgroundSprite;
        boxImage.type = style?.BackgroundType ?? Image.Type.Simple;
        boxImage.preserveAspect = style?.BackgroundPreserveAspect ?? false;
        boxImage.color = style?.BackgroundColor ?? new Color(0.16f, 0.17f, 0.16f, 0.95f);
        boxImage.raycastTarget = false;
        var boxLayout = box.GetComponent<LayoutElement>();
        boxLayout.minWidth = 18f;
        boxLayout.preferredWidth = 18f;
        boxLayout.minHeight = 18f;
        boxLayout.preferredHeight = 18f;
        boxLayout.flexibleWidth = 0f;
        boxLayout.flexibleHeight = 0f;

        Graphic? checkGraphic = null;
        GameObject? fallbackCheckmark = null;
        if (style?.CheckmarkSprite != null)
        {
            var check = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            check.transform.SetParent(box.transform, false);
            var checkImage = check.GetComponent<Image>();
            checkImage.sprite = style.CheckmarkSprite;
            checkImage.type = style.CheckmarkType;
            checkImage.preserveAspect = style.CheckmarkPreserveAspect;
            checkImage.color = style.CheckmarkColor;
            checkImage.raycastTarget = false;
            HermesNativeUiFramework.Stretch((RectTransform)check.transform, 0f, 0f, 0f, 0f);
            checkGraphic = checkImage;
        }
        else
        {
            fallbackCheckmark = CreateFallbackCheckmark(box.transform);
            fallbackCheckmark.SetActive(value);
        }

        var label = HermesNativeUiFramework.CreateText("Label", root.transform, 12.5f, true, TextAlignmentOptions.Left);
        label.text = text;
        label.color = HermesNativeUiFramework.NormalTextColor;
        label.raycastTarget = false;
        var labelLayout = label.gameObject.AddComponent<LayoutElement>();
        labelLayout.flexibleWidth = 1f;
        labelLayout.minHeight = 18f;

        var toggle = root.GetComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkGraphic;
        toggle.transition = Selectable.Transition.ColorTint;
        if (style != null)
        {
            toggle.colors = style.ToggleColors;
        }

        toggle.SetIsOnWithoutNotify(value);
        if (checkGraphic != null)
        {
            checkGraphic.canvasRenderer.SetAlpha(value ? 1f : 0f);
        }

        if (fallbackCheckmark != null)
        {
            toggle.onValueChanged.AddListener(isOn => fallbackCheckmark.SetActive(isOn));
        }

        toggle.onValueChanged.AddListener(action);
        return toggle;
    }

    private static NativeFleaCheckboxStyle? ResolveNativeFleaCheckboxStyle()
    {
        if (_nativeFleaCheckboxStyle != null)
        {
            return _nativeFleaCheckboxStyle;
        }

        Toggle? best = null;
        Image? bestBackground = null;
        Image? bestCheckmark = null;
        var bestScore = int.MinValue;

        foreach (var candidate in Resources.FindObjectsOfTypeAll<Toggle>())
        {
            if (candidate == null || candidate.gameObject == null)
            {
                continue;
            }

            var path = BuildHierarchyPath(candidate.transform);
            if (path.IndexOf("HERMES", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            if (candidate.targetGraphic is not Image background
                || candidate.graphic is not Image checkmark
                || ReferenceEquals(background, checkmark)
                || background.sprite == null
                || checkmark.sprite == null)
            {
                continue;
            }

            var lowerPath = path.ToLowerInvariant();
            var lowerName = candidate.name.ToLowerInvariant();
            var backgroundName = background.sprite.name?.ToLowerInvariant() ?? string.Empty;
            var checkmarkName = checkmark.sprite.name?.ToLowerInvariant() ?? string.Empty;
            var graphicName = checkmark.name.ToLowerInvariant();

            var score = 0;
            if (lowerPath.Contains("ragfair")) score += 320;
            if (lowerPath.Contains("filter")) score += 260;
            if (lowerPath.Contains("popup") || lowerPath.Contains("window")) score += 80;
            if (lowerName.Contains("checkbox")) score += 220;
            if (lowerName.Contains("check")) score += 100;
            if (lowerName.Contains("filter")) score += 70;
            if (backgroundName.Contains("checkbox")) score += 180;
            if (backgroundName.Contains("filter")) score += 50;
            if (checkmarkName.Contains("check")) score += 220;
            if (graphicName.Contains("check")) score += 180;

            var rect = candidate.transform as RectTransform;
            if (rect != null && rect.rect.width <= 64f && rect.rect.height <= 64f)
            {
                score += 25;
            }

            if (score <= bestScore)
            {
                continue;
            }

            best = candidate;
            bestBackground = background;
            bestCheckmark = checkmark;
            bestScore = score;
        }

        if (best == null || bestBackground == null || bestCheckmark == null || bestScore < 350)
        {
            if (!_nativeFleaCheckboxProbeLogged)
            {
                _nativeFleaCheckboxProbeLogged = true;
                Plugin.Log?.LogWarning(
                    "HERMES could not resolve the native Ragfair filter checkbox sprites yet; using the visible fallback checkmark.");
            }

            return null;
        }

        _nativeFleaCheckboxStyle = new NativeFleaCheckboxStyle
        {
            BackgroundSprite = bestBackground.sprite,
            BackgroundColor = bestBackground.color,
            BackgroundType = bestBackground.type,
            BackgroundPreserveAspect = bestBackground.preserveAspect,
            CheckmarkSprite = bestCheckmark.sprite,
            CheckmarkColor = bestCheckmark.color,
            CheckmarkType = bestCheckmark.type,
            CheckmarkPreserveAspect = bestCheckmark.preserveAspect,
            ToggleColors = best.colors
        };

        Plugin.Log?.LogInfo(
            $"HERMES captured native Ragfair filter checkbox '{BuildHierarchyPath(best.transform)}' "
            + $"with checkmark sprite '{bestCheckmark.sprite.name}'.");
        return _nativeFleaCheckboxStyle;
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        var names = new Stack<string>();
        var current = transform;
        var depth = 0;
        while (current != null && depth++ < 16)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static GameObject CreateFallbackCheckmark(Transform parent)
    {
        var root = new GameObject("FallbackCheckmark", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        HermesNativeUiFramework.Stretch((RectTransform)root.transform, 1f, 1f, 1f, 1f);

        AddFallbackCheckLine(root.transform, "ShortStroke", new Vector2(-3.2f, -1.4f), new Vector2(2.5f, 7f), -42f);
        AddFallbackCheckLine(root.transform, "LongStroke", new Vector2(2.1f, 1.3f), new Vector2(2.5f, 11f), 43f);
        return root;
    }

    private static void AddFallbackCheckLine(
        Transform parent,
        string name,
        Vector2 anchoredPosition,
        Vector2 size,
        float rotation)
    {
        var line = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        line.transform.SetParent(parent, false);
        var rect = (RectTransform)line.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        rect.localEulerAngles = new Vector3(0f, 0f, rotation);
        var image = line.GetComponent<Image>();
        image.color = HermesNativeUiFramework.AccentTextColor;
        image.raycastTarget = false;
    }

    private sealed class NativeFleaCheckboxStyle
    {
        public Sprite? BackgroundSprite { get; set; }
        public Color BackgroundColor { get; set; }
        public Image.Type BackgroundType { get; set; }
        public bool BackgroundPreserveAspect { get; set; }
        public Sprite? CheckmarkSprite { get; set; }
        public Color CheckmarkColor { get; set; }
        public Image.Type CheckmarkType { get; set; }
        public bool CheckmarkPreserveAspect { get; set; }
        public ColorBlock ToggleColors { get; set; }
    }

    private static Button AddButton(
        Transform parent,
        string text,
        UnityAction action,
        float width,
        bool interactable = true,
        bool selected = false,
        float height = CompactControlHeight,
        float fontSize = 12.5f)
    {
        var root = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.ButtonBackgroundSprite;
        image.type = image.sprite != null && image.sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        image.color = selected ? new Color(0.78f, 0.77f, 0.70f, 0.95f) : new Color(0.24f, 0.25f, 0.24f, 0.82f);
        image.raycastTarget = true;
        var button = root.GetComponent<Button>();
        button.targetGraphic = image;
        button.interactable = interactable;
        button.transition = Selectable.Transition.ColorTint;
        button.onClick.AddListener(action);
        var colors = button.colors;
        colors.normalColor = image.color;
        colors.highlightedColor = selected ? new Color(0.90f, 0.89f, 0.82f, 1f) : new Color(0.37f, 0.38f, 0.36f, 1f);
        colors.pressedColor = new Color(0.16f, 0.17f, 0.16f, 1f);
        colors.disabledColor = new Color(0.12f, 0.13f, 0.13f, 0.55f);
        button.colors = colors;
        var layout = root.GetComponent<LayoutElement>();
        HermesNativeUiFramework.SetScalableButtonSize(
            layout,
            defaultPreferredWidth: width,
            defaultMinWidth: width,
            defaultPreferredHeight: height,
            defaultMinHeight: height,
            maxPreferredHeight: Math.Max(height * HermesNativeUiFramework.DefaultButtonMaxWidthScale, height + 8f),
            maxMinHeight: Math.Max(height * HermesNativeUiFramework.DefaultButtonMaxWidthScale, height + 8f));
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;
        var label = HermesNativeUiFramework.CreateText("Label", root.transform, fontSize, true, TextAlignmentOptions.Center);
        label.text = text;
        label.color = selected ? new Color32(26, 28, 27, 255) : HermesNativeUiFramework.NormalTextColor;
        HermesNativeUiFramework.Stretch(label.rectTransform, 7f, 2f, 7f, 2f);
        return button;
    }

    private static TMP_Text AddText(
        Transform parent,
        string text,
        float size,
        bool bold,
        Color color,
        TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        float? preferredWidth = null)
    {
        var label = HermesNativeUiFramework.CreateText("Text", parent, size, bold, alignment);
        label.text = text ?? string.Empty;
        label.color = color;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (preferredWidth.HasValue)
        {
            layout.minWidth = preferredWidth.Value;
            layout.preferredWidth = preferredWidth.Value;
        }
        else
        {
            layout.flexibleWidth = 1f;
        }
        layout.minHeight = Math.Max(18f, HermesNativeUiFramework.ScaleFontSize(size) + 5f);
        layout.flexibleHeight = 0f;
        var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return label;
    }

    private static RectTransform AddCard(
        Transform parent,
        string title,
        string body,
        string meta,
        Action? onClick = null,
        Color? color = null,
        Action? onRightClick = null)
    {
        var types = onClick is null && onRightClick is null
            ? new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) }
            : new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement) };
        var root = new GameObject("Card", types);
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        var image = root.GetComponent<Image>();
        image.color = color ?? HermesNativeUiFramework.RowColor;
        image.raycastTarget = onClick is not null || onRightClick is not null;
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(9, 9, 6, 6);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var cardElement = root.GetComponent<LayoutElement>();
        cardElement.minHeight = 48f;
        cardElement.flexibleHeight = 0f;
        if (onClick is not null)
        {
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            button.onClick.AddListener(() => onClick());
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(image.color.r + 0.08f, image.color.g + 0.08f, image.color.b + 0.08f, Math.Max(0.88f, image.color.a));
            colors.pressedColor = new Color(0.12f, 0.13f, 0.13f, 0.92f);
            button.colors = colors;
        }
        if (onRightClick is not null)
        {
            var relay = root.AddComponent<RightClickRelay>();
            relay.Initialize(onRightClick);
        }

        AddText(root.transform, title, 15f, true, HermesNativeUiFramework.NormalTextColor);
        if (!string.IsNullOrWhiteSpace(body))
        {
            AddText(root.transform, body, 13f, false, HermesNativeUiFramework.NormalTextColor);
        }
        if (!string.IsNullOrWhiteSpace(meta))
        {
            AddText(root.transform, meta, 11.5f, false, HermesNativeUiFramework.MutedTextColor);
        }
        AddBottomSeparator(root.transform);
        return rect;
    }

    private sealed class RightClickRelay : MonoBehaviour, IPointerClickHandler
    {
        private Action? _onRightClick;

        public void Initialize(Action onRightClick)
        {
            _onRightClick = onRightClick;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                _onRightClick?.Invoke();
            }
        }
    }

    private static void AddSectionHeader(Transform parent, string title)
    {
        HermesNativeUiFramework.CreateSectionHeader(parent, title, CompactSectionHeight);
    }

    private static void AddEmptyState(Transform parent, string title, string detail)
    {
        var card = AddCard(parent, title, detail, "");
        card.GetComponent<Image>().color = new Color(0.02f, 0.025f, 0.025f, 0.42f);
    }

    private static void AddMetricGrid(Transform parent, params (string Label, string Value)[] metrics)
    {
        if (metrics.Length == 0)
        {
            return;
        }

        var root = new GameObject("MetricGrid", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var rootLayout = root.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = 3f;
        rootLayout.childAlignment = TextAnchor.UpperLeft;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var rootElement = root.GetComponent<LayoutElement>();
        rootElement.minHeight = CompactMetricHeight;
        rootElement.flexibleHeight = 0f;

        var columns = metrics.Length switch
        {
            <= 4 => metrics.Length,
            5 or 6 => 3,
            _ => 4
        };
        for (var offset = 0; offset < metrics.Length; offset += columns)
        {
            var row = new GameObject("MetricRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(root.transform, false);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 3f;
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            var rowElement = row.GetComponent<LayoutElement>();
            rowElement.minHeight = CompactMetricHeight;
            rowElement.preferredHeight = CompactMetricHeight;
            rowElement.flexibleHeight = 0f;

            var count = Math.Min(columns, metrics.Length - offset);
            for (var index = 0; index < count; index++)
            {
                var metric = metrics[offset + index];
                var cell = HermesNativeUiFramework.CreatePanel("Metric", row.transform, HermesNativeUiFramework.RowColor);
                var cellElement = cell.gameObject.AddComponent<LayoutElement>();
                cellElement.minWidth = 145f;
                cellElement.preferredWidth = 210f;
                cellElement.flexibleWidth = 1f;
                cellElement.minHeight = CompactMetricHeight;
                cellElement.preferredHeight = CompactMetricHeight;
                cellElement.flexibleHeight = 0f;
                var cellLayout = cell.gameObject.AddComponent<VerticalLayoutGroup>();
                cellLayout.padding = new RectOffset(8, 8, 4, 4);
                cellLayout.spacing = 0f;
                cellLayout.childAlignment = TextAnchor.MiddleLeft;
                cellLayout.childControlWidth = true;
                cellLayout.childControlHeight = true;
                cellLayout.childForceExpandWidth = true;
                cellLayout.childForceExpandHeight = false;
                AddText(cell, metric.Label, 10f, true, HermesNativeUiFramework.MutedTextColor);
                AddText(cell, metric.Value, 13.5f, true, HermesNativeUiFramework.NormalTextColor);
            }

            for (var index = count; index < columns; index++)
            {
                var spacer = new GameObject("MetricSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacer.transform.SetParent(row.transform, false);
                var spacerElement = spacer.GetComponent<LayoutElement>();
                spacerElement.minWidth = 145f;
                spacerElement.preferredWidth = 210f;
                spacerElement.flexibleWidth = 1f;
                spacerElement.minHeight = CompactMetricHeight;
                spacerElement.preferredHeight = CompactMetricHeight;
                spacerElement.flexibleHeight = 0f;
            }
        }
    }

    private (RectTransform Root, RectTransform Content, ScrollRect Scroll) CreateScroll(Transform parent, string key, bool flexibleHeight)
    {
        var scroll = HermesNativeUiFramework.CreateScrollView(parent, key);
        var layout = scroll.Root.gameObject.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.flexibleHeight = flexibleHeight ? 1f : 0f;
        layout.minHeight = flexibleHeight ? 120f : 72f;
        _activeScrolls[key] = scroll.ScrollRect;
        return (scroll.Root, scroll.Content, scroll.ScrollRect);
    }

    private static void AddAnchoredBottomRule(RectTransform parent)
    {
        var separator = new GameObject("BottomRule", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var rect = (RectTransform)separator.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = new Vector2(0f, 1f);
        var layout = separator.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
    }

    private static void AddBottomSeparator(Transform parent)
    {
        var separator = new GameObject("Separator", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        separator.transform.SetParent(parent, false);
        var image = separator.GetComponent<Image>();
        image.color = HermesNativeUiFramework.SeparatorColor;
        image.raycastTarget = false;
        separator.GetComponent<LayoutElement>().preferredHeight = 1f;
        separator.GetComponent<LayoutElement>().minHeight = 1f;
    }

    private void CaptureScrollPositions()
    {
        foreach (var pair in _activeScrolls)
        {
            if (pair.Value != null)
            {
                _savedScrollPositions[pair.Key] = pair.Value.verticalNormalizedPosition;
            }
        }
    }

    private string BuildItemResultSetKey()
    {
        if (_state is null)
        {
            return string.Empty;
        }

        var results = _state.SearchResults;
        return string.Join("|",
            _state.SearchQuery.Trim(),
            _state.SearchLoading,
            RuntimeHelpers.GetHashCode(results),
            results.Count);
    }

    private IEnumerator RestoreScrollPositionsNextFrame()
    {
        var forceTop = new HashSet<string>(_scrollsToForceTop, StringComparer.Ordinal);
        _scrollsToForceTop.Clear();

        yield return null;
        Canvas.ForceUpdateCanvases();
        RestoreScrollPositions(forceTop);

        // ContentSizeFitter and ScrollRect can perform one more layout pass after the first
        // canvas update. Reassert forced-top lists once more so a new item search never lands
        // at the bottom because the prior empty result view reported a normalized value of 0.
        if (forceTop.Count > 0)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            RestoreScrollPositions(forceTop);
        }
    }

    private void RestoreScrollPositions(HashSet<string> forceTop)
    {
        foreach (var pair in _activeScrolls)
        {
            var scroll = pair.Value;
            if (scroll == null)
            {
                continue;
            }

            var shouldForceTop = forceTop.Contains(pair.Key);
            if (!shouldForceTop && !_savedScrollPositions.TryGetValue(pair.Key, out _))
            {
                continue;
            }

            var position = shouldForceTop ? 1f : Mathf.Clamp01(_savedScrollPositions[pair.Key]);
            scroll.StopMovement();
            scroll.velocity = Vector2.zero;
            scroll.verticalNormalizedPosition = position;

            if (shouldForceTop && scroll.content != null)
            {
                var anchored = scroll.content.anchoredPosition;
                scroll.content.anchoredPosition = new Vector2(anchored.x, 0f);
                _savedScrollPositions[pair.Key] = 1f;
            }
        }
    }

    private void Invalidate(float delay = 0f)
    {
        _forceRebuild = true;
        _nextSyncAt = Time.unscaledTime + Math.Max(0f, delay);
    }

    private static string Money(long? amount) => amount.HasValue ? $"₽{amount.Value:N0}" : "N/A";
    private static string FormatCount(double value) => Math.Abs(value - Math.Round(value)) < 0.0001d ? Math.Round(value).ToString("N0") : value.ToString("0.##");
    private static string YesNo(bool value) => value ? "COVERED" : "MISSING";

    private static string FormatUnixTime(long unixTime)
    {
        if (unixTime <= 0)
        {
            return "time unknown";
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime.ToString("g");
    }

    private static int ActionSecondsRemaining(HermesActionProposal proposal)
        => (int)Math.Max(0L, proposal.ExpiresUnixTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static string FormatCurrency(long amount, string currency)
        => currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1d)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }
        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
        if (duration.TotalMinutes >= 1d)
        {
            return $"{(int)duration.TotalMinutes}m";
        }
        return $"{Math.Max(1, duration.Seconds)}s";
    }

    #endregion
}
