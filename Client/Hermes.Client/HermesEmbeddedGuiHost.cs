using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Compatibility shim for older inventory hosts. The former embedded IMGUI
/// renderer has been removed; legacy attachments are redirected to the native uGUI body.
/// </summary>
[Obsolete("Use HermesNativeWorkspaceBody. Embedded IMGUI rendering is no longer supported.")]
internal sealed class HermesEmbeddedGuiHost : MonoBehaviour
{
    internal void Initialize(HermesWindow window)
    {
        var nativeBody = GetComponent<HermesNativeWorkspaceBody>()
                         ?? gameObject.AddComponent<HermesNativeWorkspaceBody>();
        nativeBody.Initialize(window);
        enabled = false;
    }
}
