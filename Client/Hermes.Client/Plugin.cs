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

    private HermesWindow? _window;

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        _window = new HermesWindow();

        try
        {
            new AskHermesContextMenuPatch().Enable();
            Logger.LogInfo("Ask HERMES inventory, trader, and flea context actions enabled.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Ask HERMES context action could not be enabled: {ex}");
        }

        Logger.LogInfo("HERMES 0.1.0-alpha10.2 loaded. Press F8 in the main menu.");
    }

    internal void OpenForStashItem(string profileItemId)
    {
        if (_window is null || string.IsNullOrWhiteSpace(profileItemId))
        {
            return;
        }

        _window.OpenForStashItem(profileItemId);
    }

    internal void OpenForPreviewItem(string templateId, string sourceLabel)
    {
        if (_window is null || string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        _window.OpenForPreviewItem(templateId, sourceLabel);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _window?.Toggle();
        }
    }

    private void OnGUI()
    {
        _window?.Draw();
    }
}
