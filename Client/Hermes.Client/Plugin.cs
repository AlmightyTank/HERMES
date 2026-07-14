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

    private HermesWindow? _window;

    private void Awake()
    {
        Log = Logger;
        _window = new HermesWindow();
        Logger.LogInfo("HERMES 0.1.0-alpha7.1 loaded. Press F8 in the main menu.");
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
