using System.Collections;
using System.Reflection;
using EFT.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Mounts HERMES as a cloned EFT Tab beside the InventoryScreen's native tabs.
/// HERMES remains outside the enum-backed GClass3808 array so EFT's SelectedTabIndex
/// always points to a valid EInventoryTab when the Character/inventory screen closes.
/// </summary>
internal sealed class HermesNativeScreenHost : MonoBehaviour
{
    private const float RetryDelaySeconds = 1f;
    private const float FullScreenTopInsetPixels = 54f;
    private const float FullScreenBottomInsetPixels = 58f;
    private const float FullScreenSideInsetPixels = 8f;

    private static readonly string[] ContentFieldNames =
    [
        "_simpleStashPanel",
        "_itemsPanel",
        "_mapScreen",
        "_tasksScreen",
        "_achievementsScreen",
        "_prestigeScreen",
        "_skillsAndMasteringScreen",
        "_overallScreen"
    ];

    private static readonly BindingFlags InstanceFields = BindingFlags.Public
                                                             | BindingFlags.NonPublic
                                                             | BindingFlags.Instance
                                                             | BindingFlags.FlattenHierarchy;

    private static readonly FieldInfo? TabDictionaryField =
        typeof(InventoryScreen).GetField("_tabDictionary", InstanceFields);

    private static readonly FieldInfo? NativeTabGroupField =
        typeof(InventoryScreen).GetField("gclass3808_0", InstanceFields);

    private static readonly FieldInfo? TabSelectionChangedField =
        typeof(global::Tab).GetField("action_0", InstanceFields);

    private static readonly FieldInfo? TabControllerField =
        typeof(global::Tab).GetField("Controller", InstanceFields);

    private readonly List<GameObject> _managedPanels = [];
    private readonly List<global::Tab> _nativeTabs = [];

    private InventoryScreen? _screen;
    private IReadOnlyDictionary<EInventoryTab, global::Tab>? _tabDictionary;
    private global::GClass3808? _nativeTabGroup;
    private global::Tab? _lastNativeTab;
    private global::Tab? _hermesTab;
    private GameObject? _hermesTabObject;
    private GameObject? _contentRoot;
    private HermesNativeContentView? _contentView;
    private HermesNativeTabController? _hermesController;
    private global::GClass3808? _boundNativeTabGroup;
    private Coroutine? _refreshCoroutine;
    private Coroutine? _initialHeaderSettleCoroutine;

    private bool _configured;
    private bool _inventoryOpen;
    private bool _buildComplete;
    private bool _showingHermes;
    private bool _selectionPending;
    private bool _showWhenReady;
    private int _transitionVersion;
    private int _showGeneration;
    private int _readyGeneration = -1;
    private int _lastAttachedLogGeneration = -1;
    private int _buildAttempts;
    private float _nextRetryTime;

    internal bool IsShowingHermes => _showingHermes || _selectionPending || _showWhenReady;
    internal bool IsScreenAvailable => _configured && this != null && gameObject != null;
    internal bool IsScreenActive => IsScreenAvailable
                                    && _inventoryOpen
                                    && isActiveAndEnabled
                                    && gameObject.activeInHierarchy;

    internal static void Attach(InventoryScreen screen)
    {
        if (screen == null || !Plugin.Settings.UseNativeInventoryTabs.Value)
        {
            return;
        }

        var host = screen.GetComponent<HermesNativeScreenHost>()
                   ?? screen.gameObject.AddComponent<HermesNativeScreenHost>();
        host.Configure(screen);
    }

    internal static void AttachOrRefresh(InventoryScreen screen)
    {
        if (screen == null || !Plugin.Settings.UseNativeInventoryTabs.Value)
        {
            return;
        }

        var host = screen.GetComponent<HermesNativeScreenHost>()
                   ?? screen.gameObject.AddComponent<HermesNativeScreenHost>();
        host.Configure(screen);
        host.NotifyInventoryShown();
    }

    private void Configure(InventoryScreen screen)
    {
        _screen = screen;
        if (_configured)
        {
            return;
        }

        _configured = true;
        HermesNativeScreenRegistry.Register(this);
    }

    private void NotifyInventoryShown()
    {
        _showGeneration++;
        _readyGeneration = -1;
        _inventoryOpen = true;
        _transitionVersion++;
        _selectionPending = false;
        _showWhenReady = false;
        _buildComplete = false;
        _boundNativeTabGroup = null;
        _buildAttempts = 0;
        StopInitialHeaderSettle();
        HideHermesVisualsImmediate();

        // InventoryScreen.Show() replaces EFT's GClass3808 every time the Character or
        // raid inventory opens. Keep exactly one HERMES tab, but keep it inactive until
        // the current generation has a stable native group and selected EFT tab.
        if (_hermesTabObject != null)
        {
            _hermesTabObject.SetActive(false);
        }

        HermesNativeScreenRegistry.MarkShown(this);
        QueueRefreshAfterNativeInitialization();
    }

