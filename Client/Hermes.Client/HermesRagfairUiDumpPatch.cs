using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using EFT.UI;
using EFT.UI.Ragfair;
using EFT.UI.Utilities.LightScroller;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Captures the live Flea Market UI templates and their assigned EFT assets.
/// The dump is diagnostic only: it does not replace, clone, or modify any Flea controls.
/// </summary>
internal static class HermesRagfairUiDumper
{
    private const int FullHierarchyDepth = 14;
    private const int ContextHierarchyDepth = 4;
    private const int MaximumObjectsPerTarget = 500;

    private static readonly BindingFlags InstanceFields = BindingFlags.Instance
                                                               | BindingFlags.Public
                                                               | BindingFlags.NonPublic
                                                               | BindingFlags.FlattenHierarchy;

    private static readonly HashSet<int> DumpedTargetRoots = new HashSet<int>();
    private static readonly Dictionary<int, string> SpriteAssets = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> FontAssets = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> Materials = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> AnimatorControllers = new Dictionary<int, string>();

    private static HermesRagfairUiDumpRunner? _runner;
    private static bool _scheduled;
    private static bool _completed;

    internal static string OutputDirectory => Path.Combine(BepInEx.Paths.PluginPath, "HERMES", "logs");
    internal static string OutputPath => Path.Combine(OutputDirectory, "ragfair-ui-dump.txt");

    internal static void Schedule(RagfairScreen? screen, string source)
    {
        if (screen == null || _scheduled || _completed)
        {
            return;
        }

        _scheduled = true;
        EnsureRunner().StartCoroutine(DumpAfterLayout(screen, source));
    }

    private static HermesRagfairUiDumpRunner EnsureRunner()
    {
        if (_runner != null)
        {
            return _runner;
        }

        var runnerObject = new GameObject("HERMES_RagfairUiDumpRunner");
        runnerObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(runnerObject);
        _runner = runnerObject.AddComponent<HermesRagfairUiDumpRunner>();
        return _runner;
    }

