using System.Reflection;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Captures a small set of visual-only Ragfair templates and keeps private inactive
/// copies alive for HERMES. No Ragfair controller, offer, trader, or purchase state is reused.
/// </summary>
internal static class HermesRagfairNativeAssets
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly object Sync = new();
    private static GameObject? _cacheRoot;

    internal static GameObject? AnimatedToggleTemplate { get; private set; }
    internal static GameObject? DefaultButtonTemplate { get; private set; }
    internal static GameObject? SearchFieldTemplate { get; private set; }
    internal static GameObject? OfferRowTemplate { get; private set; }
    internal static Sprite? ButtonBackgroundSprite { get; private set; }
    internal static Sprite? SearchBorderSprite { get; private set; }
    internal static Sprite? SpinnerSprite { get; private set; }
    internal static TMP_FontAsset? NormalFont { get; private set; }
    internal static TMP_FontAsset? ShadowedFont { get; private set; }

    internal static bool Ready => AnimatedToggleTemplate != null
                                  && DefaultButtonTemplate != null
                                  && SearchFieldTemplate != null;

    internal static bool TryResolve()
    {
        if (Ready)
        {
            return true;
        }

        lock (Sync)
        {
            if (Ready)
            {
                return true;
            }

            var screens = Resources.FindObjectsOfTypeAll<RagfairScreen>()
                .Where(screen => screen != null)
                .OrderByDescending(screen => screen.gameObject.scene.IsValid())
                .ToArray();

            foreach (var screen in screens)
            {
                if (Capture(screen))
                {
                    return true;
                }
            }

            CaptureLooseResources();
            return Ready;
        }
    }

    internal static bool Capture(RagfairScreen screen)
    {
        if (screen == null)
        {
            return Ready;
        }

        lock (Sync)
        {
            EnsureCacheRoot();

            try
            {
                if (AnimatedToggleTemplate == null)
                {
                    var spawner = ReadField<UIAnimatedToggleSpawner>(screen, "_allOffersToggle");
                    var spawned = spawner?.SpawnedObject;
                    if (spawned != null)
                    {
                        AnimatedToggleTemplate = CacheTemplate(spawned.gameObject, "HERMES_Ragfair_AnimatedToggle_Template");
                    }
                }

                if (DefaultButtonTemplate == null)
                {
                    var button = ReadField<DefaultUIButton>(screen, "_addOfferButton");
                    if (button != null)
                    {
                        DefaultButtonTemplate = CacheTemplate(button.gameObject, "HERMES_Ragfair_DefaultButton_Template");
                    }
                }

                var listTemplate = ReadField<OfferViewList>(screen, "_offersListTemplate")
                                   ?? Resources.FindObjectsOfTypeAll<OfferViewList>()
                                       .FirstOrDefault(view => view != null && view.name == "OfferViewList");

                if (listTemplate != null)
                {
                    if (OfferRowTemplate == null)
                    {
                        var row = ReadField<UIElement>(listTemplate, "_cellViewPrefab");
                        if (row != null)
                        {
                            OfferRowTemplate = CacheTemplate(row.gameObject, "HERMES_Ragfair_OfferRow_Template");
                        }
                    }

                    if (SearchFieldTemplate == null)
                    {
                        var categories = ReadField<RagfairCategoriesPanel>(listTemplate, "_browseCategoriesPanel");
                        var searchField = categories == null
                            ? null
                            : ReadFieldRecursive<TMP_InputField>(categories, "SearchInputField");
                        if (searchField != null)
                        {
                            SearchFieldTemplate = CacheTemplate(searchField.gameObject, "HERMES_Ragfair_SearchField_Template");
                        }
                    }
                }

                CaptureNamedResources();
                CaptureFontsFromTemplates();

                if (Ready)
                {
                    Plugin.Log?.LogInfo("HERMES captured native Ragfair toggle, button, and search-field templates.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"HERMES native Ragfair asset capture was incomplete: {ex.Message}");
            }

            return Ready;
        }
    }

    private static void CaptureLooseResources()
    {
        EnsureCacheRoot();

        if (AnimatedToggleTemplate == null)
        {
            var spawner = Resources.FindObjectsOfTypeAll<UIAnimatedToggleSpawner>()
                .FirstOrDefault(value => value != null && value.name.Contains("BrowseToggle", StringComparison.OrdinalIgnoreCase));
            if (spawner?.SpawnedObject != null)
            {
                AnimatedToggleTemplate = CacheTemplate(spawner.SpawnedObject.gameObject, "HERMES_Ragfair_AnimatedToggle_Template");
            }
        }

        if (DefaultButtonTemplate == null)
        {
            var button = Resources.FindObjectsOfTypeAll<DefaultUIButton>()
                .FirstOrDefault(value => value != null && value.name.Contains("AddOffer", StringComparison.OrdinalIgnoreCase));
            if (button != null)
            {
                DefaultButtonTemplate = CacheTemplate(button.gameObject, "HERMES_Ragfair_DefaultButton_Template");
            }
        }

        if (SearchFieldTemplate == null)
        {
            var field = Resources.FindObjectsOfTypeAll<TMP_InputField>()
                .FirstOrDefault(value => value != null
                                         && value.name == "SearchInputField"
                                         && value.GetComponent<Image>()?.sprite?.name == "currency_border");
            if (field != null)
            {
                SearchFieldTemplate = CacheTemplate(field.gameObject, "HERMES_Ragfair_SearchField_Template");
            }
        }

        if (OfferRowTemplate == null)
        {
            var row = Resources.FindObjectsOfTypeAll<OfferView>()
                .FirstOrDefault(value => value != null && value.name == "RagfairOffer");
            if (row != null)
            {
                OfferRowTemplate = CacheTemplate(row.gameObject, "HERMES_Ragfair_OfferRow_Template");
            }
        }

        CaptureNamedResources();
        CaptureFontsFromTemplates();
    }

    private static void CaptureNamedResources()
    {
        var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        ButtonBackgroundSprite ??= sprites.FirstOrDefault(sprite => sprite != null && sprite.name == "button_back_big");
        SearchBorderSprite ??= sprites.FirstOrDefault(sprite => sprite != null && sprite.name == "currency_border");
        SpinnerSprite ??= sprites.FirstOrDefault(sprite => sprite != null && sprite.name == "spinner_big");

        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        NormalFont ??= fonts.FirstOrDefault(font => font != null && font.name == "Jovanny Lemonad - Bender Normal SDF");
        ShadowedFont ??= fonts.FirstOrDefault(font => font != null && font.name == "Jovanny Lemonad - Bender Shadowed SDF");
    }

    private static void CaptureFontsFromTemplates()
    {
        foreach (var template in new[] { AnimatedToggleTemplate, DefaultButtonTemplate, SearchFieldTemplate, OfferRowTemplate })
        {
            if (template == null)
            {
                continue;
            }

            foreach (var text in template.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text?.font == null)
                {
                    continue;
                }

                if (NormalFont == null && text.font.name.Contains("Bender Normal", StringComparison.OrdinalIgnoreCase))
                {
                    NormalFont = text.font;
                }

                if (ShadowedFont == null && text.font.name.Contains("Bender Shadowed", StringComparison.OrdinalIgnoreCase))
                {
                    ShadowedFont = text.font;
                }
            }
        }
    }

    private static GameObject CacheTemplate(GameObject source, string name)
    {
        EnsureCacheRoot();
        var clone = UnityEngine.Object.Instantiate(source, _cacheRoot!.transform, false);
        clone.name = name;
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.SetActive(false);
        return clone;
    }

    private static void EnsureCacheRoot()
    {
        if (_cacheRoot != null)
        {
            return;
        }

        _cacheRoot = new GameObject("HERMES_RagfairNativeAssetCache");
        _cacheRoot.hideFlags = HideFlags.HideAndDontSave;
        _cacheRoot.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_cacheRoot);
    }

    private static T? ReadField<T>(object owner, string name) where T : class
    {
        return owner.GetType().GetField(name, InstanceFields)?.GetValue(owner) as T;
    }

    private static T? ReadFieldRecursive<T>(object owner, string name) where T : class
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, InstanceFields);
            if (field?.GetValue(owner) is T value)
            {
                return value;
            }
        }

        return null;
    }
}

internal sealed class HermesRagfairNativeAssetPatch : ModulePatch
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
        HermesRagfairNativeAssets.Capture(__instance);
    }
}
