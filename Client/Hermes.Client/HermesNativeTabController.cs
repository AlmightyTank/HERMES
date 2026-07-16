using System.Threading.Tasks;

namespace Hermes.Client;

/// <summary>
/// Native EFT tab controller for HERMES. GClass3800 is the controller base used by
/// Achievements, Tasks, Map, Gear, and the other InventoryScreen tabs.
/// </summary>
internal sealed class HermesNativeTabController
    : global::GClass3800<HermesNativeContentView>
{
    internal HermesNativeTabController(HermesNativeContentView content)
        : base(content)
    {
    }

    public override void Show()
    {
        Gparam_0.ShowContent();
    }

    public override Task<bool> TryHide()
    {
        Gparam_0.HideContent();
        return Task.FromResult(true);
    }
}