    private static IEnumerator DumpAfterLayout(RagfairScreen screen, string source)
    {
        // Awake can occur before the final canvas layout pass. Waiting several frames gives
        // spawned toggles, template clones, and their RectTransforms time to initialize.
        yield return null;
        yield return null;
        yield return new WaitForEndOfFrame();

        try
        {
            if (screen == null || screen.gameObject == null)
            {
                _scheduled = false;
                yield break;
            }

            Canvas.ForceUpdateCanvases();
            if (screen.transform is RectTransform screenRect)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(screenRect);
            }

            WriteDump(screen, source);
            _completed = true;
            Plugin.Log?.LogInfo($"HERMES Ragfair UI dump created: {OutputPath}");
        }
        catch (Exception ex)
        {
            _scheduled = false;
            Plugin.Log?.LogError($"HERMES Ragfair UI dump failed: {ex}");
        }
    }

    private static void WriteDump(RagfairScreen screen, string source)
    {
        DumpedTargetRoots.Clear();
        SpriteAssets.Clear();
        FontAssets.Clear();
        Materials.Clear();
        AnimatorControllers.Clear();

        var builder = new StringBuilder(512 * 1024);
        builder.AppendLine("HERMES RAGFAIR UI ASSET DUMP");
        builder.AppendLine("================================");
        builder.AppendLine($"Generated (local): {DateTime.Now:O}");
        builder.AppendLine($"Generated (UTC):   {DateTime.UtcNow:O}");
        builder.AppendLine($"Capture source:    {source}");
        builder.AppendLine($"Unity version:     {Application.unityVersion}");
        builder.AppendLine($"Screen resolution: {Screen.width} x {Screen.height}");
        builder.AppendLine($"Ragfair object:    {GetHierarchyPath(screen.transform)}");
        builder.AppendLine($"Ragfair active:    self={screen.gameObject.activeSelf}, hierarchy={screen.gameObject.activeInHierarchy}");
        builder.AppendLine();

        DumpTarget(builder, "RagfairScreen (context hierarchy)", screen.gameObject, ContextHierarchyDepth);

        var allOffersToggle = GetFieldValue<UIAnimatedToggleSpawner>(screen, "_allOffersToggle");
        var wishListToggle = GetFieldValue<UIAnimatedToggleSpawner>(screen, "_wishListToggle");
        var myOffersToggle = GetFieldValue<UIAnimatedToggleSpawner>(screen, "_myOffersToggle");
        var addOfferButton = GetFieldValue<DefaultUIButton>(screen, "_addOfferButton");
        var filterButton = GetFieldValue<Button>(screen, "_filterButton");
        var offersListTemplate = GetFieldValue<OfferViewList>(screen, "_offersListTemplate");
        var offersListContainer = GetFieldValue<RectTransform>(screen, "_offersListContainer");

        DumpTarget(builder, "RagfairScreen._allOffersToggle", allOffersToggle);
        DumpSpawnedToggle(builder, "RagfairScreen._allOffersToggle.SpawnedObject", allOffersToggle);
        DumpTarget(builder, "RagfairScreen._wishListToggle", wishListToggle);
        DumpSpawnedToggle(builder, "RagfairScreen._wishListToggle.SpawnedObject", wishListToggle);
        DumpTarget(builder, "RagfairScreen._myOffersToggle", myOffersToggle);
        DumpSpawnedToggle(builder, "RagfairScreen._myOffersToggle.SpawnedObject", myOffersToggle);
        DumpTarget(builder, "RagfairScreen._addOfferButton", addOfferButton);
        DumpTarget(builder, "RagfairScreen._filterButton", filterButton);
        DumpTarget(builder, "RagfairScreen._offersListContainer", offersListContainer);
        DumpTarget(builder, "RagfairScreen._offersListTemplate", offersListTemplate);

        if (offersListTemplate != null)
        {
            DumpOfferViewList(builder, offersListTemplate);
        }

        DumpAssetSummary(builder);

        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(OutputPath, builder.ToString(), new UTF8Encoding(false));

        var timestampedPath = Path.Combine(
            OutputDirectory,
            $"ragfair-ui-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(timestampedPath, builder.ToString(), new UTF8Encoding(false));
    }

    private static void DumpSpawnedToggle(
        StringBuilder builder,
        string label,
        UIAnimatedToggleSpawner? spawner)
    {
        if (spawner == null)
        {
            DumpMissing(builder, label);
            return;
        }

        try
        {
            var spawned = spawner.SpawnedObject;
            DumpTarget(builder, label, spawned);
        }
        catch (Exception ex)
        {
            builder.AppendLine($"=== TARGET: {label} ===");
            builder.AppendLine($"Could not access spawned toggle: {ex.GetType().Name}: {ex.Message}");
            builder.AppendLine();
        }
    }

    private static void DumpOfferViewList(StringBuilder builder, OfferViewList offerViewList)
    {
        var filtersPanel = GetFieldValue<Component>(offerViewList, "_filtersPanel");
        var categoriesPanel = GetFieldValue<RagfairCategoriesPanel>(offerViewList, "_browseCategoriesPanel");
        var cancellableFilters = GetFieldValue<Component>(offerViewList, "_cancellableFiltersPanel");
        var loader = GetFieldValue<GameObject>(offerViewList, "_loader");
        var scroller = GetFieldValue<LightScroller>(offerViewList, "_scroller");
        var cellViewPrefab = GetFieldValue<UIElement>(offerViewList, "_cellViewPrefab");
        var updateDataPrefab = GetFieldValue<UIElement>(offerViewList, "_updateDataPrefab");
        var availabilityWarning = GetFieldValue<Component>(offerViewList, "_availabilityWarningPrefab");
        var notFoundObject = GetFieldValue<GameObject>(offerViewList, "_notFoundObject");
        var notFoundLabel = GetFieldValue<TextMeshProUGUI>(offerViewList, "_notFoundLabel");
        var massPurchasePanel = GetFieldValue<Component>(offerViewList, "_massPurchasePanel");
        var refreshButton = GetFieldValue<Button>(offerViewList, "_refreshButton");

        DumpTarget(builder, "OfferViewList._filtersPanel", filtersPanel);
        DumpTarget(builder, "OfferViewList._browseCategoriesPanel", categoriesPanel);
        DumpTarget(builder, "OfferViewList._cancellableFiltersPanel", cancellableFilters);
        DumpTarget(builder, "OfferViewList._loader", loader);
        DumpTarget(builder, "OfferViewList._scroller", scroller);
        DumpTarget(builder, "OfferViewList._cellViewPrefab", cellViewPrefab);
        DumpTarget(builder, "OfferViewList._updateDataPrefab", updateDataPrefab);
        DumpTarget(builder, "OfferViewList._availabilityWarningPrefab", availabilityWarning);
        DumpTarget(builder, "OfferViewList._notFoundObject", notFoundObject);
        DumpTarget(builder, "OfferViewList._notFoundLabel", notFoundLabel);
        DumpTarget(builder, "OfferViewList._massPurchasePanel", massPurchasePanel);
        DumpTarget(builder, "OfferViewList._refreshButton", refreshButton);

        if (categoriesPanel != null)
        {
            DumpBrowseCategoriesPanel(builder, categoriesPanel);
        }

        if (scroller != null)
        {
            DumpLightScroller(builder, scroller);
        }

        if (cellViewPrefab is OfferView offerView)
        {
            DumpOfferViewFields(builder, offerView);
        }
    }

    private static void DumpBrowseCategoriesPanel(
        StringBuilder builder,
        RagfairCategoriesPanel categoriesPanel)
    {
        DumpTarget(
            builder,
            "BrowseCategoriesPanel.SearchInputField",
            GetFieldValue<TMP_InputField>(categoriesPanel, "SearchInputField"));
        DumpTarget(
            builder,
            "BrowseCategoriesPanel.CombinedCategoryView",
            GetFieldValue<Component>(categoriesPanel, "CombinedCategoryView"));
        DumpTarget(
            builder,
            "BrowseCategoriesPanel.CategoryViewsContainer",
            GetFieldValue<RectTransform>(categoriesPanel, "CategoryViewsContainer"));
        DumpTarget(
            builder,
            "BrowseCategoriesPanel._loader",
            GetFieldValue<GameObject>(categoriesPanel, "_loader"));
        DumpTarget(
            builder,
            "BrowseCategoriesPanel._searchIcon",
            GetFieldValue<GameObject>(categoriesPanel, "_searchIcon"));
    }

    private static void DumpLightScroller(StringBuilder builder, LightScroller scroller)
    {
        DumpTarget(
            builder,
            "LightScroller._targetArea",
            GetFieldValue<RectTransform>(scroller, "_targetArea"));
        DumpTarget(
            builder,
            "LightScroller._scrollbar",
            GetFieldValue<Scrollbar>(scroller, "_scrollbar"));
    }

    private static void DumpOfferViewFields(StringBuilder builder, OfferView offerView)
    {
        var fieldNames = new[]
        {
            "_checkboxPanel",
            "_selectedMark",
            "_minimizeButton",
            "_buttonsContainer",
            "_purchaseButton",
            "_removeButton",
            "_offerId",
            "_exchangeOffer",
            "_merchantInfoView",
            "_merchantCanvasGroup",
            "_descriptionShrunk",
            "_descriptionExpanded",
            "_priceShrunk",
            "_priceExpanded",
            "_lockedButton",
            "_canvasGroup",
            "_hoverTooltipArea",
            "_renewButton",
            "_disabledPanel",
            "_selectedBackground",
            "_notAvailableButton",
            "_outOfStockButton",
            "_expirationTimePanel",
            "_expirationLabel",
            "_createdTimeLabel",
            "_availableTimePanel",
            "_availableTimeLabel",
            "_loader",
            "_timeLeftImage",
            "_borderImage",
            "_backgroundIdImage"
        };

        foreach (var fieldName in fieldNames)
        {
            DumpReflectionTarget(builder, $"OfferView.{fieldName}", offerView, fieldName);
        }

        DumpReflectionValue(builder, "OfferView._backgroundImages", offerView, "_backgroundImages");
        DumpReflectionValue(builder, "OfferView._minimizedSprite", offerView, "_minimizedSprite");
        DumpReflectionValue(builder, "OfferView._expandedSprite", offerView, "_expandedSprite");
        DumpReflectionValue(builder, "OfferView._myIdSprite", offerView, "_myIdSprite");
        DumpReflectionValue(builder, "OfferView._traderIdSprite", offerView, "_traderIdSprite");
    }

    private static void DumpReflectionTarget(
        StringBuilder builder,
        string label,
        object owner,
        string fieldName)
    {
        var value = GetFieldValue(owner, fieldName);
        DumpTarget(builder, label, value);
    }

    private static void DumpReflectionValue(
        StringBuilder builder,
        string label,
        object owner,
        string fieldName)
    {
        builder.AppendLine($"=== VALUE: {label} ===");
        var value = GetFieldValue(owner, fieldName);
        builder.AppendLine(FormatValue(value));
        CollectAssets(value);
        builder.AppendLine();
    }

    private static void DumpTarget(
        StringBuilder builder,
        string label,
        object? target,
        int maximumDepth = FullHierarchyDepth)
    {
        var gameObject = ToGameObject(target);
        if (gameObject == null)
        {
            DumpMissing(builder, label, target);
            return;
        }

        builder.AppendLine($"=== TARGET: {label} ===");
        builder.AppendLine($"Runtime type: {target?.GetType().FullName ?? gameObject.GetType().FullName}");
        builder.AppendLine($"GameObject:   {gameObject.name}");
        builder.AppendLine($"Path:         {GetHierarchyPath(gameObject.transform)}");
        builder.AppendLine($"Instance ID:  {gameObject.GetInstanceID()}");
        builder.AppendLine($"Active:       self={gameObject.activeSelf}, hierarchy={gameObject.activeInHierarchy}");
        builder.AppendLine($"Layer:        {gameObject.layer}");
        builder.AppendLine($"Tag:          {SafeTag(gameObject)}");

        if (!DumpedTargetRoots.Add(gameObject.GetInstanceID()))
        {
            builder.AppendLine("Hierarchy already dumped through another field reference.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("Hierarchy:");
        var objectCount = 0;
        DumpTransform(builder, gameObject.transform, 0, maximumDepth, ref objectCount);
        if (objectCount >= MaximumObjectsPerTarget)
        {
            builder.AppendLine($"[TRUNCATED after {MaximumObjectsPerTarget} objects]");
        }
        builder.AppendLine();
    }

    private static void DumpMissing(StringBuilder builder, string label, object? value = null)
    {
        builder.AppendLine($"=== TARGET: {label} ===");
        builder.AppendLine(value == null
            ? "Reference is null or the field was not found."
            : $"Value is not a GameObject/Component/Transform: {FormatValue(value)}");
        builder.AppendLine();
    }

    private static void DumpTransform(
        StringBuilder builder,
        Transform transform,
        int depth,
        int maximumDepth,
        ref int objectCount)
    {
        if (transform == null || objectCount >= MaximumObjectsPerTarget)
        {
            return;
        }

        objectCount++;
        var indent = new string(' ', depth * 2);
        var gameObject = transform.gameObject;
        builder.Append(indent)
            .Append("- ")
            .Append(gameObject.name)
            .Append(" [")
            .Append(gameObject.activeSelf ? "active" : "inactive")
            .Append(", layer=")
            .Append(gameObject.layer)
            .AppendLine("]");

        var components = gameObject.GetComponents<Component>();
        foreach (var component in components)
        {
            if (component == null)
            {
                builder.AppendLine($"{indent}  * <missing component>");
                continue;
            }

            DumpComponent(builder, component, indent + "  ");
        }

        if (depth >= maximumDepth)
        {
            if (transform.childCount > 0)
            {
                builder.AppendLine($"{indent}  ... {transform.childCount} child object(s) hidden by depth limit");
            }
            return;
        }

        for (var index = 0; index < transform.childCount; index++)
        {
            DumpTransform(builder, transform.GetChild(index), depth + 1, maximumDepth, ref objectCount);
            if (objectCount >= MaximumObjectsPerTarget)
            {
                return;
            }
        }
    }

    private static void DumpComponent(StringBuilder builder, Component component, string indent)
    {
        try
        {
            builder.Append(indent).Append("* ").AppendLine(component.GetType().FullName);

            if (component is RectTransform rectTransform)
            {
                builder.AppendLine($"{indent}  anchors={FormatVector2(rectTransform.anchorMin)} -> {FormatVector2(rectTransform.anchorMax)}");
                builder.AppendLine($"{indent}  pivot={FormatVector2(rectTransform.pivot)}, anchoredPosition={FormatVector2(rectTransform.anchoredPosition)}");
                builder.AppendLine($"{indent}  sizeDelta={FormatVector2(rectTransform.sizeDelta)}, rect={FormatRect(rectTransform.rect)}");
                builder.AppendLine($"{indent}  offsetMin={FormatVector2(rectTransform.offsetMin)}, offsetMax={FormatVector2(rectTransform.offsetMax)}");
                builder.AppendLine($"{indent}  localScale={FormatVector3(rectTransform.localScale)}, localEuler={FormatVector3(rectTransform.localEulerAngles)}");
            }

            if (component is Canvas canvas)
            {
                builder.AppendLine($"{indent}  renderMode={canvas.renderMode}, sortingOrder={canvas.sortingOrder}, overrideSorting={canvas.overrideSorting}");
                builder.AppendLine($"{indent}  sortingLayer={canvas.sortingLayerName}, pixelPerfect={canvas.pixelPerfect}, scaleFactor={FormatFloat(canvas.scaleFactor)}");
            }

            if (component is CanvasGroup canvasGroup)
            {
                builder.AppendLine($"{indent}  alpha={FormatFloat(canvasGroup.alpha)}, interactable={canvasGroup.interactable}, blocksRaycasts={canvasGroup.blocksRaycasts}, ignoreParentGroups={canvasGroup.ignoreParentGroups}");
            }

            if (component is Image image)
            {
                CollectSprite(image.sprite);
                CollectMaterial(image.material);
                builder.AppendLine($"{indent}  sprite={FormatUnityObject(image.sprite)}, type={image.type}, preserveAspect={image.preserveAspect}");
                builder.AppendLine($"{indent}  color={FormatColor(image.color)}, material={FormatUnityObject(image.material)}, raycastTarget={image.raycastTarget}");
                builder.AppendLine($"{indent}  fillMethod={image.fillMethod}, fillAmount={FormatFloat(image.fillAmount)}, fillOrigin={image.fillOrigin}, fillClockwise={image.fillClockwise}");
                builder.AppendLine($"{indent}  pixelsPerUnitMultiplier={FormatFloat(image.pixelsPerUnitMultiplier)}, useSpriteMesh={image.useSpriteMesh}");
            }
            else if (component is RawImage rawImage)
            {
                CollectMaterial(rawImage.material);
                builder.AppendLine($"{indent}  texture={FormatUnityObject(rawImage.texture)}, uvRect={FormatRect(rawImage.uvRect)}");
                builder.AppendLine($"{indent}  color={FormatColor(rawImage.color)}, material={FormatUnityObject(rawImage.material)}, raycastTarget={rawImage.raycastTarget}");
            }

            if (component is TMP_Text text)
            {
                CollectFont(text.font);
                CollectMaterial(text.fontSharedMaterial);
                builder.AppendLine($"{indent}  text=\"{EscapeText(text.text)}\"");
                builder.AppendLine($"{indent}  font={FormatUnityObject(text.font)}, fontMaterial={FormatUnityObject(text.fontSharedMaterial)}");
                builder.AppendLine($"{indent}  fontSize={FormatFloat(text.fontSize)}, style={text.fontStyle}, color={FormatColor(text.color)}");
                builder.AppendLine($"{indent}  alignment={text.alignment}, overflow={text.overflowMode}, wordWrapping={text.enableWordWrapping}");
                builder.AppendLine($"{indent}  characterSpacing={FormatFloat(text.characterSpacing)}, wordSpacing={FormatFloat(text.wordSpacing)}, lineSpacing={FormatFloat(text.lineSpacing)}");
                builder.AppendLine($"{indent}  margin={FormatVector4(text.margin)}, raycastTarget={text.raycastTarget}");
            }

            if (component is TMP_InputField inputField)
            {
                builder.AppendLine($"{indent}  text=\"{EscapeText(inputField.text)}\", interactable={inputField.interactable}, readOnly={inputField.readOnly}");
                builder.AppendLine($"{indent}  contentType={inputField.contentType}, inputType={inputField.inputType}, lineType={inputField.lineType}");
                builder.AppendLine($"{indent}  characterLimit={inputField.characterLimit}, caretWidth={inputField.caretWidth}, customCaretColor={inputField.customCaretColor}");
                builder.AppendLine($"{indent}  textComponent={FormatUnityObject(inputField.textComponent)}, placeholder={FormatUnityObject(inputField.placeholder)}, viewport={FormatUnityObject(inputField.textViewport)}");
                builder.AppendLine($"{indent}  selectionColor={FormatColor(inputField.selectionColor)}, caretColor={FormatColor(inputField.caretColor)}");
            }

            if (component is Selectable selectable)
            {
                DumpSelectable(builder, selectable, indent);
            }

            if (component is Toggle toggle)
            {
                builder.AppendLine($"{indent}  isOn={toggle.isOn}, toggleTransition={toggle.toggleTransition}, group={FormatUnityObject(toggle.group)}, graphic={FormatUnityObject(toggle.graphic)}");
            }

            if (component is Scrollbar scrollbar)
            {
                builder.AppendLine($"{indent}  direction={scrollbar.direction}, value={FormatFloat(scrollbar.value)}, size={FormatFloat(scrollbar.size)}, steps={scrollbar.numberOfSteps}");
                builder.AppendLine($"{indent}  handleRect={FormatUnityObject(scrollbar.handleRect)}");
            }

            if (component is ScrollRect scrollRect)
            {
                builder.AppendLine($"{indent}  content={FormatUnityObject(scrollRect.content)}, viewport={FormatUnityObject(scrollRect.viewport)}");
                builder.AppendLine($"{indent}  horizontal={scrollRect.horizontal}, vertical={scrollRect.vertical}, movement={scrollRect.movementType}, inertia={scrollRect.inertia}");
                builder.AppendLine($"{indent}  elasticity={FormatFloat(scrollRect.elasticity)}, deceleration={FormatFloat(scrollRect.decelerationRate)}, sensitivity={FormatFloat(scrollRect.scrollSensitivity)}");
                builder.AppendLine($"{indent}  horizontalScrollbar={FormatUnityObject(scrollRect.horizontalScrollbar)}, verticalScrollbar={FormatUnityObject(scrollRect.verticalScrollbar)}");
            }

            if (component is LayoutElement layoutElement)
            {
                builder.AppendLine($"{indent}  ignoreLayout={layoutElement.ignoreLayout}, min=({FormatFloat(layoutElement.minWidth)}, {FormatFloat(layoutElement.minHeight)})");
                builder.AppendLine($"{indent}  preferred=({FormatFloat(layoutElement.preferredWidth)}, {FormatFloat(layoutElement.preferredHeight)}), flexible=({FormatFloat(layoutElement.flexibleWidth)}, {FormatFloat(layoutElement.flexibleHeight)}), priority={layoutElement.layoutPriority}");
            }

            if (component is HorizontalOrVerticalLayoutGroup layoutGroup)
            {
                builder.AppendLine($"{indent}  padding={FormatRectOffset(layoutGroup.padding)}, spacing={FormatFloat(layoutGroup.spacing)}, alignment={layoutGroup.childAlignment}");
                builder.AppendLine($"{indent}  controlWidth={layoutGroup.childControlWidth}, controlHeight={layoutGroup.childControlHeight}, forceWidth={layoutGroup.childForceExpandWidth}, forceHeight={layoutGroup.childForceExpandHeight}");
                builder.AppendLine($"{indent}  useScaleWidth={layoutGroup.childScaleWidth}, useScaleHeight={layoutGroup.childScaleHeight}, reverseArrangement={layoutGroup.reverseArrangement}");
            }

            if (component is GridLayoutGroup gridLayout)
            {
                builder.AppendLine($"{indent}  padding={FormatRectOffset(gridLayout.padding)}, spacing={FormatVector2(gridLayout.spacing)}, cellSize={FormatVector2(gridLayout.cellSize)}");
                builder.AppendLine($"{indent}  startCorner={gridLayout.startCorner}, startAxis={gridLayout.startAxis}, alignment={gridLayout.childAlignment}, constraint={gridLayout.constraint}, count={gridLayout.constraintCount}");
            }

            if (component is ContentSizeFitter sizeFitter)
            {
                builder.AppendLine($"{indent}  horizontalFit={sizeFitter.horizontalFit}, verticalFit={sizeFitter.verticalFit}");
            }

            if (component is AspectRatioFitter aspectRatioFitter)
            {
                builder.AppendLine($"{indent}  aspectMode={aspectRatioFitter.aspectMode}, aspectRatio={FormatFloat(aspectRatioFitter.aspectRatio)}");
            }

            if (component is Mask mask)
            {
                builder.AppendLine($"{indent}  showMaskGraphic={mask.showMaskGraphic}, graphic={FormatUnityObject(mask.graphic)}");
            }

            if (component is RectMask2D rectMask)
            {
                builder.AppendLine($"{indent}  padding={FormatVector4(rectMask.padding)}, softness={rectMask.softness}");
            }

            if (component is Shadow shadow)
            {
                builder.AppendLine($"{indent}  effectColor={FormatColor(shadow.effectColor)}, effectDistance={FormatVector2(shadow.effectDistance)}, useGraphicAlpha={shadow.useGraphicAlpha}");
            }

            if (component is Animator animator)
            {
                CollectAnimatorController(animator.runtimeAnimatorController);
                builder.AppendLine($"{indent}  enabled={animator.enabled}, active={animator.isActiveAndEnabled}, controller={FormatUnityObject(animator.runtimeAnimatorController)}");
                builder.AppendLine($"{indent}  updateMode={animator.updateMode}, cullingMode={animator.cullingMode}, speed={FormatFloat(animator.speed)}, layers={animator.layerCount}");
                if (animator.runtimeAnimatorController != null)
                {
                    var parameters = animator.parameters
                        .Select(parameter => $"{parameter.name}:{parameter.type}")
                        .ToArray();
                    builder.AppendLine($"{indent}  parameters=[{string.Join(", ", parameters)}]");
                }
            }

            DumpSerializedFields(builder, component, indent + "  ");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"{indent}  ! component dump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DumpSelectable(StringBuilder builder, Selectable selectable, string indent)
    {
        builder.AppendLine($"{indent}  interactable={selectable.interactable}, transition={selectable.transition}, targetGraphic={FormatUnityObject(selectable.targetGraphic)}");
        builder.AppendLine($"{indent}  navigation={selectable.navigation.mode}");

        var colors = selectable.colors;
        builder.AppendLine($"{indent}  colors.normal={FormatColor(colors.normalColor)}, highlighted={FormatColor(colors.highlightedColor)}, pressed={FormatColor(colors.pressedColor)}");
        builder.AppendLine($"{indent}  colors.selected={FormatColor(colors.selectedColor)}, disabled={FormatColor(colors.disabledColor)}, multiplier={FormatFloat(colors.colorMultiplier)}, fade={FormatFloat(colors.fadeDuration)}");

        var sprites = selectable.spriteState;
        CollectSprite(sprites.highlightedSprite);
        CollectSprite(sprites.pressedSprite);
        CollectSprite(sprites.selectedSprite);
        CollectSprite(sprites.disabledSprite);
        builder.AppendLine($"{indent}  sprites.highlighted={FormatUnityObject(sprites.highlightedSprite)}, pressed={FormatUnityObject(sprites.pressedSprite)}");
        builder.AppendLine($"{indent}  sprites.selected={FormatUnityObject(sprites.selectedSprite)}, disabled={FormatUnityObject(sprites.disabledSprite)}");

        var triggers = selectable.animationTriggers;
        builder.AppendLine($"{indent}  triggers.normal={triggers.normalTrigger}, highlighted={triggers.highlightedTrigger}, pressed={triggers.pressedTrigger}, selected={triggers.selectedTrigger}, disabled={triggers.disabledTrigger}");
    }

    private static void DumpSerializedFields(StringBuilder builder, Component component, string indent)
    {
        var fields = EnumerateFields(component.GetType())
            .Where(field => !field.IsStatic)
            .Where(field => field.IsPublic || field.GetCustomAttribute<SerializeField>() != null)
            .Where(field => !field.IsDefined(typeof(CompilerGeneratedAttribute), true))
            .OrderBy(field => field.DeclaringType?.FullName)
            .ThenBy(field => field.Name)
            .ToArray();

        if (fields.Length == 0)
        {
            return;
        }

        builder.AppendLine($"{indent}serialized fields:");
        foreach (var field in fields)
        {
            try
            {
                var value = field.GetValue(component);
                CollectAssets(value);
                builder.AppendLine($"{indent}  {field.DeclaringType?.Name}.{field.Name} = {FormatValue(value)}");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"{indent}  {field.Name} = <error: {ex.GetType().Name}: {ex.Message}>");
            }
        }
    }

    private static IEnumerable<FieldInfo> EnumerateFields(Type type)
    {
        for (var current = type;
             current != null
             && current != typeof(MonoBehaviour)
             && current != typeof(Behaviour)
             && current != typeof(Component)
             && current != typeof(UnityEngine.Object);
             current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                         BindingFlags.Instance
                         | BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    private static void DumpAssetSummary(StringBuilder builder)
    {
        builder.AppendLine("ASSET SUMMARY");
        builder.AppendLine("=============");

        builder.AppendLine($"Sprites ({SpriteAssets.Count}):");
        foreach (var sprite in SpriteAssets.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("- " + sprite);
        }
        builder.AppendLine();

        builder.AppendLine($"TMP fonts ({FontAssets.Count}):");
        foreach (var font in FontAssets.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("- " + font);
        }
        builder.AppendLine();

        builder.AppendLine($"Materials ({Materials.Count}):");
        foreach (var material in Materials.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("- " + material);
        }
        builder.AppendLine();

        builder.AppendLine($"Animator controllers ({AnimatorControllers.Count}):");
        foreach (var controller in AnimatorControllers.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("- " + controller);
        }
        builder.AppendLine();
    }

    private static void CollectAssets(object? value)
    {
        if (value == null)
        {
            return;
        }

        if (value is Sprite sprite)
        {
            CollectSprite(sprite);
            return;
        }

        if (value is TMP_FontAsset font)
        {
            CollectFont(font);
            return;
        }

        if (value is Material material)
        {
            CollectMaterial(material);
            return;
        }

        if (value is RuntimeAnimatorController controller)
        {
            CollectAnimatorController(controller);
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var count = 0;
            foreach (var item in enumerable)
            {
                CollectAssets(item);
                count++;
                if (count >= 128)
                {
                    break;
                }
            }
        }
    }

    private static void CollectSprite(Sprite? sprite)
    {
        if (sprite == null || SpriteAssets.ContainsKey(sprite.GetInstanceID()))
        {
            return;
        }

        var texture = sprite.texture;
        var textureName = texture != null ? texture.name : "<null>";
        SpriteAssets[sprite.GetInstanceID()] =
            $"{sprite.name} | texture={textureName} | rect={FormatRect(sprite.rect)} | border={FormatVector4(sprite.border)} | pivot={FormatVector2(sprite.pivot)} | ppu={FormatFloat(sprite.pixelsPerUnit)}";
    }

    private static void CollectFont(TMP_FontAsset? font)
    {
        if (font == null || FontAssets.ContainsKey(font.GetInstanceID()))
        {
            return;
        }

        FontAssets[font.GetInstanceID()] =
            $"{font.name} | material={FormatUnityObject(font.material)} | atlas={FormatUnityObject(font.atlasTexture)}";
        CollectMaterial(font.material);
    }

    private static void CollectMaterial(Material? material)
    {
        if (material == null || Materials.ContainsKey(material.GetInstanceID()))
        {
            return;
        }

        Materials[material.GetInstanceID()] =
            $"{material.name} | shader={FormatUnityObject(material.shader)} | renderQueue={material.renderQueue}";
    }

    private static void CollectAnimatorController(RuntimeAnimatorController? controller)
    {
        if (controller == null || AnimatorControllers.ContainsKey(controller.GetInstanceID()))
        {
            return;
        }

        var clips = controller.animationClips
            .Where(clip => clip != null)
            .Select(clip => clip.name)
            .Distinct()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AnimatorControllers[controller.GetInstanceID()] =
            $"{controller.name} | clips=[{string.Join(", ", clips)}]";
    }

    private static object? GetFieldValue(object owner, string fieldName)
    {
        var field = FindField(owner.GetType(), fieldName);
        return field?.GetValue(owner);
    }

    private static T? GetFieldValue<T>(object owner, string fieldName) where T : class
    {
        return GetFieldValue(owner, fieldName) as T;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(
                fieldName,
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field;
            }
        }

        return type.GetField(fieldName, InstanceFields);
    }

    private static GameObject? ToGameObject(object? target)
    {
        return target switch
        {
            GameObject gameObject => gameObject,
            Transform transform => transform.gameObject,
            Component component => component.gameObject,
            _ => null
        };
    }

    private static string FormatValue(object? value)
    {
        if (value == null)
        {
            return "<null>";
        }

        if (value is UnityEngine.Object unityObject)
        {
            return FormatUnityObject(unityObject);
        }

        if (value is string text)
        {
            return "\"" + EscapeText(text) + "\"";
        }

        if (value is Color color)
        {
            return FormatColor(color);
        }

        if (value is Color32 color32)
        {
            return $"rgba32({color32.r}, {color32.g}, {color32.b}, {color32.a})";
        }

        if (value is Vector2 vector2)
        {
            return FormatVector2(vector2);
        }

        if (value is Vector3 vector3)
        {
            return FormatVector3(vector3);
        }

        if (value is Vector4 vector4)
        {
            return FormatVector4(vector4);
        }

        if (value is Rect rect)
        {
            return FormatRect(rect);
        }

        if (value is RectOffset rectOffset)
        {
            return FormatRectOffset(rectOffset);
        }

        if (value is IEnumerable enumerable)
        {
            var values = new List<string>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count < 24)
                {
                    values.Add(FormatValue(item));
                }
                count++;
            }

            var suffix = count > values.Count ? $", ... ({count} total)" : string.Empty;
            return "[" + string.Join(", ", values) + suffix + "]";
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty;
        }

        return value.ToString() ?? value.GetType().FullName ?? "<unknown>";
    }

    private static string FormatUnityObject(UnityEngine.Object? unityObject)
    {
        if (unityObject == null)
        {
            return "<null>";
        }

        var path = unityObject switch
        {
            GameObject gameObject => GetHierarchyPath(gameObject.transform),
            Component component => GetHierarchyPath(component.transform),
            _ => unityObject.name
        };

        return $"{unityObject.GetType().Name}(\"{unityObject.name}\", id={unityObject.GetInstanceID()}, path=\"{path}\")";
    }

    private static string GetHierarchyPath(Transform? transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        var names = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static string SafeTag(GameObject gameObject)
    {
        try
        {
            return gameObject.tag;
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string EscapeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var escaped = text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            .Replace("\"", "\\\"");
        return escaped.Length <= 300 ? escaped : escaped.Substring(0, 300) + "...";
    }

    private static string FormatFloat(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatVector2(Vector2 value)
        => $"({FormatFloat(value.x)}, {FormatFloat(value.y)})";

    private static string FormatVector3(Vector3 value)
        => $"({FormatFloat(value.x)}, {FormatFloat(value.y)}, {FormatFloat(value.z)})";

    private static string FormatVector4(Vector4 value)
        => $"({FormatFloat(value.x)}, {FormatFloat(value.y)}, {FormatFloat(value.z)}, {FormatFloat(value.w)})";

    private static string FormatRect(Rect value)
        => $"(x={FormatFloat(value.x)}, y={FormatFloat(value.y)}, w={FormatFloat(value.width)}, h={FormatFloat(value.height)})";

    private static string FormatRectOffset(RectOffset? value)
        => value == null
            ? "<null>"
            : $"(left={value.left}, right={value.right}, top={value.top}, bottom={value.bottom})";

    private static string FormatColor(Color value)
        => $"rgba({FormatFloat(value.r)}, {FormatFloat(value.g)}, {FormatFloat(value.b)}, {FormatFloat(value.a)}) / #{ColorUtility.ToHtmlStringRGBA(value)}";
}

internal sealed class HermesRagfairUiDumpRunner : MonoBehaviour
{
}

/// <summary>
/// Primary hook. RagfairScreen.Awake creates the OfferViewList clones and accesses
/// the top toggle SpawnedObject properties, so all important prefab references exist
/// by the postfix.
/// </summary>
internal sealed class HermesRagfairUiDumpAwakePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(RagfairScreen).GetMethod(
                   "Awake",
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(RagfairScreen).FullName, "Awake");
    }

    [PatchPostfix]
    private static void Postfix(RagfairScreen __instance)
    {
        HermesRagfairUiDumper.Schedule(__instance, "RagfairScreen.Awake postfix");
    }
}

/// <summary>
/// Fallback hook for installations where the RagfairScreen instance awakened before
/// HERMES enabled its patches. The first six-argument Show method is the main Flea
/// Market screen entry point in SPT 4.0.13.
/// </summary>
internal sealed class HermesRagfairUiDumpShowPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(RagfairScreen)
                   .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .FirstOrDefault(method => method.Name == "Show" && method.GetParameters().Length == 6)
               ?? throw new MissingMethodException(typeof(RagfairScreen).FullName, "Show(6 parameters)");
    }

    [PatchPostfix]
    private static void Postfix(RagfairScreen __instance)
    {
        HermesRagfairUiDumper.Schedule(__instance, "RagfairScreen.Show postfix");
    }
}