    internal void NotifyInventoryClosing()
    {
        _inventoryOpen = false;
        _readyGeneration = -1;
        _transitionVersion++;
        _selectionPending = false;
        _showWhenReady = false;
        _showingHermes = false;
        _buildComplete = false;
        _boundNativeTabGroup = null;
        _nativeTabGroup = null;
        HermesNativeScreenRegistry.MarkClosing(this);

        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }
        StopInitialHeaderSettle();

        HideHermesVisualsImmediate();
        if (_hermesTabObject != null)
        {
            _hermesTabObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (!_configured)
        {
            return;
        }

        var nativeEnabled = Plugin.Settings.UseNativeInventoryTabs.Value;
        var tabShouldBeVisible = nativeEnabled
                                 && _inventoryOpen
                                 && _buildComplete
                                 && _readyGeneration == _showGeneration
                                 && gameObject.activeInHierarchy;
        if (_hermesTabObject != null && _hermesTabObject.activeSelf != tabShouldBeVisible)
        {
            _hermesTabObject.SetActive(tabShouldBeVisible);
        }

        if (!nativeEnabled)
        {
            if (IsShowingHermes)
            {
                HideHermes(false);
            }

            return;
        }

        if (_inventoryOpen
            && !_buildComplete
            && gameObject.activeInHierarchy
            && _refreshCoroutine == null
            && Time.unscaledTime >= _nextRetryTime)
        {
            QueueRefreshAfterNativeInitialization();
        }
    }

    private void QueueRefreshAfterNativeInitialization()
    {
        if (!_inventoryOpen || !isActiveAndEnabled || !gameObject.activeInHierarchy)
        {
            _nextRetryTime = Time.unscaledTime + RetryDelaySeconds;
            return;
        }

        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
        }

