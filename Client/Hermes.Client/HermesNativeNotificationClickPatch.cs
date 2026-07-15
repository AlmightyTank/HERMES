using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;

namespace Hermes.Client;

/// <summary>
/// Hooks EFT's own BaseNotificationView click path. Only notifications registered
/// by HermesNativeNotificationBridge are handled; every other EFT notification is untouched.
/// The original EFT method still runs and closes the infinite notification normally.
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
    private static void Prefix(object __instance, object __0)
    {
        try
        {
            if (!IsLeftClick(__0))
            {
                return;
            }

            var notification = HermesNativeNotificationBridge.ReadNotificationFromView(__instance);
            HermesNativeNotificationBridge.TryHandleNativeClick(notification);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native notification click patch failed: {ex}");
        }
    }

    private static bool IsLeftClick(object? pointerEventData)
    {
        if (pointerEventData is null)
        {
            return true;
        }

        var button = pointerEventData.GetType()
            .GetProperty("button", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(pointerEventData)
            ?.ToString();
        return string.IsNullOrWhiteSpace(button)
               || button.Equals("Left", StringComparison.OrdinalIgnoreCase);
    }
}
