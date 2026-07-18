using System.Reflection;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Native EFT workspace shell for HERMES. The complete interface is owned through
/// native uGUI: header, activity state, workspace rail, search toolbar, and every workspace body.
/// </summary>
internal sealed class HermesNativeWorkspaceView : MonoBehaviour
{
    private const float WideRailWidth = 230f;
    private const float WideContentLeft = 236f;
    private const float WideWorkspaceCellWidth = 208f;

    private static readonly (string Name, string Label, string Subtitle)[] WorkspaceDefinitions =
    [
        ("Assistant", "ASSISTANT", "LOCAL READ-ONLY ASSISTANCE"),
        ("ItemSearch", "ITEMS & MARKET", "SEARCH, COMPARE, AND SOURCE ITEMS"),
        ("Hideout", "HIDEOUT", "AREA STATUS, REQUIREMENTS, AND UPGRADES"),
        ("Crafts", "CRAFTS", "RECIPE READINESS AND PROFIT INTELLIGENCE"),
        ("Stash", "STASH", "VALUE, SPACE, DUPLICATES, AND CONDITION"),
        ("Loadout", "LOADOUT", "READINESS, COVERAGE, AND INSURANCE"),
        ("RaidPlanner", "RAID PLANNER", "ROUTES, QUESTS, REQUIRED GEAR, AND ACCESS")
    ];

    private readonly Dictionary<string, AnimatedToggle> _workspaceToggles = new(StringComparer.Ordinal);

    private HermesWindow? _window;
    private RectTransform? _rootRect;
    private RectTransform? _headerRect;
    private RectTransform? _navigationRect;
    private RectTransform? _searchToolbarRect;
    private RectTransform? _bodyRect;
    private RectTransform? _workspaceGridRect;
    private GridLayoutGroup? _workspaceGrid;
    private ToggleGroup? _toggleGroup;
    private TMP_Text? _titleLabel;
    private TMP_Text? _statusLabel;
    private TMP_Text? _diagnosticsLabel;
    private TMP_Text? _activityLabel;
    private Image? _activitySpinner;
    private RectTransform? _activityBadgeRect;
    private TMP_InputField? _searchInput;
    private HermesNativeButtonHandle? _resetButton;
    private HermesNativeButtonHandle? _refreshButton;
    private HermesNativeButtonHandle? _backButton;
    private HermesNativeButtonHandle? _searchButton;
    private HermesNativeButtonHandle? _clearButton;
    private GameObject? _assistantToggleObject;
    private GameObject? _navigationHeadingObject;
    private GameObject? _diagnosticsObject;
    private string _lastTabName = string.Empty;
    private string _lastQuery = string.Empty;
    private bool _lastRefreshing;
    private bool _built;
    private bool _syncingSearch;
    private bool _wideLayout;
    private float _lastRootWidth = -1f;

    internal void Initialize(HermesWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _rootRect = transform as RectTransform
                    ?? throw new InvalidOperationException("HERMES native workspace requires a RectTransform.");
        Build();
        gameObject.SetActive(false);
    }

    internal void ShowWorkspace()
    {
        if (!_built)
        {
            Build();
        }

        HermesNativeWorkspaceRuntime.RequestClientRefresh();
        gameObject.SetActive(true);
        HermesNativeWorkspaceRuntime.Active = true;
        SyncAll(true);
    }

    internal void HideWorkspace()
    {
        HermesNativeWorkspaceRuntime.Active = false;
        if (gameObject.activeSelf)
        {
            gameObject.SetActive(false);
        }
    }

    private void Build()
    {
        if (_built || _window == null || _rootRect == null)
        {
            return;
        }

        HermesRagfairNativeAssets.TryResolve();

        var shellImage = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
        // Keep the shell root transparent while it blocks clicks from reaching the inventory.
        // Every visible HERMES element now belongs to the native uGUI canvas hierarchy, so EFT
        // messenger, notification, and confirmation layers can sort above the center body.
        shellImage.color = Color.clear;
        shellImage.raycastTarget = true;

        _headerRect = CreatePanel("Header", transform, new Color(0f, 0f, 0f, 0.255f));
        _navigationRect = CreatePanel("WorkspaceNavigation", transform, new Color(0f, 0f, 0f, 0.255f));
        _searchToolbarRect = CreatePanel("ItemSearchToolbar", transform, new Color(0f, 0f, 0f, 0.255f));
        _bodyRect = CreatePanel("WorkspaceBody", transform, Color.clear);
        _bodyRect.gameObject.GetComponent<Image>().raycastTarget = false;

        BuildHeader();
        BuildNavigation();
        BuildSearchToolbar();

        var bodyHost = _bodyRect.gameObject.AddComponent<HermesNativeWorkspaceBody>();
        bodyHost.Initialize(_window);

        ApplyResponsiveLayout(true);
        _built = true;
        Plugin.Log?.LogInfo($"HERMES {HermesVersionInfo.DisplayVersion} native workspace built. Ragfair templates ready: {HermesRagfairNativeAssets.Ready}.");
    }

