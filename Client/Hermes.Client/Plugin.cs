using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Hermes.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.amightytank.hermes.client";
    public const string PluginName = "HERMES Client";
    public const string PluginVersion = "2.0.0";

    internal static ManualLogSource Log { get; private set; } = null!;
    internal static Plugin? Instance { get; private set; }
    internal static HermesClientSettings Settings { get; private set; } = null!;

    private HermesWindow? _window;
    private HermesWorkspaceSnapshotCoordinator? _snapshotCoordinator;

    internal HermesWindow Window => _window
                                   ?? throw new InvalidOperationException("HERMES window controller is not initialized.");

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        Settings = new HermesClientSettings();
        Settings.Bind(Config);
        HermesPreRaidReadinessSettings.Bind(Config);
        _window = new HermesWindow();
        _snapshotCoordinator = HermesWorkspaceSnapshotCoordinator.Configure(_window);

        TryEnable("Ask HERMES context actions", () => new AskHermesContextMenuPatch().Enable());
        TryEnable("Character and in-raid inventory tab injection", () => new HermesNativeInventoryScreenPatch().Enable());
        TryEnable("InventoryScreen close lifecycle cleanup", () => new HermesNativeInventoryScreenClosePatch().Enable());
        TryEnable("native inventory-tab icon correction and click preservation", () => new HermesNativeTabIconFixPatch().Enable());
        TryEnable("native inventory-tab exclusive header state", () => new HermesNativeTabHeaderFixPatch().Enable());

        // The native workspace owns all visible HERMES rendering and pins new item-search results to the top.
        // Legacy controller methods remain request/state owners only; every visible workspace is
        // built by HermesNativeWorkspaceBody under the EFT Canvas.

        TryEnable("native Ragfair asset capture", () => new HermesRagfairNativeAssetPatch().Enable());
        TryEnable("native Items & Market search toolbar", () => new HermesNativeItemSearchBarSuppressionPatch().Enable());
        TryEnable("native EFT notification click routing", () => new HermesNativeNotificationClickPatch().Enable());
        TryEnable(
            "pre-raid readiness map-selection preparation",
            () => new HermesPreRaidMapSelectionPrefetchPatch().Enable());
        TryEnable(
            "PMC pre-raid readiness native Insurance interception",
            () => new HermesPreRaidInsuranceNextPatch().Enable());
        TryEnable(
            "pre-raid confirmation Back return to readiness",
            () => new HermesPreRaidConfirmationBackPatch().Enable());

        HermesRagfairNativeAssets.TryResolve();

        Logger.LogInfo(
            $"HERMES {HermesVersionInfo.DisplayVersion} loaded. "
            + $"Native Ragfair templates ready: {HermesRagfairNativeAssets.Ready}. "
            + $"Inventory-only workspace: {Settings.UseNativeInventoryTabs.Value}. "
            + $"Toggle shortcut: {Settings.ToggleWindowShortcut.Value}.");
    }

    private void TryEnable(string label, Action enable)
    {
        try
        {
            enable();
            Logger.LogInfo($"HERMES {label} enabled.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"HERMES {label} could not be enabled: {ex}");
        }
    }

    internal void OpenForInventoryItem(string profileItemId)
    {
        if (_window is null || string.IsNullOrWhiteSpace(profileItemId))
        {
            return;
        }

        _window.OpenForInventoryItem(profileItemId);
        HermesGlobalNavigation.RequestOpen();
    }

    internal void OpenForStashItem(string selectionKey, string itemName)
    {
        if (_window is null
            || (string.IsNullOrWhiteSpace(selectionKey) && string.IsNullOrWhiteSpace(itemName)))
        {
            return;
        }

        _window.OpenForStashItem(selectionKey, itemName);
        HermesGlobalNavigation.RequestOpen();
    }

    internal void OpenForStashItem(string selectionKey)
    {
        OpenForStashItem(selectionKey, string.Empty);
    }

    internal void OpenForPreviewItem(string templateId, string sourceLabel)
    {
        if (_window is null || string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        _window.OpenForPreviewItem(templateId, sourceLabel);
        HermesGlobalNavigation.RequestOpen();
    }

    internal void OpenNoticeTarget(string targetTab)
    {
        if (_window is null || string.IsNullOrWhiteSpace(targetTab))
        {
            return;
        }

        _window.OpenNativeNoticeTarget(targetTab);
        HermesGlobalNavigation.RequestOpen();
    }

    private void Update()
    {
        HermesNativeScreenRegistry.TickDiscovery();
        HermesGlobalNavigation.Tick();
        _snapshotCoordinator?.Tick();
        _window?.Tick();

        if (Settings.ToggleWindowShortcut.Value.IsDown())
        {
            _window?.Toggle();
        }
    }
}
