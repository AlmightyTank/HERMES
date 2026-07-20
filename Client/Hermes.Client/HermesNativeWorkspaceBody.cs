using System.Collections;
using System.Runtime.CompilerServices;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Fully native uGUI renderer for every HERMES workspace. No workspace content is drawn
/// through IMGUI, so EFT messenger, confirmation dialogs, notifications, and modal windows
/// share one predictable Canvas ordering path with the HERMES workspace rail.
/// </summary>
internal sealed partial class HermesNativeWorkspaceBody : MonoBehaviour
{
    private const float SyncInterval = 0.20f;
    private static int MaximumRowsPerSection => Plugin.Settings.GetMaximumRowsPerSection();
    private const float CompactToolbarHeight = 36f;
    private const float CompactControlHeight = 32f;
    private const float CompactMetricHeight = 46f;
    private const float CompactSectionHeight = 30f;

    private static readonly (string Value, string Label)[] TagColorOptions =
    [
        ("red", "RED"),
        ("orange", "ORANGE"),
        ("yellow", "YELLOW"),
        ("green", "GREEN"),
        ("blue", "BLUE"),
        ("violet", "VIOLET"),
        ("grey", "GREY")
    ];

    private readonly Dictionary<string, float> _savedScrollPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScrollRect> _activeScrolls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameObject> _workspaceRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workspaceRootFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ScrollRect>> _workspaceScrolls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedRaidMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _itemDetailSectionExpansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scrollsToForceTop = new(StringComparer.Ordinal);

    private HermesNativeWorkspaceState? _state;
    private RectTransform? _root;
    private GameObject? _contentRoot;
    private string _lastFingerprint = string.Empty;
    private float _nextSyncAt;
    private int _lastClientRefreshRevision;
    private bool _forceRebuild;

    private string _assistantDraft = string.Empty;
    private string _hideoutSearch = string.Empty;
    private string _hideoutFilter = "ALL";
    private string _craftView = "NOW";
    private string _craftSearch = string.Empty;
    private string _craftFilter = "ALL";
    private bool _craftAvailableOnly;
    private int _craftFocusRevision;
    private readonly HashSet<string> _craftFocusKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _stashSearch = string.Empty;
    private string _stashView = "OVERVIEW";
    private string _loadoutView = "OVERVIEW";
    private string _raidSearch = string.Empty;
    private string _tagEditorInstanceKey = string.Empty;
    private string _tagEditorMode = "apply";
    private string _tagEditorDraftName = string.Empty;
    private string _tagEditorDraftColor = "blue";
    private bool _tagColorDropdownOpen;
    private string _lastItemResultSetKey = string.Empty;
    private string _lastSelectedItemKey = string.Empty;

    private static NativeFleaCheckboxStyle? _nativeFleaCheckboxStyle;
    private static bool _nativeFleaCheckboxProbeLogged;

    internal void Initialize(HermesWindow window)
    {
        _state = new HermesNativeWorkspaceState(window);
        _root = transform as RectTransform
                ?? throw new InvalidOperationException("HERMES native body requires a RectTransform.");

        var image = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
        image.color = HermesNativeUiFramework.PanelColor;
        image.raycastTarget = true;

        _forceRebuild = true;
        _lastClientRefreshRevision = HermesNativeWorkspaceRuntime.ClientRefreshRevision;
        _lastFingerprint = _state.BuildFingerprint();
        Rebuild(force: true);
    }

    private void OnEnable()
    {
        _forceRebuild = true;
        _nextSyncAt = 0f;
    }

    private void Update()
    {
        if (_state is null || !HermesNativeWorkspaceRuntime.Active)
        {
            return;
        }

        var clientRefreshRevision = HermesNativeWorkspaceRuntime.ClientRefreshRevision;
        var clientRefreshRequested = clientRefreshRevision != _lastClientRefreshRevision;
        if (!clientRefreshRequested && Time.unscaledTime < _nextSyncAt)
        {
            return;
        }

        if (clientRefreshRequested)
        {
            _lastClientRefreshRevision = clientRefreshRevision;
            _forceRebuild = true;
        }

        _nextSyncAt = Time.unscaledTime + SyncInterval;
        var fingerprint = _state.BuildFingerprint();
        if (_forceRebuild || !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            var force = _forceRebuild;
            _lastFingerprint = fingerprint;
            _forceRebuild = false;
            Rebuild(force);
        }
    }

