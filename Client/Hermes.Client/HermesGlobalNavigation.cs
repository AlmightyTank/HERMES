using System;
using System.Collections.Generic;
using System.Linq;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Carries Ask HERMES and notification navigation across EFT menu screens.
/// When no InventoryScreen is currently open, it activates the native Character
/// taskbar button and waits for the shared InventoryScreen host to become ready.
/// </summary>
internal static class HermesGlobalNavigation
{
    private const float PendingLifetimeSeconds = 20f;
    private const float NavigationRetrySeconds = 0.75f;

    private static bool _pending;
    private static float _pendingUntil;
    private static float _nextNavigationAttempt;
    private static bool _loggedNavigationAttempt;

    internal static void RequestOpen()
    {
        if (!Plugin.Settings.UseNativeInventoryTabs.Value)
        {
            return;
        }

        if (HermesNativeScreenRegistry.TryShow())
        {
            ClearPending();
            return;
        }

        _pending = true;
        _pendingUntil = Time.unscaledTime + PendingLifetimeSeconds;
        _nextNavigationAttempt = Time.unscaledTime;
        _loggedNavigationAttempt = false;
    }

    internal static void Tick()
    {
        if (!_pending)
        {
            return;
        }

        if (HermesNativeScreenRegistry.TryShow())
        {
            Plugin.Log?.LogDebug("HERMES completed queued cross-screen navigation.");
            ClearPending();
            return;
        }

        if (Time.unscaledTime >= _pendingUntil)
        {
            Plugin.Log?.LogWarning(
                "HERMES could not open the Character screen for the queued navigation request. The requested item or notice target remains selected and will be visible the next time HERMES opens.");
            ClearPending();
            return;
        }

        if (Time.unscaledTime < _nextNavigationAttempt)
        {
            return;
        }

        _nextNavigationAttempt = Time.unscaledTime + NavigationRetrySeconds;
        if (TryActivateCharacterScreen() && !_loggedNavigationAttempt)
        {
            _loggedNavigationAttempt = true;
            Plugin.Log?.LogDebug(
                "HERMES activated EFT's native Character navigation and is waiting for InventoryScreen initialization.");
        }
    }

    private static bool TryActivateCharacterScreen()
    {
        // DefaultUIButton is used by many EFT taskbar controls. Invoking its public
        // UnityEvent follows the same screen-controller callback as a normal click.
        var defaultButton = Resources.FindObjectsOfTypeAll<DefaultUIButton>()
            .Where(IsUsable)
            .Where(button => IsCharacterControl(button.transform, button.HeaderText))
            .OrderByDescending(button => Score(button.transform))
            .FirstOrDefault();
        if (defaultButton != null)
        {
            defaultButton.OnClick.Invoke();
            return true;
        }

        // Some builds expose the taskbar item as a standard Unity Button.
        var unityButton = Resources.FindObjectsOfTypeAll<Button>()
            .Where(IsUsable)
            .Where(button => IsCharacterControl(button.transform, null))
            .OrderByDescending(button => Score(button.transform))
            .FirstOrDefault();
        if (unityButton != null)
        {
            unityButton.onClick.Invoke();
            return true;
        }

        // Defensive fallback for toggle-based taskbars.
        var toggle = Resources.FindObjectsOfTypeAll<Toggle>()
            .Where(IsUsable)
            .Where(control => IsCharacterControl(control.transform, null))
            .OrderByDescending(control => Score(control.transform))
            .FirstOrDefault();
        if (toggle != null)
        {
            toggle.isOn = true;
            return true;
        }

        return false;
    }

    private static bool IsUsable(DefaultUIButton button)
    {
        return button != null
               && button.gameObject != null
               && button.gameObject.activeInHierarchy
               && button.Interactable
               && IsRuntimeObject(button.gameObject)
               && IsVisibleThroughCanvasGroups(button.transform);
    }

    private static bool IsUsable(Selectable selectable)
    {
        return selectable != null
               && selectable.gameObject != null
               && selectable.gameObject.activeInHierarchy
               && selectable.interactable
               && IsRuntimeObject(selectable.gameObject)
               && IsVisibleThroughCanvasGroups(selectable.transform);
    }

    private static bool IsRuntimeObject(GameObject gameObject)
    {
        var scene = gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static bool IsVisibleThroughCanvasGroups(Transform transform)
    {
        var groups = transform.GetComponentsInParent<CanvasGroup>(true);
        return groups.All(group => group == null || group.alpha > 0.01f);
    }

    private static bool IsCharacterControl(Transform transform, string? declaredText)
    {
        if (IsCharacterText(declaredText))
        {
            return true;
        }

        if (transform.gameObject.name.Contains("character", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var text in transform.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text != null && IsCharacterText(text.text))
            {
                return true;
            }
        }

        foreach (var text in transform.GetComponentsInChildren<Text>(true))
        {
            if (text != null && IsCharacterText(text.text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCharacterText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = new string(text
            .Where(character => !char.IsWhiteSpace(character))
            .ToArray());
        return normalized.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase);
    }

    private static float Score(Transform transform)
    {
        var score = 0f;
        if (transform.gameObject.name.Contains("character", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000f;
        }

        if (transform is RectTransform rect)
        {
            score += Mathf.Abs(rect.rect.width);
            // The main-menu taskbar is near the bottom of the screen.
            score += Mathf.Max(0f, -rect.position.y) * 0.001f;
        }

        return score;
    }

    private static void ClearPending()
    {
        _pending = false;
        _pendingUntil = 0f;
        _nextNavigationAttempt = 0f;
        _loggedNavigationAttempt = false;
    }
}