    private void BuildHeader()
    {
        if (_headerRect == null)
        {
            return;
        }

        var titleBlock = CreateRect("TitleBlock", _headerRect);
        titleBlock.anchorMin = new Vector2(0f, 0f);
        titleBlock.anchorMax = new Vector2(1f, 1f);
        titleBlock.offsetMin = new Vector2(14f, 5f);
        titleBlock.offsetMax = new Vector2(-470f, -5f);

        _titleLabel = CreateText("Title", titleBlock, 20f, true, TextAlignmentOptions.Left);
        _titleLabel.color = HermesNativeUiFramework.AccentTextColor;
        _titleLabel.rectTransform.anchorMin = new Vector2(0f, 0.46f);
        _titleLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        _titleLabel.rectTransform.offsetMin = Vector2.zero;
        _titleLabel.rectTransform.offsetMax = Vector2.zero;

        _statusLabel = CreateText("Status", titleBlock, 12f, false, TextAlignmentOptions.Left);
        _statusLabel.color = HermesNativeUiFramework.MutedTextColor;
        _statusLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
        _statusLabel.rectTransform.anchorMax = new Vector2(1f, 0.48f);
        _statusLabel.rectTransform.offsetMin = Vector2.zero;
        _statusLabel.rectTransform.offsetMax = Vector2.zero;

        var actions = CreateRect("Actions", _headerRect);
        actions.anchorMin = new Vector2(1f, 0f);
        actions.anchorMax = new Vector2(1f, 1f);
        actions.pivot = new Vector2(1f, 0.5f);
        actions.anchoredPosition = new Vector2(-8f, 0f);
        actions.sizeDelta = new Vector2(452f, 0f);

        var layout = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 7, 7);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        _activityBadgeRect = HermesNativeUiFramework.CreateStatusBadge(
            actions,
            "ActivityBadge",
            out var activityLabel,
            out var activitySpinner);
        _activityLabel = activityLabel;
        _activitySpinner = activitySpinner;
        _activityBadgeRect.gameObject.SetActive(false);

        _resetButton = CreateNativeButton("Reset", actions, "RESET", 72f);
        _refreshButton = CreateNativeButton("Refresh", actions, "REFRESH", 88f);
        _backButton = CreateNativeButton("Back", actions, "BACK", 72f);

        _resetButton.AddListener(() =>
        {
            if (_window != null)
            {
                HermesEftWindowReflection.Clear(_window);
            }
        });
        _refreshButton.AddListener(RefreshActiveWorkspace);
        _backButton.AddListener(() => HermesNativeScreenRegistry.TryReturnToInventory());

