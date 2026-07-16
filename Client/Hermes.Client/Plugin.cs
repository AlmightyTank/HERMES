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
    public const string PluginVersion = "0.1.0";

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
        _window = new HermesWindow();
        _snapshotCoordinator = HermesWorkspaceSnapshotCoordinator.Configure(_window);

        TryEnable("Ask HERMES context actions", () => new AskHermesContextMenuPatch().Enable());
        TryEnable("Character and in-raid inventory tab injection", () => new HermesNativeInventoryScreenPatch().Enable());
        TryEnable("InventoryScreen close lifecycle cleanup", () => new HermesNativeInventoryScreenClosePatch().Enable());
        TryEnable("native inventory-tab icon correction", () => new HermesNativeTabIconFixPatch().Enable());

        try
        {
            new HermesWindowRaidPlannerDrawPatch().Enable();
            new HermesWindowRaidPlannerRefreshPatch().Enable();
            new HermesWindowRaidPlannerClearPatch().Enable();
            new HermesLoadoutTabsWithoutRaidPlannerPatch().Enable();
            new HermesLoadoutOpenViewSeparationPatch().Enable();
            new HermesLoadoutDefaultViewSeparationPatch().Enable();
            new HermesLoadoutSummaryViewGuardPatch().Enable();
            Logger.LogInfo("HERMES Loadout and Raid Planner workspace separation enabled.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"HERMES Loadout/Raid Planner separation could not be enabled: {ex}");
        }

        try
        {
            HermesEftThemeBootstrap.Enable();
            Logger.LogInfo("HERMES EFT body-panel theme enabled for the staged native conversion.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"HERMES EFT body-panel theme could not be enabled: {ex}");
        }

        TryEnable("server revision workspace loading", () => new HermesSnapshotPresentationOpenPatch().Enable());
        TryEnable("native Ragfair asset capture", () => new HermesRagfairNativeAssetPatch().Enable());
        TryEnable("native Items & Market search toolbar", () => new HermesNativeItemSearchBarSuppressionPatch().Enable());
        TryEnable("native EFT notification click routing", () => new HermesNativeNotificationClickPatch().Enable());

        HermesRagfairNativeAssets.TryResolve();

        Logger.LogInfo(
            $"HERMES 0.1.0-alpha12.7.3.7 quiet server revisions loaded. "
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
    }

    internal void OpenForStashItem(string profileItemId)
    {
        OpenForInventoryItem(profileItemId);
    }

    internal void OpenForPreviewItem(string templateId, string sourceLabel)
    {
        if (_window is null || string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        _window.OpenForPreviewItem(templateId, sourceLabel);
    }

    internal void OpenNoticeTarget(string targetTab)
    {
        if (_window is null || string.IsNullOrWhiteSpace(targetTab))
        {
            return;
        }

        _window.OpenNativeNoticeTarget(targetTab);
    }

    private void Update()
    {
        HermesNativeScreenRegistry.TickDiscovery();
        _snapshotCoordinator?.Tick();
        _window?.Tick();

        if (Settings.ToggleWindowShortcut.Value.IsDown())
        {
            _window?.Toggle();
        }
    }
}