        var generation = _showGeneration;
        _refreshCoroutine = StartCoroutine(RefreshAfterNativeInitialization(generation));
    }

    private IEnumerator RefreshAfterNativeInitialization(int generation)
    {
        // InventoryScreen.method_4 initializes controllers and selects the opening tab
        // across several frames. Wait for a stable dictionary, tab group and selected
        // native tab instead of assuming exactly two frames is always enough.
        const int maxFrames = 120;
        for (var frame = 0; frame < maxFrames; frame++)
        {
            yield return null;

            if (!_inventoryOpen
                || generation != _showGeneration
                || _screen == null
                || this == null)
            {
                _refreshCoroutine = null;
                yield break;
            }

            if (frame < 2 || !isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                continue;
            }

            if (TryBuildOrRefreshUi(generation))
            {
                _refreshCoroutine = null;
                yield break;
            }
        }

        _refreshCoroutine = null;
        _nextRetryTime = Time.unscaledTime + RetryDelaySeconds;
        LogWaitingForNativeUi();
    }

    internal void ToggleHermes()
    {
        if (IsShowingHermes)
        {
            HideHermes(true);
        }
        else
        {
            ShowHermes();
        }
    }

    internal void ShowHermes()
    {
        if (!Plugin.Settings.UseNativeInventoryTabs.Value
            || !_inventoryOpen
            || !isActiveAndEnabled
            || !gameObject.activeInHierarchy)
        {
            return;
        }

        if (!_buildComplete || _readyGeneration != _showGeneration)
        {
            _showWhenReady = true;
            QueueRefreshAfterNativeInitialization();
            return;
        }

        if (_showingHermes || _selectionPending)
        {
            return;
        }

        var requestVersion = ++_transitionVersion;
        _selectionPending = true;
        _ = ShowHermesAsync(requestVersion);
    }

    private async Task ShowHermesAsync(int requestVersion)
    {
        try
        {
            RefreshNativeReferences();
            BindHermesController();
            if (_nativeTabGroup == null
                || _hermesTab == null
                || _hermesController == null
                || _contentView == null)
            {
                Plugin.Log?.LogWarning("HERMES could not select its native tab because the EFT tab group or HERMES controller is unavailable.");
                return;
            }

            _lastNativeTab = ResolveSelectedNativeTab()
                             ?? _lastNativeTab
                             ?? ResolveFallbackNativeTab();

            var hidden = await _nativeTabGroup.TryHide();
            if (!hidden
                || requestVersion != _transitionVersion
                || !_inventoryOpen
                || _readyGeneration != _showGeneration
                || !isActiveAndEnabled
                || !gameObject.activeInHierarchy)
            {
                return;
            }

            HermesNativeScreenRegistry.NotifyWindowHidden(this);
            _showingHermes = true;
            _hermesTab.Select(sendCallback: true, uiOnly: false);
            Plugin.Log?.LogDebug($"HERMES native tab selected on '{gameObject.name}'.");
        }
        catch (Exception ex)
        {
            HideHermesVisualsImmediate();
            Plugin.Log?.LogError($"HERMES could not select its native InventoryScreen tab: {ex}");
        }
        finally
        {
            if (requestVersion == _transitionVersion)
            {
                _selectionPending = false;
            }
        }
    }

    internal void HideHermes(bool restorePreviousPanel)
    {
        if (_showWhenReady && !_showingHermes && !_selectionPending)
        {
            _showWhenReady = false;
            return;
        }

        var hadHermesState = IsShowingHermes
                             || (_contentView != null && _contentView.gameObject.activeSelf);
        if (!hadHermesState)
        {
            return;
        }

        // Do not start a second overlapping async tab transition. Cancel the stale
        // operation, hide HERMES immediately, and restore the last native tab only
        // when this request came from F8/navigation rather than a native tab click.
        if (_selectionPending)
        {
            CancelPendingTransition();
            if (restorePreviousPanel)
            {
                RestorePreviousNativePanel();
            }

            return;
        }

        var requestVersion = ++_transitionVersion;
        _selectionPending = true;
        _showWhenReady = false;
        _showingHermes = false;
        _ = HideHermesAsync(requestVersion, restorePreviousPanel, null);
    }

    private void HideHermesFromNativeSelection(global::Tab selectedNativeTab)
    {
        _lastNativeTab = selectedNativeTab;

        // GClass3808 already owns the selected native tab click. When another HERMES
        // transition is still awaiting, cancel it and only clear HERMES state; starting
        // another Deselect task here causes the tab to flicker between both selections.
        if (_selectionPending)
        {
            CancelPendingTransition();
            return;
        }

        var requestVersion = ++_transitionVersion;
        _selectionPending = true;
        _showWhenReady = false;
        _showingHermes = false;
        _ = HideHermesAsync(requestVersion, restorePreviousPanel: false, selectedNativeTab);
    }

    private void CancelPendingTransition()
    {
        _transitionVersion++;
        _selectionPending = false;
        _showWhenReady = false;
        _showingHermes = false;
        HideHermesVisualsImmediate();
    }

    private void RestorePreviousNativePanel()
    {
        if (!_inventoryOpen
            || !isActiveAndEnabled
            || !gameObject.activeInHierarchy
            || !RefreshNativeReferences())
        {
            return;
        }

        var nativeTab = _lastNativeTab
                        ?? ResolveSelectedNativeTab()
                        ?? ResolveFallbackNativeTab();
        if (_nativeTabGroup != null && nativeTab != null)
        {
            _nativeTabGroup.Show(nativeTab, true);
            _lastNativeTab = nativeTab;
        }
    }

    private async Task HideHermesAsync(
        int requestVersion,
        bool restorePreviousPanel,
        global::Tab? selectedNativeTab)
    {
        try
        {
            if (_hermesTab != null)
            {
                await _hermesTab.Deselect();
            }
            else
            {
                _contentView?.HideContent();
            }

            if (requestVersion != _transitionVersion)
            {
                return;
            }

            if (restorePreviousPanel
                && isActiveAndEnabled
                && gameObject.activeInHierarchy)
            {
                RefreshNativeReferences();
                var nativeTab = selectedNativeTab
                                ?? _lastNativeTab
                                ?? ResolveSelectedNativeTab()
                                ?? ResolveFallbackNativeTab();
                if (_nativeTabGroup != null && nativeTab != null)
                {
                    _nativeTabGroup.Show(nativeTab, true);
                    _lastNativeTab = nativeTab;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES could not hide its native InventoryScreen tab: {ex}");
        }
        finally
        {
            if (requestVersion == _transitionVersion)
            {
                HideHermesVisualsImmediate();
                _selectionPending = false;
            }
        }
    }

    private void HideHermesVisualsImmediate()
    {
        _hermesTab?.UpdateVisual(false);
        _contentView?.HideContent();
        if (_contentView == null && _contentRoot != null)
        {
            Plugin.Instance?.Window.SetNativeVisibility(false);
            _contentRoot.SetActive(false);
        }

        _showingHermes = false;
    }

    private bool TryBuildOrRefreshUi(int generation)
    {
        if (_screen == null || !_inventoryOpen || generation != _showGeneration)
        {
            return false;
        }

        try
        {
            _buildAttempts++;
            CollectManagedPanels();
            if (!RefreshNativeReferences())
            {
                return false;
            }

            // method_4 may have created the group but not selected the opening native tab
            // yet. Waiting for a valid selection avoids the HERMES tab jumping while EFT
            // finishes its own initialization.
            if (ResolveSelectedNativeTab() == null)
            {
                return false;
            }

            if (_hermesTab == null || _hermesTabObject == null)
            {
                _hermesTab = CreateHermesTab();
            }
            else
            {
                EnsureHermesTabPlacement();
            }

            var contentTemplate = FindContentTemplate();
            if (contentTemplate == null)
            {
                return false;
            }

            if (_contentRoot == null)
            {
                _contentRoot = CreateContentRoot(contentTemplate);
            }
            else
            {
                EnsureContentPlacement(contentTemplate);
                _contentView ??= _contentRoot.GetComponent<HermesNativeContentView>();
            }

            BindHermesController();
            _buildComplete = _hermesTab != null
                             && _hermesTabObject != null
                             && _contentRoot != null
                             && _contentView != null
                             && _hermesController != null
                             && _nativeTabGroup != null;
            if (!_buildComplete)
            {
                return false;
            }

            _hermesTab.UpdateVisual(false);
            _readyGeneration = generation;
            _hermesTabObject.SetActive(Plugin.Settings.UseNativeInventoryTabs.Value
                                       && _inventoryOpen
                                       && generation == _showGeneration);
            RepairInitialHeaderState();
            QueueInitialHeaderSettle(generation);

            if (_lastAttachedLogGeneration != generation)
            {
                _lastAttachedLogGeneration = generation;
                Plugin.Log?.LogInfo(
                    $"HERMES native EFT Tab ready on InventoryScreen '{gameObject.name}' in scene '{gameObject.scene.name}'. "
                    + $"Native tabs: {_nativeTabs.Count}; show generation: {generation}.");
            }

            if (_showWhenReady)
            {
                _showWhenReady = false;
                ShowHermes();
            }

            return true;
        }
        catch (Exception ex)
        {
            _buildComplete = false;
            _nextRetryTime = Time.unscaledTime + RetryDelaySeconds;
            Plugin.Log?.LogError($"HERMES could not build its native InventoryScreen tab: {ex}");
            return false;
        }
    }

    private bool RefreshNativeReferences()
    {
        if (_screen == null || TabDictionaryField == null || NativeTabGroupField == null)
        {
            return false;
        }

        var dictionary = TabDictionaryField.GetValue(_screen)
                         as IReadOnlyDictionary<EInventoryTab, global::Tab>;
        var tabGroup = NativeTabGroupField.GetValue(_screen) as global::GClass3808;
        if (dictionary == null || dictionary.Count == 0 || tabGroup == null)
        {
            return false;
        }

        var updatedTabs = dictionary.Values
            .Where(tab => tab != null)
            .Distinct()
            .ToList();

        var tabsChanged = updatedTabs.Count != _nativeTabs.Count
                          || updatedTabs.Where((tab, index) => _nativeTabs.Count <= index || _nativeTabs[index] != tab).Any();
        if (tabsChanged)
        {
            UnsubscribeNativeTabs();
            _nativeTabs.Clear();
            _nativeTabs.AddRange(updatedTabs);
            foreach (var nativeTab in _nativeTabs)
            {
                nativeTab.OnSelectionChanged -= OnNativeTabSelectionChanged;
                nativeTab.OnSelectionChanged += OnNativeTabSelectionChanged;
            }
        }

        _tabDictionary = dictionary;
        if (_nativeTabGroup != tabGroup)
        {
            _nativeTabGroup = tabGroup;
            _boundNativeTabGroup = null;
        }
        _lastNativeTab = ResolveSelectedNativeTab()
                         ?? (_lastNativeTab != null && _nativeTabs.Contains(_lastNativeTab)
                             ? _lastNativeTab
                             : ResolveFallbackNativeTab());
        return true;
    }

    private global::Tab? ResolveSelectedNativeTab()
    {
        if (_nativeTabGroup == null || _nativeTabs.Count == 0)
        {
            return null;
        }

        var selectedIndex = _nativeTabGroup.SelectedTabIndex;
        return selectedIndex >= 0 && selectedIndex < _nativeTabs.Count
            ? _nativeTabs[selectedIndex]
            : null;
    }

    private global::Tab? ResolveFallbackNativeTab()
    {
        if (_tabDictionary != null)
        {
            if (_tabDictionary.TryGetValue(EInventoryTab.Gear, out var gearTab) && gearTab != null)
            {
                return gearTab;
            }

            if (_tabDictionary.TryGetValue(EInventoryTab.Overall, out var overallTab) && overallTab != null)
            {
                return overallTab;
            }
        }

        return _nativeTabs.FirstOrDefault();
    }

    private global::Tab CreateHermesTab()
    {
        var template = ResolveAchievementsTemplate()
                       ?? throw new InvalidOperationException("EFT did not provide the Achievements Tab template.");
        var prestigeTab = ResolvePrestigeTab()
                          ?? throw new InvalidOperationException("EFT did not provide the Prestige Tab used to position HERMES.");
        var parent = template.transform.parent
                     ?? throw new InvalidOperationException("The EFT Achievements Tab template has no parent.");
        if (prestigeTab.transform.parent != parent)
        {
            throw new InvalidOperationException("The Achievements and Prestige tabs do not share the same tab-strip parent.");
        }

        RemoveDuplicateHermesTabs(parent, keep: null);

        // Instantiate a separate GameObject. The original Achievements tab is never
        // renamed, re-parented, re-initialized or subscribed to as the HERMES tab.
        var tabObject = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
        tabObject.name = "HERMES_Tab";
        tabObject.SetActive(false);
        PositionHermesAfterPrestige(tabObject.transform, template.transform, prestigeTab.transform, parent);

        var hermesTab = tabObject.GetComponent<global::Tab>()
                        ?? throw new InvalidOperationException("The cloned EFT tab does not contain Tab.");
        if (ReferenceEquals(hermesTab, template))
        {
            throw new InvalidOperationException("HERMES received the original Achievements Tab instead of a clone.");
        }

        // A runtime clone can inherit transient controller/event state from its template.
        // HERMES owns this external tab, so clear both before adding our callback.
        TabSelectionChangedField?.SetValue(hermesTab, null);
        TabControllerField?.SetValue(hermesTab, null);
        hermesTab.OnSelectionChanged += OnHermesSelectionChanged;
        hermesTab.UpdateVisual(false);
        hermesTab.vmethod_0(true);

        SetTabLabel(hermesTab);
        AddHermesIcon(tabObject);

        _hermesTabObject = tabObject;
        tabObject.SetActive(false);
        SetTabLabel(hermesTab);
        return hermesTab;
    }

    private void BindHermesController()
    {
        if (_hermesTab == null || _contentView == null || _nativeTabGroup == null)
        {
            return;
        }

        if (_hermesController != null && _boundNativeTabGroup == _nativeTabGroup)
        {
            return;
        }

        // InventoryScreen.Show() creates a new GClass3808 every time. Rebind the
        // persistent external Tab once per native group so it never retains stale
        // controller state from the previous Character/inventory opening.
        _hermesController = new HermesNativeTabController(_contentView);
        _hermesTab.Init(_hermesController);
        _boundNativeTabGroup = _nativeTabGroup;
    }

    private void EnsureHermesTabPlacement()
    {
        if (_hermesTabObject == null)
        {
            return;
        }

        var achievementsTab = ResolveAchievementsTemplate();
        var prestigeTab = ResolvePrestigeTab();
        var parent = achievementsTab?.transform.parent;
        if (achievementsTab == null
            || prestigeTab == null
            || parent == null
            || prestigeTab.transform.parent != parent)
        {
            return;
        }

        if (_hermesTabObject.transform.parent != parent)
        {
            _hermesTabObject.transform.SetParent(parent, false);
        }

        RemoveDuplicateHermesTabs(parent, _hermesTabObject);
        PositionHermesAfterPrestige(
            _hermesTabObject.transform,
            achievementsTab.transform,
            prestigeTab.transform,
            parent);
        SetTabLabel(_hermesTab!);
    }

    private static void PositionHermesAfterPrestige(
        Transform hermesTransform,
        Transform achievementsTransform,
        Transform prestigeTransform,
        Transform parent)
    {
        // Sibling order matters when EFT uses a layout group. Always place HERMES
        // immediately after Prestige, even when Prestige is currently disabled.
        var siblingIndex = Math.Min(
            prestigeTransform.GetSiblingIndex() + 1,
            Math.Max(0, parent.childCount - 1));
        hermesTransform.SetSiblingIndex(siblingIndex);

        // Some EFT tab strips use fixed RectTransform positions instead of a layout
        // group. The clone inherits Achievements' coordinates, so explicitly advance
        // it by the Achievements-to-Prestige spacing to avoid covering Achievements.
        if (hermesTransform is RectTransform hermesRect
            && achievementsTransform is RectTransform achievementsRect
            && prestigeTransform is RectTransform prestigeRect)
        {
            var step = prestigeRect.anchoredPosition - achievementsRect.anchoredPosition;
            if (step.sqrMagnitude > 0.01f)
            {
                hermesRect.anchoredPosition = prestigeRect.anchoredPosition + step;
            }
        }

        if (parent is RectTransform parentRect)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
        }
    }

    private static void RemoveDuplicateHermesTabs(Transform parent, GameObject? keep)
    {
        var duplicateCount = 0;
        for (var index = parent.childCount - 1; index >= 0; index--)
        {
            var child = parent.GetChild(index)?.gameObject;
            if (child == null
                || child == keep
                || !string.Equals(child.name, "HERMES_Tab", StringComparison.Ordinal))
            {
                continue;
            }

            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
            duplicateCount++;
        }

        if (duplicateCount > 0)
        {
            Plugin.Log?.LogWarning($"HERMES removed {duplicateCount} duplicate native tab object(s).");
        }
    }

    private void RepairInitialHeaderState()
    {
        if (_showingHermes || _selectionPending || _showWhenReady)
        {
            return;
        }

        // The cloned Tab can receive one late visual update from EFT after it is activated.
        // Normalize both the external HERMES header and the authoritative native selection.
        _hermesTab?.UpdateVisual(false);
        _hermesTabObject?.GetComponent<HermesNativeTabHeaderStateController>()
            ?.SetSelected(false);
        ResolveSelectedNativeTab()?.UpdateVisual(true);
    }

    private void QueueInitialHeaderSettle(int generation)
    {
        StopInitialHeaderSettle();
        if (!_inventoryOpen || generation != _showGeneration || !isActiveAndEnabled)
        {
            return;
        }

        _initialHeaderSettleCoroutine = StartCoroutine(SettleInitialHeaderState(generation));
    }

    private IEnumerator SettleInitialHeaderState(int generation)
    {
        // EFT can finalize its opening native-tab selection well after the clone first looks
        // correct. Keep HERMES inactive for the full one-second settling window instead of
        // exiting after a few apparently stable frames. Stop immediately when the player begins
        // opening HERMES, closes the inventory, or a newer InventoryScreen generation replaces it.
        const int maximumFrames = 60;
        for (var frame = 0; frame < maximumFrames; frame++)
        {
            yield return null;
            if (!_inventoryOpen
                || generation != _showGeneration
                || _hermesTabObject == null
                || !isActiveAndEnabled
                || !gameObject.activeInHierarchy
                || _showingHermes
                || _selectionPending
                || _showWhenReady)
            {
                break;
            }

            RepairInitialHeaderState();
        }

        _initialHeaderSettleCoroutine = null;
    }

    private void StopInitialHeaderSettle()
    {
        if (_initialHeaderSettleCoroutine == null)
        {
            return;
        }

        StopCoroutine(_initialHeaderSettleCoroutine);
        _initialHeaderSettleCoroutine = null;
    }

    private void EnsureContentPlacement(RectTransform template)
    {
        if (_contentRoot == null)
        {
            return;
        }

        if (_contentRoot.transform.parent != template.parent)
        {
            _contentRoot.transform.SetParent(template.parent, false);
        }

        var contentRect = (RectTransform)_contentRoot.transform;
        CopyRectTransform(template, contentRect);
        ApplyInventoryViewportInsets(template, contentRect);
        _contentRoot.transform.SetAsLastSibling();
        _contentRoot.SetActive(false);
    }

    private void DestroyHermesTab()
    {
        if (_hermesTab != null)
        {
            _hermesTab.OnSelectionChanged -= OnHermesSelectionChanged;
        }

        if (_hermesTabObject != null)
        {
            _hermesTabObject.SetActive(false);
            UnityEngine.Object.Destroy(_hermesTabObject);
        }

        _hermesTab = null;
        _hermesTabObject = null;
        _hermesController = null;
        _boundNativeTabGroup = null;
    }

    private global::Tab? ResolveAchievementsTemplate()
    {
        if (_tabDictionary != null
            && _tabDictionary.TryGetValue(EInventoryTab.Achievements, out var achievements)
            && achievements != null)
        {
            return achievements;
        }

        return null;
    }

    private global::Tab? ResolvePrestigeTab()
    {
        if (_tabDictionary != null
            && _tabDictionary.TryGetValue(EInventoryTab.Prestige, out var prestige)
            && prestige != null)
        {
            return prestige;
        }

        return null;
    }

    private static void SetTabLabel(global::Tab hermesTab)
    {
        var localizedText = hermesTab.LocalizedText
                            ?? hermesTab.GetComponentInChildren<LocalizedText>(true);
        if (localizedText == null)
        {
            return;
        }

        try
        {
            var type = localizedText.GetType();
            type.GetProperty("LocalizationKey", InstanceFields)?.SetValue(localizedText, "HERMES");
            type.GetProperty("FormattedText", InstanceFields)?.SetValue(localizedText, "HERMES");
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogDebug($"HERMES could not replace the cloned native tab label: {ex.Message}");
        }
    }

    private static void AddHermesIcon(GameObject tabObject)
    {
        var sprite = HermesIconService.AskHermesIcon;
        if (sprite == null)
        {
            return;
        }

        var iconObject = new GameObject(
            "HERMES_Icon",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        iconObject.transform.SetParent(tabObject.transform, false);
        iconObject.transform.SetAsLastSibling();

        var rect = (RectTransform)iconObject.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(28f, 28f);

        var image = iconObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }

    private void OnHermesSelectionChanged(global::Tab _, bool shouldSelect)
    {
        if (shouldSelect)
        {
            ShowHermes();
        }
    }

    private void OnNativeTabSelectionChanged(global::Tab nativeTab, bool shouldSelect)
    {
        if (!shouldSelect)
        {
            return;
        }

        _lastNativeTab = nativeTab;

        // HERMES is external to EFT's enum-backed tab group. A cloned Tab can retain
        // its selected child even while Gear or another native tab is authoritative.
        // Clear HERMES immediately on every native selection so exactly one header is
        // visually active. The header-state controller repeats this at LateUpdate as
        // protection against EFT toggling the clone later in the same frame.
        _hermesTab?.UpdateVisual(false);
        _hermesTabObject?.GetComponent<HermesNativeTabHeaderStateController>()
            ?.SetSelected(false);

        if (IsShowingHermes || (_contentView != null && _contentView.gameObject.activeSelf))
        {
            HideHermesFromNativeSelection(nativeTab);
        }
    }

    private void CollectManagedPanels()
    {
        _managedPanels.Clear();
        foreach (var fieldName in ContentFieldNames)
        {
            var value = _screen?.GetType().GetField(fieldName, InstanceFields)?.GetValue(_screen);
            var panel = value switch
            {
                GameObject gameObjectValue => gameObjectValue,
                Component component => component.gameObject,
                _ => null
            };
            if (panel != null && panel != gameObject && !_managedPanels.Contains(panel))
            {
                _managedPanels.Add(panel);
            }
        }
    }

    private RectTransform? FindContentTemplate()
    {
        // Gear/Health share ItemsPanel and its rectangle is the most stable inventory
        // workspace in both the main menu and an active raid. Prefer it over whichever
        // panel happens to be active while EFT finishes its multi-frame tab setup.
        var itemsPanelValue = _screen?.GetType().GetField("_itemsPanel", InstanceFields)?.GetValue(_screen);
        var itemsPanelRect = itemsPanelValue switch
        {
            GameObject gameObjectValue => gameObjectValue.transform as RectTransform,
            Component componentValue => componentValue.transform as RectTransform,
            _ => null
        };
        if (itemsPanelRect != null)
        {
            return itemsPanelRect;
        }

        return _managedPanels
                   .Where(panel => panel != null && panel.activeSelf)
                   .Select(panel => panel.transform as RectTransform)
                   .FirstOrDefault(rect => rect != null)
               ?? _managedPanels
                   .Select(panel => panel.transform as RectTransform)
                   .FirstOrDefault(rect => rect != null)
               ?? transform as RectTransform;
    }

    private GameObject CreateContentRoot(RectTransform template)
    {
        var contentObject = new GameObject(
            "HERMES_NativeContent",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(CanvasGroup),
            typeof(RectMask2D),
            typeof(HermesNativeContentView));
        contentObject.transform.SetParent(template.parent, false);
        contentObject.transform.SetAsLastSibling();
        var contentRect = (RectTransform)contentObject.transform;
        CopyRectTransform(template, contentRect);
        ApplyInventoryViewportInsets(template, contentRect);

        var background = contentObject.GetComponent<Image>();
        background.color = new Color(0.025f, 0.03f, 0.035f, 0.985f);
        background.raycastTarget = true;

        var canvasGroup = contentObject.GetComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;
        canvasGroup.interactable = true;
        canvasGroup.blocksRaycasts = true;

        _contentView = contentObject.GetComponent<HermesNativeContentView>();
        _contentView.Initialize(Plugin.Instance?.Window
                                ?? throw new InvalidOperationException("HERMES window controller is unavailable."));
        contentObject.SetActive(false);
        return contentObject;
    }


    private static void ApplyInventoryViewportInsets(RectTransform source, RectTransform target)
    {
        var corners = new Vector3[4];
        source.GetWorldCorners(corners);
        var canvas = source.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        var topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

        // ItemsPanel can occupy the complete persistent InventoryScreen, including the
        // native Character tab strip and bottom task bar. Only inset edges that touch
        // the screen so panels that already provide a proper content rectangle are not
        // reduced a second time.
        var topPixels = topRight.y >= Screen.height - 20f ? FullScreenTopInsetPixels : 0f;
        var bottomPixels = bottomLeft.y <= 24f ? FullScreenBottomInsetPixels : 0f;
        var leftPixels = bottomLeft.x <= 2f ? FullScreenSideInsetPixels : 0f;
        var rightPixels = topRight.x >= Screen.width - 2f ? FullScreenSideInsetPixels : 0f;

        var scaleFactor = canvas != null && canvas.scaleFactor > 0.01f
            ? canvas.scaleFactor
            : 1f;
        target.offsetMin = source.offsetMin + new Vector2(
            leftPixels / scaleFactor,
            bottomPixels / scaleFactor);
        target.offsetMax = source.offsetMax - new Vector2(
            rightPixels / scaleFactor,
            topPixels / scaleFactor);
    }

    private static void CopyRectTransform(RectTransform source, RectTransform target)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.offsetMin = source.offsetMin;
        target.offsetMax = source.offsetMax;
        // The content root is parented beside the template, so carrying a prefab-specific
        // local scale can enlarge the IMGUI bridge and create bottom overhang.
        target.localScale = Vector3.one;
        target.localRotation = source.localRotation;
    }

    private void LogWaitingForNativeUi()
    {
        if (_buildAttempts != 1 && _buildAttempts % 5 != 0)
        {
            return;
        }

        var dictionaryCount = _tabDictionary?.Count
                              ?? (TabDictionaryField?.GetValue(_screen!)
                                  as IReadOnlyDictionary<EInventoryTab, global::Tab>)?.Count
                              ?? 0;
        Plugin.Log?.LogWarning(
            $"HERMES is waiting for InventoryScreen native Tab construction on '{gameObject.name}'. "
            + $"Dictionary tabs: {dictionaryCount}; tab group ready: {NativeTabGroupField?.GetValue(_screen!) != null}; "
            + $"content panels: {_managedPanels.Count}; attempt: {_buildAttempts}.");
    }

    private void UnsubscribeNativeTabs()
    {
        foreach (var nativeTab in _nativeTabs)
        {
            if (nativeTab != null)
            {
                nativeTab.OnSelectionChanged -= OnNativeTabSelectionChanged;
            }
        }
    }

    private void OnEnable()
    {
        // InventoryScreen.Show(...) is the authoritative lifecycle signal. Do not
        // reactivate a tab from the previous Show generation during Unity OnEnable.
        if (_hermesTabObject != null && !_buildComplete)
        {
            _hermesTabObject.SetActive(false);
        }
    }

    private void OnDisable()
    {
        _inventoryOpen = false;
        _readyGeneration = -1;
        _transitionVersion++;
        _selectionPending = false;
        _showWhenReady = false;
        _buildComplete = false;
        _boundNativeTabGroup = null;
        _nativeTabGroup = null;
        HermesNativeScreenRegistry.MarkClosing(this);
        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }
        StopInitialHeaderSettle();

        HideHermesVisualsImmediate();
        if (_hermesTabObject != null)
        {
            _hermesTabObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        _inventoryOpen = false;
        _readyGeneration = -1;
        _transitionVersion++;
        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }
        StopInitialHeaderSettle();

        UnsubscribeNativeTabs();
        if (_hermesTab != null)
        {
            _hermesTab.OnSelectionChanged -= OnHermesSelectionChanged;
        }

        HermesNativeScreenRegistry.Unregister(this);
        HideHermesVisualsImmediate();
        DestroyHermesTab();
        if (_contentRoot != null)
        {
            UnityEngine.Object.Destroy(_contentRoot);
            _contentRoot = null;
            _contentView = null;
            _hermesController = null;
        }
    }
}
