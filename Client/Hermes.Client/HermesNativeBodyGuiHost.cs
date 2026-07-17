using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Compatibility shim for source branches that still reference the former IMGUI body host.
/// Alpha12.7.4 never calls OnGUI. Any remaining attachment is redirected to the fully native
/// HermesNativeWorkspaceBody component.
/// </summary>
[Obsolete("Use HermesNativeWorkspaceBody. The IMGUI workspace bridge was removed in Alpha12.7.4.")]
internal sealed class HermesNativeBodyGuiHost : MonoBehaviour
{
    internal void Initialize(HermesWindow window)
    {
        var nativeBody = GetComponent<HermesNativeWorkspaceBody>()
                         ?? gameObject.AddComponent<HermesNativeWorkspaceBody>();
        nativeBody.Initialize(window);
        enabled = false;
    }
}
