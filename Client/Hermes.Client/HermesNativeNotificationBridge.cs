using System.Reflection;
using EFT.Communications;
using EFT.UI;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Publishes one-line HERMES alerts through EFT's native notification queue.
/// HERMES tracks only its own descriptions so clicking a card can open the
/// corresponding read-only inventory workspace.
/// </summary>
internal static class HermesNativeNotificationBridge
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, NativeRegistration> ById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, NativeRegistration> ByDescription = new(StringComparer.Ordinal);
    private static readonly FieldInfo? NotificationField = typeof(BaseNotificationView).GetField(
        "_notification",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static Action<string, string>? _clicked;
    private static Action<string>? _dismissed;

    public static int ActiveCount
    {
        get
        {
            lock (Sync)
            {
                return ById.Count;
            }
        }
    }

    public static void Configure(Action<string, string> clicked, Action<string>? dismissed = null)
    {
        _clicked = clicked;
        _dismissed = dismissed;
    }

    public static bool TryShow(
        string noticeId,
        string severity,
        string category,
        string title,
        string message,
        string targetTab,
        out string description)
    {
        _ = message;
        description = BuildDescription(title);
        if (string.IsNullOrWhiteSpace(noticeId)
            || string.IsNullOrWhiteSpace(description)
            || !IsNotificationManagerReady())
        {
            return false;
        }

        var registration = new NativeRegistration(noticeId, description, targetTab);
        lock (Sync)
        {
            if (ById.ContainsKey(noticeId))
            {
                return true;
            }

            if (ById.Count > 0 || ByDescription.ContainsKey(description))
            {
                return false;
            }

            ById[noticeId] = registration;
            ByDescription[description] = registration;
        }

        try
        {
            NotificationManagerClass.DisplayMessageNotification(
                description,
                ENotificationDurationType.Infinite,
                ResolveIcon(severity, category),
                null);
            return true;
        }
        catch (Exception ex)
        {
            RemoveRegistration(registration);
            Plugin.Log?.LogWarning($"HERMES could not publish an EFT notification: {ex.Message}");
            return false;
        }
    }

    public static bool TryHandleNativeClick(object? notification)
    {
        var description = TryReadDescription(notification);
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        NativeRegistration? registration;
        lock (Sync)
        {
            if (!ByDescription.TryGetValue(description, out registration))
            {
                return false;
            }

            ByDescription.Remove(description);
            ById.Remove(registration.NoticeId);
        }

        try
        {
            _clicked?.Invoke(registration.NoticeId, registration.TargetTab);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native notification click failed: {ex}");
        }

        return true;
    }

    public static bool TryDismissNativeClick(object? notification)
    {
        var description = TryReadDescription(notification);
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        NativeRegistration? registration;
        lock (Sync)
        {
            if (!ByDescription.TryGetValue(description, out registration))
            {
                return false;
            }

            ByDescription.Remove(description);
            ById.Remove(registration.NoticeId);
        }

        HideNativeView(registration.Description);
        try
        {
            _dismissed?.Invoke(registration.NoticeId);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native notification dismiss failed: {ex}");
        }

        return true;
    }

    public static void Dismiss(string noticeId)
    {
        NativeRegistration? registration;
        lock (Sync)
        {
            if (!ById.TryGetValue(noticeId, out registration))
            {
                return;
            }

            ById.Remove(noticeId);
            ByDescription.Remove(registration.Description);
        }

        HideNativeView(registration.Description);
    }

    public static void DismissAll()
    {
        NativeRegistration[] registrations;
        lock (Sync)
        {
            registrations = ById.Values.ToArray();
            ById.Clear();
            ByDescription.Clear();
        }

        foreach (var registration in registrations)
        {
            HideNativeView(registration.Description);
        }
    }

    private static string BuildDescription(string title)
    {
        var cleanTitle = NormalizeLine(title);
        return string.IsNullOrWhiteSpace(cleanTitle)
            ? "HERMES"
            : $"HERMES • {cleanTitle}";
    }

    private static string NormalizeLine(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static ENotificationIconType ResolveIcon(string severity, string category)
    {
        if (severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("error", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            return ENotificationIconType.Alert;
        }

        if (category.Contains("hideout", StringComparison.OrdinalIgnoreCase)
            || category.Contains("craft", StringComparison.OrdinalIgnoreCase))
        {
            return ENotificationIconType.Hideout;
        }

        return ENotificationIconType.Note;
    }

    private static bool IsNotificationManagerReady()
    {
        try
        {
            var singletonType = typeof(NotificationManagerClass).Assembly
                .GetType("Comfort.Common.Singleton`1", false);
            if (singletonType is null)
            {
                return true;
            }

            var closedType = singletonType.MakeGenericType(typeof(NotificationManagerClass));
            var property = closedType.GetProperty(
                "Instantiated",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var value = property?.GetValue(null);
            return value is not bool instantiated || instantiated;
        }
        catch
        {
            return true;
        }
    }

    private static string? TryReadDescription(object? notification)
    {
        if (notification is NotificationAbstractClass native)
        {
            return native.Description;
        }

        return notification?.GetType()
            .GetProperty("Description", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(notification)
            ?.ToString();
    }

    internal static object? ReadNotificationFromView(object view)
    {
        return NotificationField?.GetValue(view);
    }

    private static void HideNativeView(string description)
    {
        try
        {
            foreach (var view in Resources.FindObjectsOfTypeAll<BaseNotificationView>())
            {
                var notification = ReadNotificationFromView(view);
                if (string.Equals(TryReadDescription(notification), description, StringComparison.Ordinal))
                {
                    view.HideNotification(true);
                }
            }
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log?.LogWarning($"HERMES could not close a native EFT notification view: {ex.Message}");
            }
        }
    }

    private static void RemoveRegistration(NativeRegistration registration)
    {
        lock (Sync)
        {
            ById.Remove(registration.NoticeId);
            ByDescription.Remove(registration.Description);
        }
    }

    private sealed record NativeRegistration(
        string NoticeId,
        string Description,
        string TargetTab);
}
