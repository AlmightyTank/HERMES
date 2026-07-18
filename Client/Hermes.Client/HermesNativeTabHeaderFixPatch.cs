using System;
using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Keeps the external HERMES inventory tab neutral until HERMES is actually
/// selected. Placement and geometry remain owned by HermesNativeScreenHost;
/// this patch only repairs the cloned Tab's normal/selected visibility.
/// </summary>
internal sealed class HermesNativeTabHeaderFixPatch : ModulePatch
{
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
            if (__result == null)
            {
                return;
            }

            var controller = __result.gameObject.GetComponent<HermesNativeTabHeaderStateController>()
                             ?? __result.gameObject.AddComponent<HermesNativeTabHeaderStateController>();
            controller.Initialize(__result);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES could not initialize the native tab header-state fix: {ex}");
        }
    }
}

/// <summary>
/// Synchronizes only the HERMES Tab's normal and selected state objects. Earlier
/// builds also copied Prestige geometry every frame; that moved the inactive state
/// outside the strip and caused the selected state to cover EFT's Tasks tab.
/// </summary>
internal sealed class HermesNativeTabHeaderStateController : MonoBehaviour
{
    private static readonly BindingFlags InstanceFields = BindingFlags.Instance
                                                           | BindingFlags.Public
                                                           | BindingFlags.NonPublic
                                                           | BindingFlags.FlattenHierarchy;

    private static readonly FieldInfo? NormalVersionField =
        typeof(global::Tab).GetField("_normalVersion", InstanceFields);

    private static readonly FieldInfo? SelectedVersionField =
        typeof(global::Tab).GetField("_selectedVersion", InstanceFields);

    private static readonly FieldInfo? ShowingHermesField =
        typeof(HermesNativeScreenHost).GetField("_showingHermes", InstanceFields);

    private global::Tab? _tab;
    private HermesNativeScreenHost? _host;
    private GameObject? _normalVersion;
    private GameObject? _selectedVersion;
    private bool? _lastAppliedSelection;
    private bool _initialized;

    internal void Initialize(global::Tab tab)
    {
        _tab = tab;
        _host = tab.GetComponentInParent<HermesNativeScreenHost>();
        _normalVersion = NormalVersionField?.GetValue(tab) as GameObject;
        _selectedVersion = SelectedVersionField?.GetValue(tab) as GameObject;
        _initialized = true;

        // CreateHermesTab and EnsureHermesTabPlacement are the single authorities
        // for sibling order and RectTransform placement. Start only the visual state.
        ApplyVisualState(selected: false, force: true);
    }

    private void OnEnable()
    {
        if (!_initialized || _tab == null)
        {
            return;
        }

        _host ??= _tab.GetComponentInParent<HermesNativeScreenHost>();

        // Every InventoryScreen.Show creates a fresh native tab controller. The
        // persistent HERMES object must re-enter that generation in the correct state
        // without copying another native tab's coordinates.
        ApplyVisualState(IsHermesActuallyShowing(), force: true);
    }

    private void LateUpdate()
    {
        if (!_initialized || _tab == null)
        {
            return;
        }

        _host ??= _tab.GetComponentInParent<HermesNativeScreenHost>();
        ApplyVisualState(IsHermesActuallyShowing(), force: false);
    }

    private bool IsHermesActuallyShowing()
    {
        if (_host == null)
        {
            return false;
        }

        try
        {
            if (ShowingHermesField?.GetValue(_host) is bool showing)
            {
                return showing;
            }
        }
        catch
        {
            // Fall back to the host lifecycle state below.
        }

        return _host.IsShowingHermes;
    }

    internal void SetSelected(bool selected)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyVisualState(selected, force: true);
    }

    private void ApplyVisualState(bool selected, bool force)
    {
        // EFT may reorder the cloned header without changing its normal/selected children.
        // Repair overlap order every LateUpdate while leaving anchored placement untouched.
        _host?.SetHermesHeaderLayer(selected);

        if (!force && _lastAppliedSelection == selected)
        {
            // EFT can toggle a cloned state later in the same frame. Repair only
            // when the state objects no longer match the authoritative host state.
            var normalCorrect = _normalVersion == null || _normalVersion.activeSelf == !selected;
            var selectedCorrect = _selectedVersion == null || _selectedVersion.activeSelf == selected;
            if (normalCorrect && selectedCorrect)
            {
                return;
            }
        }

        _lastAppliedSelection = selected;

        if (_normalVersion != null && _normalVersion.activeSelf != !selected)
        {
            _normalVersion.SetActive(!selected);
        }

        if (_selectedVersion != null && _selectedVersion.activeSelf != selected)
        {
            _selectedVersion.SetActive(selected);
        }
    }
}