    private void Rebuild(bool force)
    {
        if (_state is null || _root is null)
        {
            return;
        }

        var activeTab = _state.ActiveTab;
        if (activeTab == "Crafts")
        {
            ApplyPendingCraftFocus();
        }

        var itemResultSetKey = activeTab == "ItemSearch"
            ? BuildItemResultSetKey()
            : _lastItemResultSetKey;
        var selectedItemKey = activeTab == "ItemSearch"
            ? _state.SelectedItem?.ItemKey ?? string.Empty
            : _lastSelectedItemKey;
        var resetItemResultsToTop = activeTab == "ItemSearch"
                                    && !string.Equals(itemResultSetKey, _lastItemResultSetKey, StringComparison.Ordinal);
        var resetItemDetailsToTop = activeTab == "ItemSearch"
                                    && (resetItemResultsToTop
                                        || !string.Equals(selectedItemKey, _lastSelectedItemKey, StringComparison.OrdinalIgnoreCase));

        CaptureScrollPositions();
        if (resetItemResultsToTop)
        {
            _savedScrollPositions["item-results"] = 1f;
            _scrollsToForceTop.Add("item-results");
        }
        if (resetItemDetailsToTop)
        {
            _savedScrollPositions["item-details"] = 1f;
            _scrollsToForceTop.Add("item-details");
        }
        _lastItemResultSetKey = itemResultSetKey;
        _lastSelectedItemKey = selectedItemKey;

        if (_contentRoot != null)
        {
            _contentRoot.SetActive(false);
        }

        _activeScrolls.Clear();
        if (!force
            && _workspaceRoots.TryGetValue(activeTab, out var cachedRoot)
            && cachedRoot != null
            && _workspaceRootFingerprints.TryGetValue(activeTab, out var cachedFingerprint)
            && string.Equals(cachedFingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            _contentRoot = cachedRoot;
            _contentRoot.SetActive(true);
            if (_workspaceScrolls.TryGetValue(activeTab, out var cachedScrolls))
            {
                foreach (var pair in cachedScrolls)
                {
                    if (pair.Value != null)
                    {
                        _activeScrolls[pair.Key] = pair.Value;
                    }
                }
            }

            StartCoroutine(RestoreScrollPositionsNextFrame());
            return;
        }

        if (_workspaceRoots.TryGetValue(activeTab, out var previousRoot) && previousRoot != null)
        {
            previousRoot.SetActive(false);
            Destroy(previousRoot);
        }

        _workspaceRoots.Remove(activeTab);
        _workspaceRootFingerprints.Remove(activeTab);
        _workspaceScrolls.Remove(activeTab);

        _contentRoot = new GameObject($"NativeWorkspaceContent_{activeTab}", typeof(RectTransform));
        _contentRoot.transform.SetParent(_root, false);
        var contentRect = (RectTransform)_contentRoot.transform;
        HermesNativeUiFramework.Stretch(contentRect, 6f, 6f, 6f, 6f);

        try
        {
            switch (_state.ActiveTab)
            {
                case "Assistant":
                    RenderAssistant(contentRect);
                    break;
                case "Hideout":
                    RenderHideout(contentRect);
                    break;
                case "Crafts":
                    RenderCrafts(contentRect);
                    break;
                case "Stash":
                    RenderStash(contentRect);
                    break;
                case "Loadout":
                    RenderLoadout(contentRect, false);
                    break;
                case "RaidPlanner":
                    RenderLoadout(contentRect, true);
                    break;
                default:
                    RenderItemSearch(contentRect);
                    break;
            }

            RenderActionConfirmationPopout(contentRect);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native workspace body failed to build: {ex}");
            var fallback = CreateVerticalRoot(contentRect);
            AddSectionHeader(fallback, "NATIVE WORKSPACE ERROR");
            AddCard(fallback, "Rendering failed", ex.Message, "ERROR");
        }

        _workspaceRoots[activeTab] = _contentRoot;
        _workspaceRootFingerprints[activeTab] = _lastFingerprint;
        _workspaceScrolls[activeTab] = new Dictionary<string, ScrollRect>(_activeScrolls, StringComparer.Ordinal);

        StartCoroutine(RestoreScrollPositionsNextFrame());
    }
}