        var separator = CreatePanel("BottomSeparator", _headerRect, HermesNativeUiFramework.SeparatorColor);
        separator.anchorMin = new Vector2(0f, 0f);
        separator.anchorMax = new Vector2(1f, 0f);
        separator.pivot = new Vector2(0.5f, 0f);
        separator.offsetMin = Vector2.zero;
        separator.offsetMax = new Vector2(0f, 1f);
        separator.gameObject.GetComponent<Image>().raycastTarget = false;
    }

    private void RefreshActiveWorkspace()
    {
        if (_window is null)
        {
            return;
        }

        // The window routes every profile workspace through the coordinator. The server performs
        // one explicit source recheck first, then only the selected workspace is downloaded.
        HermesEftWindowReflection.Refresh(_window);
    }

    private void BuildNavigation()
    {
        if (_navigationRect == null || _window == null)
        {
            return;
        }

        _toggleGroup = _navigationRect.gameObject.GetComponent<ToggleGroup>()
                       ?? _navigationRect.gameObject.AddComponent<ToggleGroup>();
        _toggleGroup.allowSwitchOff = false;

        var heading = CreateText("Heading", _navigationRect, 12f, true, TextAlignmentOptions.Left);
        _navigationHeadingObject = heading.gameObject;
        heading.text = "WORKSPACES";
        heading.color = new Color32(145, 151, 148, 255);

        _workspaceGridRect = CreateRect("WorkspaceToggleGrid", _navigationRect);
        _workspaceGrid = _workspaceGridRect.gameObject.AddComponent<GridLayoutGroup>();
        _workspaceGrid.padding = new RectOffset(0, 0, 0, 0);
        _workspaceGrid.spacing = new Vector2(0f, 3f);
        _workspaceGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        _workspaceGrid.startAxis = GridLayoutGroup.Axis.Horizontal;
        _workspaceGrid.childAlignment = TextAnchor.UpperLeft;
        _workspaceGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        _workspaceGrid.constraintCount = 1;

        for (var index = 0; index < WorkspaceDefinitions.Length; index++)
        {
            var definition = WorkspaceDefinitions[index];
            var toggle = CreateNativeToggle(definition.Name, definition.Label, _workspaceGridRect, index);
            _workspaceToggles[definition.Name] = toggle;
            if (definition.Name == "Assistant")
            {
                _assistantToggleObject = toggle.gameObject;
            }
        }

        _diagnosticsObject = new GameObject(
            "Diagnostics",
            typeof(RectTransform),
            typeof(VerticalLayoutGroup));
        _diagnosticsObject.transform.SetParent(_navigationRect, false);

        var diagnosticsLayout = _diagnosticsObject.GetComponent<VerticalLayoutGroup>();
        diagnosticsLayout.spacing = 3f;
        diagnosticsLayout.childControlWidth = true;
        diagnosticsLayout.childControlHeight = false;
        diagnosticsLayout.childForceExpandWidth = true;
        diagnosticsLayout.childForceExpandHeight = false;

        var readOnly = CreateText("ReadOnly", _diagnosticsObject.transform, 12f, true, TextAlignmentOptions.Left);
        readOnly.text = "READ ONLY";
        readOnly.color = new Color32(197, 195, 178, 255);
        readOnly.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

        _diagnosticsLabel = CreateText("DiagnosticsText", _diagnosticsObject.transform, 11f, false, TextAlignmentOptions.Left);
        _diagnosticsLabel.color = new Color32(123, 132, 131, 255);
        _diagnosticsLabel.enableWordWrapping = true;
        _diagnosticsLabel.gameObject.AddComponent<LayoutElement>().preferredHeight = 32f;

        var copyButton = CreateNativeButton("CopyDiagnostics", _diagnosticsObject.transform, "COPY DIAGNOSTICS", 160f);
        copyButton.Layout.preferredHeight = 28f;
        copyButton.AddListener(() =>
        {
            if (_window == null)
            {
                return;
            }

            GUIUtility.systemCopyBuffer = HermesEftWindowReflection.BuildDiagnostics(_window);
            HermesEftWindowReflection.SetRefreshStatus(_window, "Diagnostics copied.");
        });
    }

    private void BuildSearchToolbar()
    {
        if (_searchToolbarRect == null || _window == null)
        {
            return;
        }

        var layout = _searchToolbarRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 2, 2);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        var searchCaption = CreateText("SearchCaption", _searchToolbarRect, 12f, true, TextAlignmentOptions.Center);
        searchCaption.text = "SEARCH";
        searchCaption.color = HermesNativeUiFramework.MutedTextColor;
        var captionLayout = searchCaption.gameObject.AddComponent<LayoutElement>();
        captionLayout.minWidth = 58f;
        captionLayout.preferredWidth = 58f;
        captionLayout.flexibleWidth = 0f;
        captionLayout.minHeight = 40f;
        captionLayout.preferredHeight = 40f;

        var fieldSlot = CreateRect("SearchFieldSlot", _searchToolbarRect);
        var fieldSlotLayout = fieldSlot.gameObject.AddComponent<LayoutElement>();
        fieldSlotLayout.flexibleWidth = 1f;
        fieldSlotLayout.preferredWidth = 500f;
        fieldSlotLayout.minWidth = 220f;
        fieldSlotLayout.preferredHeight = 40f;
        fieldSlotLayout.minHeight = 40f;

        _searchInput = CreateNativeSearchField(fieldSlot);
        SetStretch((RectTransform)_searchInput.transform, 0f, 0f, 0f, 0f);

        _searchInput.onValueChanged.RemoveAllListeners();
        _searchInput.onEndEdit.RemoveAllListeners();
        _searchInput.onSubmit.RemoveAllListeners();
        _searchInput.onValueChanged.AddListener(value =>
        {
            if (_syncingSearch || _window == null)
            {
                return;
            }

            HermesNativeSearchBridge.SetQuery(_window, value);
        });
        _searchInput.onSubmit.AddListener(_ =>
        {
            if (_window != null && HermesNativeSearchBridge.CanSearch(_window))
            {
                HermesNativeSearchBridge.Search(_window);
            }
        });

        _searchButton = CreateNativeButton("Search", _searchToolbarRect, "SEARCH", 138f);
        _clearButton = CreateNativeButton("Clear", _searchToolbarRect, "CLEAR", 108f);
        _searchButton.AddListener(() =>
        {
            if (_window != null)
            {
                HermesNativeSearchBridge.Search(_window);
            }
        });
        _clearButton.AddListener(() =>
        {
            if (_window == null)
            {
                return;
            }

            HermesNativeSearchBridge.Clear(_window);
            SyncSearch(true);
        });

        var separator = CreatePanel("BottomSeparator", _searchToolbarRect, HermesNativeUiFramework.SeparatorColor);
        separator.anchorMin = new Vector2(0f, 0f);
        separator.anchorMax = new Vector2(1f, 0f);
        separator.pivot = new Vector2(0.5f, 0f);
        separator.offsetMin = Vector2.zero;
        separator.offsetMax = new Vector2(0f, 1f);
        separator.gameObject.GetComponent<Image>().raycastTarget = false;
        separator.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        separator.SetAsFirstSibling();
    }

    private void Update()
    {
        if (!_built || _window == null || !gameObject.activeInHierarchy)
        {
            return;
        }

        ApplyResponsiveLayout(false);
        SyncAll(false);
    }

    private void SyncAll(bool force)
    {
        if (_window == null)
        {
            return;
        }

        var tabName = HermesEftWindowReflection.ActiveTabName(_window);
        if (string.Equals(tabName, "Assistant", StringComparison.Ordinal)
            && !Plugin.Settings.EnableAssistantTab.Value)
        {
            HermesEftWindowReflection.Select(_window, "ItemSearch");
            tabName = "ItemSearch";
        }

        var refreshing = HermesEftWindowReflection.IsRefreshing(_window);
        var searching = HermesNativeSearchBridge.IsSearching(_window);
        var tabChanged = force || !string.Equals(tabName, _lastTabName, StringComparison.Ordinal);

        if (tabChanged)
        {
            _lastTabName = tabName;
            foreach (var pair in _workspaceToggles)
            {
                pair.Value.ToggleSilent(string.Equals(pair.Key, tabName, StringComparison.Ordinal));
            }

            if (_titleLabel != null)
            {
                _titleLabel.text = $"HERMES  /  {HermesEftWindowReflection.ActiveTabDisplayName(_window)}";
            }

            ApplyResponsiveLayout(true);
        }

        if (_statusLabel != null)
        {
            _statusLabel.text = refreshing
                ? NormalizeActivityStatus(HermesEftWindowReflection.RefreshStatus(_window), "SYNCING CURRENT VIEW")
                : WorkspaceSubtitle(tabName);
        }

        if (_diagnosticsLabel != null)
        {
            _diagnosticsLabel.text = HermesEftWindowReflection.FormatDiagnostics(_window);
        }

        if (_assistantToggleObject != null)
        {
            _assistantToggleObject.SetActive(Plugin.Settings.EnableAssistantTab.Value);
        }

        _navigationHeadingObject?.SetActive(_wideLayout);
        if (_diagnosticsObject != null)
        {
            _diagnosticsObject.SetActive(_wideLayout && Plugin.Settings.ShowDiagnosticsFooter.Value);
        }

        var active = refreshing || searching;
        if (_activityBadgeRect != null)
        {
            _activityBadgeRect.gameObject.SetActive(active);
        }
        if (_activityLabel != null)
        {
            _activityLabel.text = searching ? "SEARCHING" : "SYNCING";
        }
        if (_activitySpinner != null)
        {
            _activitySpinner.enabled = active;
        }

        if (force || refreshing != _lastRefreshing || tabChanged)
        {
            _lastRefreshing = refreshing;
            if (_refreshButton != null)
            {
                _refreshButton.SetInteractable(!refreshing);
                _refreshButton.SetText(refreshing ? "WORKING" : "REFRESH");
            }
        }

        SyncSearch(force || tabChanged);
    }

    private void SyncSearch(bool force)
    {
        if (_window == null || _searchInput == null)
        {
            return;
        }

        var query = HermesNativeSearchBridge.Query(_window);
        if (force || !string.Equals(query, _lastQuery, StringComparison.Ordinal))
        {
            _lastQuery = query;
            _syncingSearch = true;
            _searchInput.SetTextWithoutNotify(query);
            _syncingSearch = false;
        }

        if (_searchButton != null)
        {
            _searchButton.SetInteractable(HermesNativeSearchBridge.CanSearch(_window));
            _searchButton.SetText(HermesNativeSearchBridge.IsSearching(_window) ? "SEARCHING" : "SEARCH");
        }
    }

    private void ApplyResponsiveLayout(bool force)
    {
        if (_rootRect == null
            || _headerRect == null
            || _navigationRect == null
            || _workspaceGridRect == null
            || _workspaceGrid == null
            || _searchToolbarRect == null
            || _bodyRect == null
            || _window == null)
        {
            return;
        }

        var rootWidth = _rootRect.rect.width;
        var wide = rootWidth >= 1080f;
        var isItemSearch = HermesEftWindowReflection.IsSelected(_window, "ItemSearch");
        var widthChanged = Mathf.Abs(rootWidth - _lastRootWidth) >= 1f;
        if (!force
            && !widthChanged
            && wide == _wideLayout
            && _searchToolbarRect.gameObject.activeSelf == isItemSearch)
        {
            return;
        }

        _lastRootWidth = rootWidth;
        _wideLayout = wide;

        SetAnchored(
            _headerRect,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(6f, -58f),
            new Vector2(-6f, -6f));

        if (wide)
        {
            SetAnchored(
                _navigationRect,
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(6f, 6f),
                new Vector2(WideRailWidth, -64f));

            if (_navigationHeadingObject?.transform is RectTransform headingRect)
            {
                SetAnchored(
                    headingRect,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(8f, -36f),
                    new Vector2(-8f, -8f));
            }

            if (_diagnosticsObject?.transform is RectTransform diagnosticsRect)
            {
                SetAnchored(
                    diagnosticsRect,
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(8f, 8f),
                    new Vector2(-8f, 82f));
            }

            SetAnchored(
                _workspaceGridRect,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, Plugin.Settings.ShowDiagnosticsFooter.Value ? 90f : 8f),
                new Vector2(-8f, -40f));

            _workspaceGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _workspaceGrid.constraintCount = 1;
            _workspaceGrid.startAxis = GridLayoutGroup.Axis.Horizontal;
            _workspaceGrid.spacing = new Vector2(0f, 2f);
            _workspaceGrid.cellSize = new Vector2(WideWorkspaceCellWidth, 40f);

            _searchToolbarRect.gameObject.SetActive(isItemSearch);
            if (isItemSearch)
            {
                SetAnchored(
                    _searchToolbarRect,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(WideContentLeft, -108f),
                    new Vector2(-6f, -64f));
                SetAnchored(
                    _bodyRect,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(WideContentLeft, 6f),
                    new Vector2(-6f, -114f));
            }
            else
            {
                SetAnchored(
                    _bodyRect,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(WideContentLeft, 6f),
                    new Vector2(-6f, -64f));
            }
        }
        else
        {
            _searchToolbarRect.gameObject.SetActive(isItemSearch);
            SetAnchored(
                _navigationRect,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(6f, -148f),
                new Vector2(-6f, -64f));
            SetAnchored(
                _workspaceGridRect,
                Vector2.zero,
                Vector2.one,
                new Vector2(6f, 5f),
                new Vector2(-6f, -5f));

            var navigationWidth = rootWidth - 12f;
            if (navigationWidth <= 1f)
            {
                navigationWidth = 320f;
            }

            _workspaceGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _workspaceGrid.constraintCount = 4;
            _workspaceGrid.startAxis = GridLayoutGroup.Axis.Horizontal;
            _workspaceGrid.spacing = new Vector2(4f, 4f);
            _workspaceGrid.cellSize = new Vector2(
                Mathf.Max(72f, (navigationWidth - 24f) / 4f),
                36f);

            if (isItemSearch)
            {
                SetAnchored(
                    _searchToolbarRect,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(6f, -198f),
                    new Vector2(-6f, -154f));
                SetAnchored(
                    _bodyRect,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(6f, 6f),
                    new Vector2(-6f, -204f));
            }
            else
            {
                SetAnchored(
                    _bodyRect,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(6f, 6f),
                    new Vector2(-6f, -154f));
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(_workspaceGridRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(_navigationRect);
        if (_searchToolbarRect.gameObject.activeSelf)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_searchToolbarRect);
        }
    }

    private AnimatedToggle CreateNativeToggle(string tabName, string label, Transform parent, int rowIndex)
    {
        GameObject toggleObject;
        if (HermesRagfairNativeAssets.AnimatedToggleTemplate != null)
        {
            toggleObject = UnityEngine.Object.Instantiate(HermesRagfairNativeAssets.AnimatedToggleTemplate, parent, false);
            HermesNativeUiFramework.NormalizeClonedControl(toggleObject);
        }
        else
        {
            toggleObject = CreateFallbackToggle(parent);
        }

        toggleObject.name = $"HERMES_{tabName}_Toggle";
        var rect = (RectTransform)toggleObject.transform;
        rect.localScale = Vector3.one;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        HermesNativeUiFramework.AddWorkspaceRowChrome(toggleObject, rowIndex);

        var layout = toggleObject.GetComponent<LayoutElement>() ?? toggleObject.AddComponent<LayoutElement>();
        layout.preferredHeight = 40f;
        layout.minHeight = 36f;
        layout.flexibleWidth = 1f;

        var toggle = toggleObject.GetComponent<AnimatedToggle>()
                     ?? throw new InvalidOperationException("Ragfair animated toggle template did not contain AnimatedToggle.");
        var spawnable = toggleObject.GetComponent<UISpawnableToggle>();
        if (spawnable != null)
        {
            spawnable.method_1(_toggleGroup!);
            spawnable.method_2(label, 19, null, null);
        }
        toggle.group = _toggleGroup;
        toggle.interactable = true;
        toggle.onValueChanged.RemoveAllListeners();
        toggle.onValueChanged.AddListener(toggle.ToggleSilent);
        toggle.onValueChanged.AddListener(selected =>
        {
            if (selected && _window != null)
            {
                HermesEftWindowReflection.Select(_window, tabName);
            }
        });

        // The Flea toggle template sizes its text container for the original 130/140 px tab.
        // HERMES rows are wider, so stretching only the visible Label leaves its parent offset
        // and makes the caption look off-center. Disable the template layout and stretch both the
        // SizeLabel container and its visible Label across the full workspace row.
        var rootLayout = toggleObject.GetComponent<HorizontalLayoutGroup>();
        if (rootLayout != null)
        {
            rootLayout.enabled = false;
        }

        var sizeLabelRect = toggleObject.transform.Find("SizeLabel") as RectTransform;
        if (sizeLabelRect != null)
        {
            sizeLabelRect.anchorMin = Vector2.zero;
            sizeLabelRect.anchorMax = Vector2.one;
            sizeLabelRect.pivot = new Vector2(0.5f, 0.5f);
            sizeLabelRect.anchoredPosition = Vector2.zero;
            sizeLabelRect.sizeDelta = Vector2.zero;
            sizeLabelRect.offsetMin = Vector2.zero;
            sizeLabelRect.offsetMax = Vector2.zero;
        }

        foreach (var text in toggleObject.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!string.Equals(text.name, "Label", StringComparison.Ordinal)
                && !string.Equals(text.name, "SizeLabel", StringComparison.Ordinal))
            {
                continue;
            }

            text.text = label;
            text.fontSize = 19f;
            text.enableAutoSizing = false;
            text.alignment = TextAlignmentOptions.Center;
            text.margin = Vector4.zero;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;

            var labelRect = text.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.anchoredPosition = Vector2.zero;
            labelRect.sizeDelta = Vector2.zero;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
        }

        return toggle;
    }

    private HermesNativeButtonHandle CreateNativeButton(string name, Transform parent, string label, float width)
    {
        GameObject buttonObject;
        DefaultUIButton? nativeButton = null;
        Button? fallbackButton = null;
        TMP_Text? fallbackLabel = null;

        if (HermesRagfairNativeAssets.DefaultButtonTemplate != null)
        {
            buttonObject = UnityEngine.Object.Instantiate(HermesRagfairNativeAssets.DefaultButtonTemplate, parent, false);
            HermesNativeUiFramework.NormalizeClonedControl(buttonObject);
            nativeButton = buttonObject.GetComponent<DefaultUIButton>();
        }
        else
        {
            buttonObject = CreateFallbackButton(parent);
            fallbackButton = buttonObject.GetComponent<Button>();
            fallbackLabel = buttonObject.GetComponentInChildren<TMP_Text>(true);
        }

        buttonObject.name = $"HERMES_{name}_Button";
        var rect = (RectTransform)buttonObject.transform;
        rect.localScale = Vector3.one;
        var layout = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();
        layout.preferredWidth = width;
        layout.minWidth = width;
        layout.preferredHeight = 40f;
        layout.minHeight = 36f;
        layout.flexibleWidth = 0f;

        var handle = new HermesNativeButtonHandle(buttonObject, layout, nativeButton, fallbackButton, fallbackLabel);
        handle.ClearListeners();
        handle.SetText(label);
        handle.SetInteractable(true);
        return handle;
    }

    private TMP_InputField CreateNativeSearchField(Transform parent)
    {
        var fieldObject = new GameObject(
            "HERMES_ItemSearchField",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(TMP_InputField));
        fieldObject.transform.SetParent(parent, false);

        var fieldRect = (RectTransform)fieldObject.transform;
        fieldRect.localScale = Vector3.one;
        SetStretch(fieldRect, 0f, 0f, 0f, 0f);

        var fieldImage = fieldObject.GetComponent<Image>();
        fieldImage.sprite = HermesRagfairNativeAssets.SearchBorderSprite;
        fieldImage.type = Image.Type.Sliced;
        fieldImage.color = Color.white;
        fieldImage.raycastTarget = true;

        var textArea = CreateRect("Text Area", fieldObject.transform);
        SetStretch(textArea, 10f, 4f, 38f, 4f);
        textArea.gameObject.AddComponent<RectMask2D>();

        var text = CreateText("Text", textArea, 18f, false, TextAlignmentOptions.Left);
        SetStretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.color = HermesNativeUiFramework.NormalTextColor;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Masking;

        var placeholder = CreateText("Placeholder", textArea, 18f, false, TextAlignmentOptions.Left);
        SetStretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.text = "ENTER ITEM NAME";
        placeholder.color = new Color32(118, 124, 122, 255);
        placeholder.enableWordWrapping = false;
        placeholder.overflowMode = TextOverflowModes.Ellipsis;

        var searchIconSprite = FindSearchIconSprite();
        if (searchIconSprite != null)
        {
            var iconObject = new GameObject(
                "SearchIcon",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            iconObject.transform.SetParent(fieldObject.transform, false);
            var iconRect = (RectTransform)iconObject.transform;
            iconRect.anchorMin = new Vector2(1f, 0.5f);
            iconRect.anchorMax = new Vector2(1f, 0.5f);
            iconRect.pivot = new Vector2(1f, 0.5f);
            iconRect.anchoredPosition = new Vector2(-10f, 0f);
            iconRect.sizeDelta = new Vector2(20f, 20f);
            var icon = iconObject.GetComponent<Image>();
            icon.sprite = searchIconSprite;
            icon.preserveAspect = true;
            icon.color = HermesNativeUiFramework.NormalTextColor;
            icon.raycastTarget = false;
        }

        var field = fieldObject.GetComponent<TMP_InputField>();
        field.targetGraphic = fieldImage;
        field.textViewport = textArea;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.characterLimit = 96;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.contentType = TMP_InputField.ContentType.Standard;
        field.readOnly = false;
        field.richText = false;
        field.selectionColor = new Color(0.659f, 0.808f, 1f, 0.753f);
        field.caretColor = Color.white;
        field.customCaretColor = true;
        field.caretWidth = 1;
        field.scrollSensitivity = 1f;
        return field;
    }

    private static string WorkspaceSubtitle(string tabName)
    {
        foreach (var definition in WorkspaceDefinitions)
        {
            if (string.Equals(definition.Name, tabName, StringComparison.Ordinal))
            {
                return definition.Subtitle;
            }
        }

        return "LOCAL READ-ONLY INVENTORY INTELLIGENCE";
    }

    private static string NormalizeActivityStatus(string status, string fallback)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return fallback;
        }

        if (status.Contains("reload", StringComparison.OrdinalIgnoreCase)
            || status.Contains("refresh", StringComparison.OrdinalIgnoreCase)
            || status.Contains("analy", StringComparison.OrdinalIgnoreCase)
            || status.Contains("load", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return status.Trim().ToUpperInvariant();
    }

    private static Sprite? FindSearchIconSprite()
    {
        var template = HermesRagfairNativeAssets.SearchFieldTemplate;
        if (template != null)
        {
            var named = template.GetComponentsInChildren<Image>(true)
                .FirstOrDefault(image => image != null
                                         && image.sprite != null
                                         && (image.name.Contains("Search", StringComparison.OrdinalIgnoreCase)
                                             || image.sprite.name.Contains("search", StringComparison.OrdinalIgnoreCase)));
            if (named?.sprite != null)
            {
                return named.sprite;
            }
        }

        return Resources.FindObjectsOfTypeAll<Sprite>()
            .FirstOrDefault(sprite => sprite != null
                                      && sprite.name.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(parent, false);
        var image = panel.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = color.a > 0.001f;
        return (RectTransform)panel.transform;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));
        gameObject.transform.SetParent(parent, false);
        return (RectTransform)gameObject.transform;
    }

    private static TMP_Text CreateText(string name, Transform parent, float size, bool bold, TextAlignmentOptions alignment)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        gameObject.transform.SetParent(parent, false);
        var text = gameObject.GetComponent<TextMeshProUGUI>();
        var font = bold
            ? HermesRagfairNativeAssets.ShadowedFont ?? HermesRagfairNativeAssets.NormalFont
            : HermesRagfairNativeAssets.NormalFont ?? HermesRagfairNativeAssets.ShadowedFont;
        font ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(candidate => candidate != null);
        if (font != null)
        {
            text.font = font;
        }
        text.fontSize = size;
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    private static GameObject CreateFallbackToggle(Transform parent)
    {
        var gameObject = new GameObject(
            "FallbackToggle",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Animator),
            typeof(AnimatedToggle),
            typeof(LayoutElement));
        gameObject.transform.SetParent(parent, false);
        var image = gameObject.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.ButtonBackgroundSprite;
        image.color = Color.white;
        image.raycastTarget = true;
        var toggle = gameObject.GetComponent<AnimatedToggle>();
        toggle.targetGraphic = image;
        toggle.transition = Selectable.Transition.ColorTint;
        var label = CreateText("Label", gameObject.transform, 20f, true, TextAlignmentOptions.Center);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 0f);
        label.rectTransform.offsetMax = new Vector2(-8f, 0f);
        return gameObject;
    }

    private static GameObject CreateFallbackButton(Transform parent)
    {
        var gameObject = new GameObject(
            "FallbackButton",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        gameObject.transform.SetParent(parent, false);
        var image = gameObject.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.ButtonBackgroundSprite;
        image.color = Color.white;
        image.raycastTarget = true;
        var button = gameObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        var label = CreateText("Label", gameObject.transform, 18f, true, TextAlignmentOptions.Center);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 0f);
        label.rectTransform.offsetMax = new Vector2(-8f, 0f);
        return gameObject;
    }

    private static GameObject CreateFallbackSearchField(Transform parent)
    {
        var gameObject = new GameObject(
            "FallbackSearchField",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(TMP_InputField),
            typeof(LayoutElement));
        gameObject.transform.SetParent(parent, false);
        var image = gameObject.GetComponent<Image>();
        image.sprite = HermesRagfairNativeAssets.SearchBorderSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;

        var viewport = CreateRect("Text Area", gameObject.transform);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(10f, 4f);
        viewport.offsetMax = new Vector2(-10f, -4f);
        viewport.gameObject.AddComponent<RectMask2D>();

        var text = CreateText("Text", viewport, 18f, false, TextAlignmentOptions.Left);
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        var placeholder = CreateText("Placeholder", viewport, 18f, false, TextAlignmentOptions.Left);
        placeholder.rectTransform.anchorMin = Vector2.zero;
        placeholder.rectTransform.anchorMax = Vector2.one;
        placeholder.rectTransform.offsetMin = Vector2.zero;
        placeholder.rectTransform.offsetMax = Vector2.zero;
        placeholder.text = "SEARCH ITEMS";
        placeholder.color = new Color32(118, 124, 122, 255);

        var input = gameObject.GetComponent<TMP_InputField>();
        input.targetGraphic = image;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = placeholder;
        return gameObject;
    }

    private static void SetStretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void SetAnchored(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }
}


