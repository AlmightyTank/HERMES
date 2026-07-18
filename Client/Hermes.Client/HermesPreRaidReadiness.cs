using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using EFT.UI;
using EFT.UI.Matchmaker;
using SPT.Reflection.Patching;
using Hermes.Client.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Pre-raid readiness interception and map-selection prefetch. It is deliberately client-only and read-only:
/// a Harmony prefix intercepts MatchmakerInsuranceScreen.method_9 before EFT advances, displays
/// the current HERMES loadout analysis, and allows that original method exactly once after the
/// player chooses Continue. The confirmation-screen Back button is then detected through EFT's native DefaultUIButton event and returns to the existing readiness review.
/// </summary>
internal static class HermesPreRaidReadinessSettings
{
    internal static ConfigEntry<bool> Enabled { get; private set; } = null!;
    internal static ConfigEntry<int> HydrationWarningPercent { get; private set; } = null!;
    internal static ConfigEntry<int> EnergyWarningPercent { get; private set; } = null!;
    internal static ConfigEntry<int> HealthWarningPercent { get; private set; } = null!;
    internal static ConfigEntry<int> MaximumFindings { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowReadyChecks { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Pre-Raid Readiness",
            "Enable HERMES readiness screen",
            true,
            "Show the HERMES readiness review after pressing Next on the PMC insurance screen.");
        HydrationWarningPercent = config.Bind(
            "Pre-Raid Readiness",
            "Hydration warning percent",
            50,
            new ConfigDescription(
                "Warn when raid-start hydration is at or below this percentage.",
                new AcceptableValueRange<int>(1, 100)));
        EnergyWarningPercent = config.Bind(
            "Pre-Raid Readiness",
            "Energy warning percent",
            50,
            new ConfigDescription(
                "Warn when raid-start energy is at or below this percentage.",
                new AcceptableValueRange<int>(1, 100)));
        HealthWarningPercent = config.Bind(
            "Pre-Raid Readiness",
            "Health warning percent",
            80,
            new ConfigDescription(
                "Warn when total health is at or below this percentage.",
                new AcceptableValueRange<int>(1, 100)));
        MaximumFindings = config.Bind(
            "Pre-Raid Readiness",
            "Maximum displayed findings",
            14,
            new ConfigDescription(
                "Maximum critical and warning cards displayed on the pre-raid screen.",
                new AcceptableValueRange<int>(4, 30)));
        ShowReadyChecks = config.Bind(
            "Pre-Raid Readiness",
            "Show ready checks",
            true,
            "Show a compact Ready section for checks that passed.");
    }
}

internal sealed class HermesPreRaidMapSelectionPrefetchPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var screenType = typeof(MatchMakerSelectionLocationScreen);
        var showMethod = screenType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == screenType && method.Name == "Show")
            .OrderByDescending(method => method.GetParameters().Length)
            .FirstOrDefault();

        return showMethod
               ?? screenType.GetMethod(
                   "Awake",
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(screenType.FullName, "Show/Awake");
    }

    [PatchPostfix]
    private static void Postfix()
    {
        HermesPreRaidReadinessController.BeginMapSelectionPreparation();
    }
}

internal sealed class HermesPreRaidInsuranceNextPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(MatchmakerInsuranceScreen).GetMethod(
               "method_9",
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(MatchmakerInsuranceScreen).FullName, "method_9");

    [PatchPrefix]
    private static bool Prefix(MatchmakerInsuranceScreen __instance)
    {
        if (HermesPreRaidReadinessController.ConsumeNativeNextBypass(__instance))
        {
            return true;
        }

        if (!HermesPreRaidReadinessSettings.Enabled.Value)
        {
            return true;
        }

        return !HermesPreRaidReadinessController.TryOpen(__instance);
    }
}

internal sealed class HermesPreRaidConfirmationBackPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(DefaultUIButton).GetMethod(
               "method_11",
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(DefaultUIButton).FullName, "method_11");

    [PatchPostfix]
    private static void Postfix(DefaultUIButton __instance)
    {
        HermesPreRaidReadinessController.HandleNativeButtonClick(__instance);
    }
}

internal static class HermesPreRaidReadinessController
{
    private static MatchmakerInsuranceScreen? _nativeNextBypassScreen;
    private static MatchmakerInsuranceScreen? _confirmationReturnInsuranceScreen;
    private static HermesPreRaidInsuranceBridge? _confirmationReturnBridge;

