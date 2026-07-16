using EFT.UI;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Native EFT UIElement that owns the Alpha12.7.3.4 HERMES workspace shell.
/// </summary>
internal sealed class HermesNativeContentView : UIElement
{
    private HermesWindow? _window;
    private HermesNativeWorkspaceView? _workspace;
    private bool _initialized;

    internal void Initialize(HermesWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _workspace = GetComponent<HermesNativeWorkspaceView>()
                     ?? gameObject.AddComponent<HermesNativeWorkspaceView>();
        _workspace.Initialize(window);
        _initialized = true;
        HideContent();
    }

    internal void ShowContent()
    {
        if (!_initialized || _window == null || _workspace == null)
        {
            return;
        }

        gameObject.SetActive(true);
        _window.SetNativeVisibility(true);
        _workspace.ShowWorkspace();
    }

    internal void HideContent()
    {
        HermesNativeWorkspaceRuntime.Active = false;
        _workspace?.HideWorkspace();
        _window?.SetNativeVisibility(false);
        if (gameObject.activeSelf)
        {
            gameObject.SetActive(false);
        }
    }
}