internal sealed class HermesNativeButtonHandle
{
    private const BindingFlags ButtonMethodFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo? SetRawTextWithSizeMethod = typeof(DefaultUIButton).GetMethod(
        "SetRawText",
        ButtonMethodFlags,
        null,
        [typeof(string), typeof(int)],
        null);
    private static readonly MethodInfo? SetRawTextMethod = typeof(DefaultUIButton).GetMethod(
        "SetRawText",
        ButtonMethodFlags,
        null,
        [typeof(string)],
        null);

    private readonly GameObject _gameObject;
    private readonly DefaultUIButton? _nativeButton;
    private readonly Button? _fallbackButton;
    private readonly TMP_Text? _fallbackLabel;
    private string _text = string.Empty;

    internal HermesNativeButtonHandle(
        GameObject gameObject,
        LayoutElement layout,
        DefaultUIButton? nativeButton,
        Button? fallbackButton,
        TMP_Text? fallbackLabel)
    {
        _gameObject = gameObject;
        Layout = layout;
        _nativeButton = nativeButton;
        _fallbackButton = fallbackButton;
        _fallbackLabel = fallbackLabel;
    }

    internal LayoutElement Layout { get; }

    internal void AddListener(UnityEngine.Events.UnityAction action)
    {
        if (_nativeButton != null)
        {
            _nativeButton.OnClick.AddListener(action);
        }
        else
        {
            _fallbackButton?.onClick.AddListener(action);
        }
    }

