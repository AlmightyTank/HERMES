using System.Reflection;
using EFT.UI;
using SPT.Reflection.Patching;

namespace Hermes.Client;

/// <summary>
/// Hides the external HERMES tab before EFT begins closing or recycling its shared
/// InventoryScreen. This prevents one-frame stale-tab flashes during Character-screen
/// and in-raid inventory transitions.
/// </summary>
internal sealed class HermesNativeInventoryScreenClosePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryScreen).GetMethod(
                   nameof(InventoryScreen.Close),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(typeof(InventoryScreen).FullName, nameof(InventoryScreen.Close));
    }

    [PatchPrefix]
    private static void Prefix(InventoryScreen __instance)
    {
        try
        {
            __instance.GetComponent<HermesNativeScreenHost>()?.NotifyInventoryClosing();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native InventoryScreen close cleanup failed: {ex}");
        }
    }
}
