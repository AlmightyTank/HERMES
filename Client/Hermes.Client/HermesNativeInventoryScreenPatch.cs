using System.Reflection;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;

namespace Hermes.Client;

/// <summary>
/// Patches the fully initialized InventoryScreen.Show overload used by both the main
/// Character screen and the in-raid inventory. Awake is too early for the native tab
/// group and can also run before BepInEx client patches are installed.
/// </summary>
internal sealed class HermesNativeInventoryScreenPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryScreen).GetMethod(
                   "Show",
                   BindingFlags.Public | BindingFlags.Instance,
                   binder: null,
                   types:
                   [
                       typeof(IHealthController),
                       typeof(InventoryController),
                       typeof(global::AbstractQuestControllerClass),
                       typeof(global::AbstractAchievementControllerClass),
                       typeof(global::AbstractPrestigeControllerClass),
                       typeof(CompoundItem),
                       typeof(EInventoryTab),
                       typeof(global::ISession),
                       typeof(global::ItemContextAbstractClass),
                       typeof(bool)
                   ],
                   modifiers: null)
               ?? throw new MissingMethodException(
                   typeof(InventoryScreen).FullName,
                   "Show(IHealthController, InventoryController, AbstractQuestControllerClass, "
                   + "AbstractAchievementControllerClass, AbstractPrestigeControllerClass, CompoundItem, "
                   + "EInventoryTab, ISession, ItemContextAbstractClass, bool)");
    }

    [PatchPostfix]
    private static void Postfix(InventoryScreen __instance)
    {
        try
        {
            HermesNativeScreenHost.AttachOrRefresh(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native InventoryScreen Show attachment failed: {ex}");
        }
    }
}