    internal void ClearListeners()
    {
        _nativeButton?.OnClick.RemoveAllListeners();
        _fallbackButton?.onClick.RemoveAllListeners();
    }

    internal void SetText(string text)
    {
        if (string.Equals(_text, text, StringComparison.Ordinal))
        {
            return;
        }

        _text = text;
        if (_nativeButton != null)
        {
            try
            {
                if (SetRawTextWithSizeMethod != null)
                {
                    SetRawTextWithSizeMethod.Invoke(_nativeButton, [text, 18]);
                    return;
                }

                if (SetRawTextMethod != null)
                {
                    SetRawTextMethod.Invoke(_nativeButton, [text]);
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"HERMES could not invoke DefaultUIButton.SetRawText: {ex.Message}");
            }

            foreach (var label in _gameObject.GetComponentsInChildren<TMP_Text>(true))
            {
                label.text = text;
            }

            var iconContainer = _gameObject.transform.Find("SizeLabel/IconContainer")?.gameObject;
            iconContainer?.SetActive(false);
        }
        else if (_fallbackLabel != null)
        {
            _fallbackLabel.text = text;
        }
    }

    internal void SetInteractable(bool interactable)
    {
        if (_nativeButton != null)
        {
            _nativeButton.Interactable = interactable;
        }

        if (_fallbackButton != null)
        {
            _fallbackButton.interactable = interactable;
        }
    }
}
