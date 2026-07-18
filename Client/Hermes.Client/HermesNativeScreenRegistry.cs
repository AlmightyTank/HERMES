using EFT.UI;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Tracks usable Character/in-raid inventory screens and keeps low-frequency discovery
/// as a fallback. The authoritative build point is the 10-parameter InventoryScreen.Show
/// postfix, where EFT has created its native GClass3808 tab group.
/// </summary>
internal static class HermesNativeScreenRegistry
{
    private const float DiscoveryIntervalSeconds = 3f;
    private const float HostedDiscoveryIntervalSeconds = 15f;

    private static readonly List<WeakReference<HermesNativeScreenHost>> Hosts = [];
    private static float _nextDiscoveryTime;
    private static bool _loggedFirstDiscovery;
    private static WeakReference<HermesNativeScreenHost>? _lastShownHost;

    internal static void Register(HermesNativeScreenHost host)
    {
        Cleanup();
        if (Hosts.Any(reference => reference.TryGetTarget(out var existing) && existing == host))
        {
            return;
        }

        Hosts.Add(new WeakReference<HermesNativeScreenHost>(host));
    }


    internal static void MarkShown(HermesNativeScreenHost host)
    {
        if (host == null)
        {
            return;
        }

        _lastShownHost = new WeakReference<HermesNativeScreenHost>(host);
    }

    internal static void MarkClosing(HermesNativeScreenHost host)
    {
        if (_lastShownHost != null
            && _lastShownHost.TryGetTarget(out var lastShown)
            && lastShown == host)
        {
            _lastShownHost = null;
        }
    }

    internal static void Unregister(HermesNativeScreenHost host)
    {
        if (_lastShownHost != null
            && _lastShownHost.TryGetTarget(out var lastShown)
            && lastShown == host)
        {
            _lastShownHost = null;
        }

        for (var index = Hosts.Count - 1; index >= 0; index--)
        {
            if (!Hosts[index].TryGetTarget(out var existing) || existing == host)
            {
                Hosts.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Called from Plugin.Update. Finds both active and inactive runtime InventoryScreen
    /// objects so the Show postfix can refresh hosts that already have a component.
    /// </summary>
    internal static void TickDiscovery()
    {
        if (!Plugin.Settings.UseNativeInventoryTabs.Value
            || Time.unscaledTime < _nextDiscoveryTime)
        {
            return;
        }

        Cleanup();
        if (HasActiveRuntimeHost())
        {
            // InventoryScreen.Show is the authoritative attachment path. Once an active runtime
            // host exists, the expensive global Unity object scan is only a very infrequent
            // fallback for unusual modded screen lifecycles.
            _nextDiscoveryTime = Time.unscaledTime + HostedDiscoveryIntervalSeconds;
            return;
        }

        _nextDiscoveryTime = Time.unscaledTime + DiscoveryIntervalSeconds;

        InventoryScreen[] screens;
        try
        {
            screens = Resources.FindObjectsOfTypeAll<InventoryScreen>();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"HERMES InventoryScreen discovery failed: {ex.Message}");
            return;
        }

        var attached = 0;
        foreach (var screen in screens)
        {
            if (screen == null || screen.gameObject == null)
            {
                continue;
            }

            // Ignore prefab assets that are not part of a loaded scene. Runtime Character
            // and raid inventory screens always belong to a valid loaded scene.
            var scene = screen.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }

            if (screen.GetComponent<HermesNativeScreenHost>() != null)
            {
                continue;
            }

            HermesNativeScreenHost.Attach(screen);
            attached++;
        }

        Cleanup();
        if (attached > 0)
        {
            Plugin.Log?.LogInfo(
                $"HERMES discovered and attached to {attached} existing InventoryScreen instance(s). Registered hosts: {Hosts.Count}.");
        }
        else if (!_loggedFirstDiscovery)
        {
            _loggedFirstDiscovery = true;
            Plugin.Log?.LogDebug(
                $"HERMES native InventoryScreen discovery initialized. Screens found: {screens.Length}; registered hosts: {Hosts.Count}.");
        }
    }

    internal static bool TryShow()
    {
        TickDiscovery();
        var host = GetBestHost();
        if (host == null)
        {
            return false;
        }

        host.ShowHermes();
        return host.IsShowingHermes;
    }

    internal static bool TryToggle()
    {
        TickDiscovery();
        var host = GetBestHost();
        if (host == null)
        {
            return false;
        }

        var wasShowing = host.IsShowingHermes;
        host.ToggleHermes();
        return wasShowing || host.IsShowingHermes;
    }


    internal static bool TryReturnToInventory()
    {
        TickDiscovery();
        var host = GetBestHost();
        if (host == null || !host.IsShowingHermes)
        {
            return false;
        }

        host.HideHermes(true);
        return true;
    }

    internal static void NotifyWindowHidden(HermesNativeScreenHost source)
    {
        foreach (var host in GetLiveHosts())
        {
            if (host != source && host.IsShowingHermes)
            {
                host.HideHermes(false);
            }
        }
    }

    private static bool HasActiveRuntimeHost()
    {
        foreach (var reference in Hosts)
        {
            if (reference.TryGetTarget(out var host)
                && host != null
                && host.IsScreenAvailable
                && host.IsScreenActive)
            {
                return true;
            }
        }

        return false;
    }

    private static HermesNativeScreenHost? GetBestHost()
    {
        if (_lastShownHost != null
            && _lastShownHost.TryGetTarget(out var lastShown)
            && lastShown != null
            && lastShown.IsScreenAvailable
            && lastShown.IsScreenActive)
        {
            return lastShown;
        }

        return GetLiveHosts()
            .Where(host => host.IsScreenAvailable && host.IsScreenActive)
            .FirstOrDefault();
    }

    private static IReadOnlyList<HermesNativeScreenHost> GetLiveHosts()
    {
        Cleanup();
        var output = new List<HermesNativeScreenHost>();
        foreach (var reference in Hosts)
        {
            if (reference.TryGetTarget(out var host) && host != null)
            {
                output.Add(host);
            }
        }

        return output;
    }

    private static void Cleanup()
    {
        for (var index = Hosts.Count - 1; index >= 0; index--)
        {
            if (!Hosts[index].TryGetTarget(out var host) || host == null)
            {
                Hosts.RemoveAt(index);
            }
        }
    }
}