    internal static void BeginMapSelectionPreparation()
    {
        if (!HermesPreRaidReadinessSettings.Enabled.Value)
        {
            return;
        }

        var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
        if (coordinator is null)
        {
            Plugin.Log.LogWarning(
                "HERMES reached map selection before the shared workspace coordinator was available.");
            return;
        }

        _ = coordinator.BeginPreRaidReadinessPrefetch();
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogInfo(
                "HERMES started pre-raid readiness preparation from the map selection screen.");
        }
    }

    internal static bool TryOpen(MatchmakerInsuranceScreen insuranceScreen)
    {
        try
        {
            if (insuranceScreen is null
                || insuranceScreen.gameObject is null
                || !insuranceScreen.gameObject.activeInHierarchy)
            {
                return false;
            }

            var bridge = insuranceScreen.GetComponent<HermesPreRaidInsuranceBridge>()
                         ?? insuranceScreen.gameObject.AddComponent<HermesPreRaidInsuranceBridge>();
            bridge.Initialize(insuranceScreen);
            bridge.OpenReadiness();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"HERMES native Insurance Next interception failed: {ex}");
            return false;
        }
    }

    internal static void AllowNativeNextOnce(MatchmakerInsuranceScreen insuranceScreen)
    {
        _nativeNextBypassScreen = insuranceScreen;
    }

    internal static bool ConsumeNativeNextBypass(MatchmakerInsuranceScreen insuranceScreen)
    {
        if (!ReferenceEquals(_nativeNextBypassScreen, insuranceScreen))
        {
            return false;
        }

        _nativeNextBypassScreen = null;
        return true;
    }

    internal static void ArmConfirmationReturn(
        MatchmakerInsuranceScreen insuranceScreen,
        HermesPreRaidInsuranceBridge bridge)
    {
        _confirmationReturnInsuranceScreen = insuranceScreen;
        _confirmationReturnBridge = bridge;
    }

    internal static void CancelConfirmationReturn(MatchmakerInsuranceScreen? insuranceScreen)
    {
        if (insuranceScreen is not null
            && !ReferenceEquals(_confirmationReturnInsuranceScreen, insuranceScreen))
        {
            return;
        }

        _confirmationReturnInsuranceScreen = null;
        _confirmationReturnBridge = null;
    }

    internal static void HandleNativeButtonClick(DefaultUIButton button)
    {
        try
        {
            var insuranceScreen = _confirmationReturnInsuranceScreen;
            var bridge = _confirmationReturnBridge;
            if (insuranceScreen is null
                || bridge is null
                || button is null
                || button.gameObject is null
                || insuranceScreen.gameObject is null
                || !LooksLikeMatchmakerConfirmationBack(button))
            {
                return;
            }

            _confirmationReturnInsuranceScreen = null;
            _confirmationReturnBridge = null;
            Plugin.Log.LogInfo(
                $"HERMES detected pre-raid confirmation Back on '{button.gameObject.name}'; returning to readiness.");
            bridge.ReturnFromConfirmationBack();
        }
        catch (Exception ex)
        {
            CancelConfirmationReturn(null);
            Plugin.Log.LogWarning($"HERMES confirmation Back detection failed safely: {ex.Message}");
        }
    }

    private static bool LooksLikeMatchmakerConfirmationBack(DefaultUIButton button)
    {
        var objectName = button.gameObject.name ?? string.Empty;
        var header = button.HeaderText ?? string.Empty;
        var looksLikeBack = objectName.Contains("Back", StringComparison.OrdinalIgnoreCase)
                            || header.Contains("Back", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeBack
            || button.GetComponentInParent<MatchmakerInsuranceScreen>(true) is not null)
        {
            return false;
        }

        return button.GetComponentsInParent<MonoBehaviour>(true).Any(component =>
        {
            var type = component.GetType();
            var typeNamespace = type.Namespace ?? string.Empty;
            return typeNamespace.StartsWith("EFT.UI.Matchmaker", StringComparison.Ordinal)
                   || type.Name.Contains("Matchmaker", StringComparison.OrdinalIgnoreCase);
        });
    }
}

internal sealed class HermesPreRaidInsuranceBridge : MonoBehaviour
{
    private static readonly string[] KnownMapNames =
    [
        "Ground Zero",
        "Streets of Tarkov",
        "Customs",
        "Factory",
        "Woods",
        "Shoreline",
        "Interchange",
        "Reserve",
        "Lighthouse",
        "The Lab",
        "Labs"
    ];

    private MatchmakerInsuranceScreen? _insuranceScreen;
    private GameObject? _overlay;
    private TMP_Text? _summaryText;
    private TMP_Text? _statusText;
    private RectTransform? _findingsContent;
    private Task<HermesLoadoutSummaryResponse>? _loadTask;
    private bool _initialized;
    private bool _overlayVisible;
    private bool _continuing;
    private bool _hasRenderedSnapshot;
    private bool _returningFromConfirmation;

    internal void Initialize(MatchmakerInsuranceScreen insuranceScreen)
    {
        if (_initialized && ReferenceEquals(_insuranceScreen, insuranceScreen))
        {
            return;
        }

        _insuranceScreen = insuranceScreen;
        _initialized = true;
    }

    private void OnEnable()
    {
        _continuing = false;
    }

    private void OnDisable()
    {
        HideOverlay();
    }

    private void OnDestroy()
    {
        HermesPreRaidReadinessController.CancelConfirmationReturn(_insuranceScreen);
        if (_overlay is not null)
        {
            Destroy(_overlay);
        }
    }

