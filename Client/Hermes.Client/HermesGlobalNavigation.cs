using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hermes.Client;

/// <summary>
/// Carries Ask HERMES, F8, embedded Ask buttons, and notification navigation
/// across EFT menu screens. The primary route invokes the same MenuTaskBar
/// navigation event used by EFT's Character toggle and ToggleInventory command.
/// </summary>
internal static class HermesGlobalNavigation
{
    private const float PendingLifetimeSeconds = 20f;
    private const float NavigationRetrySeconds = 0.75f;

    private static readonly FieldInfo? MenuNavigationEventField = ResolveMenuNavigationEventField();

    private static bool _pending;
    private static float _pendingUntil;
    private static float _nextNavigationAttempt;
    private static bool _loggedNavigationAttempt;
    private static bool _loggedNativeRouteFailure;

    internal static void RequestOpen()
    {
        if (!Plugin.Settings.UseNativeInventoryTabs.Value)
        {
            return;
        }

        // Character or in-raid InventoryScreen is already active. Selecting HERMES
        // here preserves the current screen instead of navigating away and back.
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

        // InventoryScreen.Show/host initialization can finish several frames after
        // EFT accepts the native Character navigation event.
        if (HermesNativeScreenRegistry.TryShow())
        {
            Plugin.Log?.LogDebug("HERMES completed queued native Character navigation.");
            ClearPending();
            return;
        }

        if (Time.unscaledTime >= _pendingUntil)
        {
            Plugin.Log?.LogWarning(
                "HERMES could not open the Character screen for the queued navigation request. "
                + "The requested item or notice target remains selected and will be visible the next time HERMES opens.");
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
                "HERMES invoked EFT's native EMenuType.Player navigation and is waiting for InventoryScreen initialization.");
        }
    }

    private static bool TryActivateCharacterScreen()
    {
        // Preferred path: this is the same Action<EMenuType, bool> that
        // MenuTaskBar's Character toggle and ECommand.ToggleInventory invoke.
        if (TryInvokeNativeMenuNavigation())
        {
            return true;
        }

        // Compatibility fallback for a future EFT mapping where the taskbar event
        // field cannot be resolved. This retains the previous visual-control route
        // without making it the normal navigation mechanism.
        return TryInvokeVisibleCharacterControl();
    }

    private static bool TryInvokeNativeMenuNavigation()
    {
        try
        {
            if (!MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                return false;
            }

            var preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
            var taskBar = preloader?.MenuTaskBar;
            if (taskBar is null || MenuNavigationEventField is null)
            {
                return false;
            }

            if (MenuNavigationEventField.GetValue(taskBar) is not Action<EMenuType, bool> navigate)
            {
                return false;
            }

            CloseOpenItemMenus();
            navigate(EMenuType.Player, true);
            return true;
        }
        catch (Exception ex)
        {
            if (!_loggedNativeRouteFailure)
            {
                _loggedNativeRouteFailure = true;
                Plugin.Log?.LogWarning(
                    $"HERMES native MenuTaskBar navigation was unavailable; using the visual fallback. {ex.Message}");
            }

            return false;
        }
    }

    private static FieldInfo? ResolveMenuNavigationEventField()
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;

        return typeof(MenuTaskBar)
            .GetFields(flags)
            .FirstOrDefault(field => field.FieldType == typeof(Action<EMenuType, bool>));
    }

    private static void CloseOpenItemMenus()
    {
        try
        {
            var itemUiContext = ItemUiContext.Instance;
            if (itemUiContext?.ContextMenu != null
                && itemUiContext.ContextMenu.gameObject.activeSelf)
            {
                itemUiContext.ContextMenu.Close();
            }

            itemUiContext?.CloseSelectItemMenu();
        }
        catch
        {
            // Navigation must not fail because a source screen already disposed its
            // transient item menu while the Ask HERMES callback was running.
        }
    }

    private static bool TryInvokeVisibleCharacterControl()
    {
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
