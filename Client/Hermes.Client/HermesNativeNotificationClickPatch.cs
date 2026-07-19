using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;

namespace Hermes.Client;

/// <summary>
/// Hooks EFT's own BaseNotificationView click path. Only notifications registered
/// by HermesNativeNotificationBridge are handled; every other EFT notification is untouched.
/// Left click keeps EFT's normal close behavior after routing to HERMES. Right click dismisses
/// HERMES alerts without opening a workspace.
/// </summary>
internal sealed class HermesNativeNotificationClickPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BaseNotificationView).GetMethod(
                   "OnPointerClick",
                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               ?? throw new MissingMethodException(typeof(BaseNotificationView).FullName, "OnPointerClick");
    }

    [PatchPrefix]
    private static bool Prefix(object __instance, object __0)
    {
        try
        {
            var notification = HermesNativeNotificationBridge.ReadNotificationFromView(__instance);
            if (IsRightClick(__0))
            {
                return !HermesNativeNotificationBridge.TryDismissNativeClick(notification);
            }

            if (IsLeftClick(__0))
            {
                HermesNativeNotificationBridge.TryHandleNativeClick(notification);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native notification click patch failed: {ex}");
        }

        return true;
    }

    private static bool IsLeftClick(object? pointerEventData)
        => string.IsNullOrWhiteSpace(ReadButton(pointerEventData))
           || ReadButton(pointerEventData).Equals("Left", StringComparison.OrdinalIgnoreCase);

    private static bool IsRightClick(object? pointerEventData)
        => ReadButton(pointerEventData).Equals("Right", StringComparison.OrdinalIgnoreCase);

    private static string ReadButton(object? pointerEventData)
    {
        if (pointerEventData is null)
        {
            return string.Empty;
        }

        return pointerEventData.GetType()
            .GetProperty("button", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(pointerEventData)
            ?.ToString()
            ?? string.Empty;
    }
}