    private void Update()
    {
        if (!HermesPreRaidReadinessSettings.Enabled.Value || _insuranceScreen is null)
        {
            return;
        }

        if (_overlayVisible && Input.GetKeyDown(KeyCode.Escape))
        {
            HideOverlay();
            return;
        }

        if (_loadTask is null || !_loadTask.IsCompleted)
        {
            return;
        }

        var completed = _loadTask;
        _loadTask = null;
        if (completed.IsCanceled)
        {
            SetStatus("Readiness check was cancelled. You can refresh or continue.");
            return;
        }

        if (completed.IsFaulted)
        {
            var error = completed.Exception?.GetBaseException();
            SetStatus(HermesApiClient.DescribeFailure(error ?? new Exception("Unknown readiness error"), "Pre-raid readiness"));
            BuildFailureFinding();
            return;
        }

        RenderSummary(completed.Result);
    }

    internal void OpenReadiness()
    {
        if (_continuing || _insuranceScreen is null)
        {
            return;
        }

        // Open with the exact same loadout object already shown in the HERMES workspace. This
        // makes the readiness screen immediate and prevents a second 10-12 second calculation.
        ShowReadiness(refreshSnapshot: false, restoreExisting: false);
    }

    internal void ReturnFromConfirmationBack()
    {
        if (_insuranceScreen is null || _returningFromConfirmation)
        {
            return;
        }

        _returningFromConfirmation = true;
        _continuing = false;
        if (Plugin.Instance is { } plugin)
        {
            plugin.StartCoroutine(WaitForInsuranceAndRestoreReadiness());
        }
        else
        {
            _returningFromConfirmation = false;
            Plugin.Log.LogWarning("HERMES could not start the confirmation Back return because the plugin runner is unavailable.");
        }
    }

    private IEnumerator WaitForInsuranceAndRestoreReadiness()
    {
        // Let EFT finish the confirmation screen's native Back transition first.
        yield return null;
        yield return null;

        var deadline = Time.realtimeSinceStartup + 5f;
        while (_insuranceScreen is not null
               && !_insuranceScreen.gameObject.activeInHierarchy
               && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        _returningFromConfirmation = false;
        if (_insuranceScreen is null || !_insuranceScreen.gameObject.activeInHierarchy)
        {
            Plugin.Log.LogWarning(
                "HERMES confirmation Back was detected, but the Insurance screen did not reactivate in time.");
            yield break;
        }

        ShowReadiness(refreshSnapshot: !_hasRenderedSnapshot, restoreExisting: true);
    }

    private void ShowReadiness(bool refreshSnapshot, bool restoreExisting)
    {
        EnsureOverlayBuilt();
        if (_overlay is null)
        {
            return;
        }

        _overlay.SetActive(true);
        _overlay.transform.SetAsLastSibling();
        _overlayVisible = true;

        if (restoreExisting && !refreshSnapshot && _hasRenderedSnapshot)
        {
            Plugin.Log.LogInfo("HERMES restored the existing pre-raid readiness review after confirmation Back.");
            return;
        }

        var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
        if (!refreshSnapshot
            && coordinator is not null
            && coordinator.TryGetPreparedPreRaidLoadout(out var prepared, out var preparedUnixTime)
            && prepared is { Found: true })
        {
            RenderSummary(prepared);
            var preparedAge = preparedUnixTime > 0
                ? Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - preparedUnixTime)
                : 0L;
            SetStatus(
                $"Readiness was prepared at map selection {preparedAge}s ago. No duplicate Insurance refresh is needed.");
            return;
        }

        var activePrefetch = coordinator?.ActivePreRaidPrefetch;
        var cached = coordinator?.CachedLoadout;
        if (!refreshSnapshot && activePrefetch is not null)
        {
            if (cached is { Found: true })
            {
                RenderSummary(cached);
            }
            else
            {
                ClearFindings();
                if (_summaryText is not null)
                {
                    _summaryText.text = $"{ResolveSelectedMap()} • finishing map-selection preparation";
                }
            }

            SetStatus(
                "Finishing the readiness refresh started at map selection; this screen is joining that same request.");
            _loadTask = activePrefetch;
            return;
        }

        if (!refreshSnapshot && cached is { Found: true })
        {
            RenderSummary(cached);
            var ageSeconds = cached.GeneratedUnixTime > 0
                ? Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cached.GeneratedUnixTime)
                : 0L;
            SetStatus(
                $"Showing the shared HERMES Loadout snapshot ({ageSeconds}s old) while a lightweight change check runs.");
            StartLoadoutRefresh(preserveRenderedFindings: true, forceRefresh: false);
            return;
        }

        RefreshReadiness();
    }

    private void RefreshReadiness()
    {
        var preserveRendered = _hasRenderedSnapshot;
        if (!preserveRendered)
        {
            ClearFindings();
            if (_summaryText is not null)
            {
                _summaryText.text = $"{ResolveSelectedMap()} • HERMES is loading the shared loadout snapshot";
            }
        }

        SetStatus(preserveRendered
            ? "Refreshing the shared HERMES Loadout snapshot in the background; current findings remain visible."
            : "Checking current PMC vitals, equipment, medical coverage, and active quest requirements...");
        StartLoadoutRefresh(preserveRenderedFindings: preserveRendered, forceRefresh: true);
    }

    private void StartLoadoutRefresh(bool preserveRenderedFindings, bool forceRefresh)
    {
        if (_loadTask is not null && !_loadTask.IsCompleted)
        {
            return;
        }

        try
        {
            var coordinator = HermesWorkspaceSnapshotCoordinator.Current;
            if (coordinator is null)
            {
                throw new InvalidOperationException("The shared HERMES workspace coordinator is unavailable.");
            }

            if (!preserveRenderedFindings)
            {
                _hasRenderedSnapshot = false;
            }

            _loadTask = forceRefresh
                ? coordinator.RefreshSharedLoadoutAsync()
                : coordinator.RevalidateLoadoutAsync();
        }
        catch (Exception ex)
        {
            SetStatus(HermesApiClient.DescribeFailure(ex, "Pre-raid readiness"));
            if (!_hasRenderedSnapshot)
            {
                BuildFailureFinding();
            }
        }
    }

    private void EnsureOverlayBuilt()
    {
        if (_overlay is not null || _insuranceScreen is null)
        {
            return;
        }

        var host = FindOverlayHost(_insuranceScreen.transform);
        _overlay = new GameObject(
            "HERMES_PreRaid_ReadinessScreen",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster),
            typeof(Image));
        _overlay.transform.SetParent(host, false);
        var overlayRect = (RectTransform)_overlay.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        var canvas = _overlay.GetComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = 600;
        var scaler = _overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _overlay.GetComponent<Image>().color = new Color(0.025f, 0.028f, 0.03f, 0.985f);

        var frame = CreatePanel(_overlay.transform, "ReadinessFrame", new Color(0.08f, 0.085f, 0.09f, 0.98f));
        var frameRect = (RectTransform)frame.transform;
        frameRect.anchorMin = new Vector2(0.12f, 0.08f);
        frameRect.anchorMax = new Vector2(0.88f, 0.92f);
        frameRect.offsetMin = Vector2.zero;
        frameRect.offsetMax = Vector2.zero;

        var layout = frame.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(28, 28, 24, 24);
        layout.spacing = 12f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        var title = CreateText(frame.transform, "HERMES — PRE-RAID READINESS", 30, FontStyles.Bold, TextAlignmentOptions.Left);
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
        _summaryText = CreateText(frame.transform, "Preparing current raid...", 20, FontStyles.Normal, TextAlignmentOptions.Left);
        _summaryText.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
        _statusText = CreateText(frame.transform, string.Empty, 17, FontStyles.Normal, TextAlignmentOptions.Left);
        _statusText.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;

        var scrollObject = new GameObject(
            "FindingsScroll",
            typeof(RectTransform),
            typeof(Image),
            typeof(Mask),
            typeof(ScrollRect),
            typeof(LayoutElement));
        scrollObject.transform.SetParent(frame.transform, false);
        scrollObject.GetComponent<Image>().color = new Color(0.02f, 0.022f, 0.024f, 0.72f);
        scrollObject.GetComponent<Mask>().showMaskGraphic = true;
        scrollObject.GetComponent<LayoutElement>().flexibleHeight = 1f;

        var viewport = (RectTransform)scrollObject.transform;
        _findingsContent = new GameObject(
            "FindingsContent",
            typeof(RectTransform),
            typeof(VerticalLayoutGroup),
            typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        _findingsContent.SetParent(viewport, false);
        _findingsContent.anchorMin = new Vector2(0f, 1f);
        _findingsContent.anchorMax = new Vector2(1f, 1f);
        _findingsContent.pivot = new Vector2(0.5f, 1f);
        _findingsContent.offsetMin = new Vector2(12f, 0f);
        _findingsContent.offsetMax = new Vector2(-12f, 0f);
        var findingsLayout = _findingsContent.GetComponent<VerticalLayoutGroup>();
        findingsLayout.padding = new RectOffset(8, 8, 10, 10);
        findingsLayout.spacing = 8f;
        findingsLayout.childControlHeight = true;
        findingsLayout.childControlWidth = true;
        findingsLayout.childForceExpandHeight = false;
        findingsLayout.childForceExpandWidth = true;
        _findingsContent.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = scrollObject.GetComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = _findingsContent;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 34f;

        var buttons = new GameObject("Actions", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        buttons.transform.SetParent(frame.transform, false);
        var actionsElement = buttons.GetComponent<LayoutElement>();
        actionsElement.minHeight = 46f;
        actionsElement.preferredHeight = 46f;
        actionsElement.flexibleHeight = 0f;
        var buttonsLayout = buttons.GetComponent<HorizontalLayoutGroup>();
        buttonsLayout.spacing = 10f;
        buttonsLayout.childAlignment = TextAnchor.MiddleRight;
        buttonsLayout.childControlWidth = true;
        buttonsLayout.childControlHeight = true;
        buttonsLayout.childForceExpandWidth = false;
        buttonsLayout.childForceExpandHeight = false;

        CreateActionButton(buttons.transform, "BACK TO INSURANCE", HideOverlay);
        CreateActionButton(buttons.transform, "REFRESH CHECK", RefreshReadiness);
        CreateActionButton(buttons.transform, "CONTINUE", ContinueToNativeNext);

        _overlay.SetActive(false);
    }

    private static Transform FindOverlayHost(Transform screenTransform)
    {
        var canvas = screenTransform.GetComponentInParent<Canvas>();
        return canvas is not null ? canvas.transform : screenTransform;
    }

    private void ContinueToNativeNext()
    {
        if (_insuranceScreen is null || _continuing)
        {
            return;
        }

        _continuing = true;
        var insuranceScreen = _insuranceScreen;
        HideOverlay();

        Plugin.Log.LogInfo(
            "HERMES pre-raid readiness accepted; allowing EFT MatchmakerInsuranceScreen.method_9 once.");
        HermesPreRaidReadinessController.ArmConfirmationReturn(insuranceScreen, this);
        HermesPreRaidReadinessController.AllowNativeNextOnce(insuranceScreen);
        insuranceScreen.method_9();

        if (Plugin.Instance is { } plugin)
        {
            plugin.StartCoroutine(RearmIfInsuranceRemainsOpen());
        }
    }

    private IEnumerator RearmIfInsuranceRemainsOpen()
    {
        yield return new WaitForSecondsRealtime(1f);
        if (_insuranceScreen is not null
            && _insuranceScreen.gameObject.activeInHierarchy
            && this is not null)
        {
            HermesPreRaidReadinessController.CancelConfirmationReturn(_insuranceScreen);
            _continuing = false;
        }
    }

    private void HideOverlay()
    {
        _overlayVisible = false;
        if (_overlay is not null)
        {
            _overlay.SetActive(false);
        }
    }

    private void RenderSummary(HermesLoadoutSummaryResponse summary)
    {
        if (!_overlayVisible)
        {
            return;
        }

        ClearFindings();
        var mapName = ResolveSelectedMap();
        if (!summary.Found)
        {
            SetStatus(summary.Message ?? "HERMES could not read the current PMC loadout.");
            BuildFailureFinding();
            return;
        }

        summary.Warnings ??= [];
        summary.QuestRequirements ??= [];
        summary.RaidPlans ??= [];
        summary.Weapons ??= [];
        summary.Armor ??= [];
        summary.Medical ??= new HermesMedicalReadiness();
        summary.Vitals ??= new HermesVitalsSummary();

        var findings = BuildFindings(summary, mapName);
        if (Plugin.Settings.DetailedLogging.Value)
        {
            Plugin.Log.LogInfo(
                $"HERMES pre-raid rendered shared Loadout data: "
                + $"{summary.Warnings.Count} server warning(s), "
                + $"{summary.QuestRequirements.Count} quest requirement(s), "
                + $"{findings.Count} readiness finding(s), map '{mapName}'.");
        }
        var criticalCount = findings.Count(finding => finding.Severity == ReadinessSeverity.Critical);
        var warningCount = findings.Count(finding => finding.Severity == ReadinessSeverity.Warning);
        var shown = findings
            .Where(finding => finding.Severity != ReadinessSeverity.Ready)
            .Take(HermesPreRaidReadinessSettings.MaximumFindings.Value)
            .ToList();

        if (_summaryText is not null)
        {
            _summaryText.text = $"{mapName} • {summary.Readiness} • {summary.ReadinessScore}/100";
        }

        SetStatus(criticalCount == 0 && warningCount == 0
            ? "No blocking readiness problems were detected. Review the ready checks or continue."
            : $"{criticalCount} critical issue(s) and {warningCount} warning(s) found. HERMES remains advisory; Continue never changes your loadout.");

        if (shown.Count == 0)
        {
            AddSectionHeader("READY FOR DEPLOYMENT");
            AddFindingCard(new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Readiness",
                "No critical or warning-level issues detected."));
        }
        else
        {
            var critical = shown.Where(finding => finding.Severity == ReadinessSeverity.Critical).ToList();
            var warnings = shown.Where(finding => finding.Severity == ReadinessSeverity.Warning).ToList();
            if (critical.Count > 0)
            {
                AddSectionHeader("CRITICAL");
                foreach (var finding in critical)
                {
                    AddFindingCard(finding);
                }
            }

            if (warnings.Count > 0)
            {
                AddSectionHeader("WARNINGS");
                foreach (var finding in warnings)
                {
                    AddFindingCard(finding);
                }
            }
        }

        if (HermesPreRaidReadinessSettings.ShowReadyChecks.Value)
        {
            var ready = findings
                .Where(finding => finding.Severity == ReadinessSeverity.Ready)
                .Take(6)
                .ToList();
            if (ready.Count > 0)
            {
                AddSectionHeader("READY");
                foreach (var finding in ready)
                {
                    AddFindingCard(finding);
                }
            }
        }

        _hasRenderedSnapshot = true;
    }

    private List<ReadinessFinding> BuildFindings(HermesLoadoutSummaryResponse summary, string mapName)
    {
        var output = new List<ReadinessFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var warning in summary.Warnings.Where(warning => warning is not null))
        {
            var category = string.IsNullOrWhiteSpace(warning.Category) ? "Loadout" : warning.Category;
            var message = warning.Message ?? string.Empty;
            var severity = string.Equals(warning.Severity, "Critical", StringComparison.OrdinalIgnoreCase)
                ? ReadinessSeverity.Critical
                : ReadinessSeverity.Warning;

            // Pre-raid quest advice is deployment-specific. Never show it unless HERMES can
            // positively resolve both the selected raid map and the warning's objective map.
            if (category.Contains("Quest", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsKnownSelectedMap(mapName)
                    || !TryResolveQuestWarningMap(summary, message, out var warningMap)
                    || !MapMatches(warningMap, mapName))
                {
                    continue;
                }
            }

            AddUnique(output, seen, new ReadinessFinding(
                severity,
                category,
                message));
        }

        var hydrationThreshold = HermesPreRaidReadinessSettings.HydrationWarningPercent.Value;
        if (summary.Vitals.HydrationPercent <= hydrationThreshold)
        {
            AddUnique(output, seen, new ReadinessFinding(
                summary.Vitals.HydrationPercent <= 20 ? ReadinessSeverity.Critical : ReadinessSeverity.Warning,
                "Vitals",
                $"Hydration is {summary.Vitals.HydrationPercent}%. Eat or drink before deployment."));
        }
        else
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Vitals",
                $"Hydration is {summary.Vitals.HydrationPercent}%."));
        }

        var energyThreshold = HermesPreRaidReadinessSettings.EnergyWarningPercent.Value;
        if (summary.Vitals.EnergyPercent <= energyThreshold)
        {
            AddUnique(output, seen, new ReadinessFinding(
                summary.Vitals.EnergyPercent <= 20 ? ReadinessSeverity.Critical : ReadinessSeverity.Warning,
                "Vitals",
                $"Energy is {summary.Vitals.EnergyPercent}%. Eat before deployment."));
        }
        else
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Vitals",
                $"Energy is {summary.Vitals.EnergyPercent}%."));
        }

        var healthThreshold = HermesPreRaidReadinessSettings.HealthWarningPercent.Value;
        if (summary.Vitals.HealthPercent <= healthThreshold)
        {
            AddUnique(output, seen, new ReadinessFinding(
                summary.Vitals.HealthPercent <= 45 ? ReadinessSeverity.Critical : ReadinessSeverity.Warning,
                "Vitals",
                $"Total health is {summary.Vitals.HealthPercent}%. Heal before deployment."));
        }
        else
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Vitals",
                $"Total health is {summary.Vitals.HealthPercent}%."));
        }

        var selectedMapKnown = IsKnownSelectedMap(mapName);
        foreach (var requirement in summary.QuestRequirements)
        {
            if (!selectedMapKnown
                || requirement.IsCompleted
                || requirement.AcquireInRaid
                || requirement.IsSatisfied)
            {
                continue;
            }

            if (!MapMatches(requirement.MapName, mapName))
            {
                continue;
            }

            var missing = Math.Max(0d, requirement.RequiredQuantity - requirement.CarriedQuantity);
            var amount = missing > 0d ? $" x{missing:0.##}" : string.Empty;
            var note = string.IsNullOrWhiteSpace(requirement.Note) ? string.Empty : $" {requirement.Note}";
            AddUnique(output, seen, new ReadinessFinding(
                requirement.IsRaidCritical ? ReadinessSeverity.Critical : ReadinessSeverity.Warning,
                "Quest",
                $"{requirement.QuestName}: {requirement.RequiredEquipment}{amount} is not in the raid loadout and may have been left in the stash.{note}"));
        }

        var selectedPlan = selectedMapKnown
            ? summary.RaidPlans.FirstOrDefault(plan => MapMatches(plan.MapName, mapName))
            : null;
        if (selectedPlan is not null)
        {
            foreach (var requirement in selectedPlan.CombinedRequirements.Where(requirement => !requirement.IsSatisfied && !requirement.AcquireInRaid))
            {
                var quests = requirement.QuestNames.Count > 0
                    ? string.Join(", ", requirement.QuestNames)
                    : "Active quest";
                AddUnique(output, seen, new ReadinessFinding(
                    ReadinessSeverity.Critical,
                    "Quest",
                    $"{quests}: missing {requirement.RequiredEquipment} x{requirement.MissingQuantity:0.##} for {selectedPlan.MapName}."));
            }
        }

        if (!summary.Medical.HasHeavyBleedTreatment)
        {
            if (!ContainsFinding(output, "Medical", "heavy"))
            {
                AddUnique(output, seen, new ReadinessFinding(
                    ReadinessSeverity.Critical,
                    "Medical",
                    "No heavy-bleed treatment detected in carried equipment."));
            }
        }
        else
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Medical",
                "Heavy-bleed treatment detected."));
        }

        if (!summary.Medical.HasLightBleedTreatment
            && !ContainsFinding(output, "Medical", "light"))
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Warning,
                "Medical",
                "No light-bleed treatment detected in carried equipment."));
        }
        if (!summary.Medical.HasFractureTreatment
            && !ContainsFinding(output, "Medical", "fracture"))
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Warning,
                "Medical",
                "No fracture treatment detected in carried equipment."));
        }
        if (!summary.Medical.HasPainTreatment
            && !ContainsFinding(output, "Medical", "pain"))
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Warning,
                "Medical",
                "No pain treatment detected in carried equipment."));
        }

        if (summary.Weapons.Count == 0)
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Critical,
                "Weapon",
                "No equipped weapon was detected."));
        }
        else
        {
            foreach (var weapon in summary.Weapons)
            {
                foreach (var warning in weapon.Warnings)
                {
                    AddUnique(output, seen, new ReadinessFinding(
                        warning.Contains("no ", StringComparison.OrdinalIgnoreCase)
                        || warning.Contains("empty", StringComparison.OrdinalIgnoreCase)
                            ? ReadinessSeverity.Critical
                            : ReadinessSeverity.Warning,
                        "Weapon",
                        $"{weapon.Name}: {warning}"));
                }
            }

            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Weapon",
                $"{summary.Weapons.Count} equipped weapon(s) analyzed."));
        }

        if (summary.Armor.Count > 0 && summary.Armor.All(armor => armor.MissingRequiredArmorInsertCount == 0))
        {
            AddUnique(output, seen, new ReadinessFinding(
                ReadinessSeverity.Ready,
                "Armor",
                "No required armor plate slots are empty."));
        }

        return output
            .OrderBy(finding => finding.Severity)
            .ThenBy(finding => finding.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsFinding(
        IEnumerable<ReadinessFinding> findings,
        string categoryToken,
        string messageToken)
        => findings.Any(finding =>
            finding.Category.Contains(categoryToken, StringComparison.OrdinalIgnoreCase)
            && finding.Message.Contains(messageToken, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveMentionedMap(string message, out string mapName)
    {
        mapName = KnownMapNames.FirstOrDefault(name =>
            message.Contains(name, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(mapName);
    }

    private static bool TryResolveQuestWarningMap(
        HermesLoadoutSummaryResponse summary,
        string message,
        out string mapName)
    {
        if (TryResolveMentionedMap(message, out mapName))
        {
            return true;
        }

        var questNameMatches = summary.QuestRequirements
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.MapName)
                                  && !string.IsNullOrWhiteSpace(requirement.QuestName)
                                  && message.Contains(
                                      requirement.QuestName,
                                      StringComparison.OrdinalIgnoreCase))
            .Select(requirement => requirement.MapName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (questNameMatches.Count == 1)
        {
            mapName = questNameMatches[0];
            return true;
        }

        var planMatches = summary.RaidPlans
            .Where(plan => plan.CombinedRequirements.Any(requirement =>
                requirement.QuestNames.Any(questName =>
                    !string.IsNullOrWhiteSpace(questName)
                    && message.Contains(questName, StringComparison.OrdinalIgnoreCase))))
            .Select(plan => plan.MapName)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (planMatches.Count == 1)
        {
            mapName = planMatches[0];
            return true;
        }

        mapName = string.Empty;
        return false;
    }

    private static bool IsKnownSelectedMap(string mapName)
        => KnownMapNames.Any(known => MapMatches(known, mapName));

    private static void AddUnique(
        ICollection<ReadinessFinding> output,
        ISet<string> seen,
        ReadinessFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.Message))
        {
            return;
        }

        var key = finding.Category + "|" + NormalizeFinding(finding.Message);
        if (seen.Add(key))
        {
            output.Add(finding);
        }
    }

    private static string NormalizeFinding(string value)
        => new string(
            value.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static bool MapMatches(string candidate, string selected)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }

        static string Normalize(string value)
            => new string(
                    value.Where(char.IsLetterOrDigit)
                        .Select(char.ToLowerInvariant)
                        .ToArray())
                .Replace("thelab", "labs");

        var left = Normalize(candidate);
        var right = Normalize(selected);
        return left.Equals(right, StringComparison.Ordinal)
               || left.IndexOf(right, StringComparison.Ordinal) >= 0
               || right.IndexOf(left, StringComparison.Ordinal) >= 0;
    }

    private string ResolveSelectedMap()
    {
        if (_insuranceScreen is null)
        {
            return "Current raid";
        }

        var visibleTexts = _insuranceScreen.GetComponentsInChildren<TMP_Text>(true)
            .Select(text => text.text)
            .Concat(_insuranceScreen.GetComponentsInChildren<Text>(true).Select(text => text.text))
            .Where(text => !string.IsNullOrWhiteSpace(text));
        foreach (var text in visibleTexts)
        {
            foreach (var map in KnownMapNames)
            {
                if (text.Contains(map, StringComparison.OrdinalIgnoreCase))
                {
                    return map.Equals("Labs", StringComparison.OrdinalIgnoreCase) ? "The Lab" : map;
                }
            }
        }

        var reflected = FindMapInMembers(_insuranceScreen, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return string.IsNullOrWhiteSpace(reflected) ? "Current raid" : reflected;
    }

    private static string? FindMapInMembers(object? owner, int depth, ISet<object> visited)
    {
        if (owner is null || depth > 2 || owner is UnityEngine.Object unityObject && unityObject == null)
        {
            return null;
        }

        if (!owner.GetType().IsValueType && !visited.Add(owner))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in owner.GetType().GetFields(flags))
        {
            object? value;
            try
            {
                value = field.GetValue(owner);
            }
            catch
            {
                continue;
            }

            if (value is string text)
            {
                var map = KnownMapNames.FirstOrDefault(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (map is not null)
                {
                    return map.Equals("Labs", StringComparison.OrdinalIgnoreCase) ? "The Lab" : map;
                }
            }
            else if (value is not null
                     && !field.FieldType.IsPrimitive
                     && field.FieldType != typeof(decimal)
                     && !typeof(Delegate).IsAssignableFrom(field.FieldType))
            {
                var nested = FindMapInMembers(value, depth + 1, visited);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private void ClearFindings()
    {
        if (_findingsContent is null)
        {
            return;
        }

        for (var index = _findingsContent.childCount - 1; index >= 0; index--)
        {
            Destroy(_findingsContent.GetChild(index).gameObject);
        }
    }

    private void BuildFailureFinding()
    {
        ClearFindings();
        AddSectionHeader("CHECK UNAVAILABLE");
        AddFindingCard(new ReadinessFinding(
            ReadinessSeverity.Warning,
            "HERMES",
            "The live readiness snapshot could not be loaded. Continue still uses EFT's original raid flow."));
        _hasRenderedSnapshot = true;
    }

    private void AddSectionHeader(string label)
    {
        if (_findingsContent is null)
        {
            return;
        }

        var text = CreateText(_findingsContent, label, 19, FontStyles.Bold, TextAlignmentOptions.Left);
        var layout = text.gameObject.AddComponent<LayoutElement>();
        layout.preferredHeight = 30f;
        layout.minHeight = 30f;
    }

    private void AddFindingCard(ReadinessFinding finding)
    {
        if (_findingsContent is null)
        {
            return;
        }

        var background = finding.Severity switch
        {
            ReadinessSeverity.Critical => new Color(0.24f, 0.075f, 0.06f, 0.96f),
            ReadinessSeverity.Warning => new Color(0.23f, 0.17f, 0.055f, 0.94f),
            _ => new Color(0.08f, 0.16f, 0.10f, 0.92f)
        };
        var card = CreatePanel(_findingsContent, "Finding", background);
        var layout = card.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 10, 10);
        layout.spacing = 3f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        card.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var heading = CreateText(card.transform, finding.Category.ToUpperInvariant(), 17, FontStyles.Bold, TextAlignmentOptions.Left);
        heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;
        var message = CreateText(card.transform, finding.Message, 17, FontStyles.Normal, TextAlignmentOptions.Left);
        message.enableWordWrapping = true;
        message.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    private void SetStatus(string value)
    {
        if (_statusText is not null)
        {
            _statusText.text = value;
        }
    }

    private static GameObject CreatePanel(Transform parent, string name, Color color)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = color;
        return panel;
    }

    private static TMP_Text CreateText(
        Transform parent,
        string value,
        float size,
        FontStyles style,
        TextAlignmentOptions alignment)
    {
        var textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        var text = textObject.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = alignment;
        text.color = new Color(0.88f, 0.86f, 0.78f, 1f);
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        return text;
    }

    private static void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action)
    {
        var buttonObject = new GameObject(
            label.Replace(' ', '_'),
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = new Color(0.18f, 0.19f, 0.18f, 1f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = buttonObject.GetComponent<Image>();
        button.onClick.AddListener(action);
        var buttonElement = buttonObject.GetComponent<LayoutElement>();
        buttonElement.minWidth = 170f;
        buttonElement.preferredWidth = 205f;
        buttonElement.minHeight = 40f;
        buttonElement.preferredHeight = 40f;
        buttonElement.flexibleWidth = 0f;
        buttonElement.flexibleHeight = 0f;

        var text = CreateText(buttonObject.transform, label, 15, FontStyles.Bold, TextAlignmentOptions.Center);
        var rect = (RectTransform)text.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(8f, 4f);
        rect.offsetMax = new Vector2(-8f, -4f);
    }

    private enum ReadinessSeverity
    {
        Critical = 0,
        Warning = 1,
        Ready = 2
    }

    private sealed class ReadinessFinding
    {
        internal ReadinessFinding(ReadinessSeverity severity, string category, string message)
        {
            Severity = severity;
            Category = category;
            Message = message;
        }

        internal ReadinessSeverity Severity { get; }
        internal string Category { get; }
        internal string Message { get; }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
