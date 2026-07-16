using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Hosts the existing HERMES renderer inside the fixed EFT inventory content rectangle.
/// The surrounding tab, lifecycle, visibility, navigation, and screen ownership are native
/// Unity UI. This removes the draggable floating window while the Character/inventory screen
/// is available and provides the migration point for later fully-prefabbed panel controls.
/// </summary>
internal sealed class HermesEmbeddedGuiHost : MonoBehaviour
{
    private readonly Vector3[] _worldCorners = new Vector3[4];
    private HermesWindow? _window;
    private RectTransform? _rectTransform;
    private Canvas? _canvas;

    internal void Initialize(HermesWindow window)
    {
        _window = window;
        _rectTransform = transform as RectTransform;
        _canvas = GetComponentInParent<Canvas>();
    }

    private void OnGUI()
    {
        if (_window == null || _rectTransform == null || !isActiveAndEnabled)
        {
            return;
        }

        var camera = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? _canvas.worldCamera
            : null;
        _rectTransform.GetWorldCorners(_worldCorners);
        var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, _worldCorners[0]);
        var topRight = RectTransformUtility.WorldToScreenPoint(camera, _worldCorners[2]);
        var width = Math.Max(0f, topRight.x - bottomLeft.x);
        var height = Math.Max(0f, topRight.y - bottomLeft.y);
        if (width < 320f || height < 240f)
        {
            return;
        }

        var unclippedLeft = bottomLeft.x;
        var unclippedRight = topRight.x;
        var unclippedTop = Screen.height - topRight.y;
        var unclippedBottom = Screen.height - bottomLeft.y;

        // InventoryScreen is persistent and several EFT panels use stretched anchors.
        // Clamp the IMGUI bridge to the actual visible screen/safe area so the HERMES
        // workspace cannot draw below the inventory content or over the bottom task bar.
        var safeArea = Screen.safeArea;
        var safeTop = Screen.height - safeArea.yMax;
        var safeBottom = Screen.height - safeArea.yMin;
        var left = Mathf.Max(unclippedLeft, safeArea.xMin);
        var right = Mathf.Min(unclippedRight, safeArea.xMax);
        var top = Mathf.Max(unclippedTop, safeTop);
        var bottom = Mathf.Min(unclippedBottom, safeBottom);
        var screenRect = Rect.MinMaxRect(left, top, right, bottom);
        if (screenRect.width < 320f || screenRect.height < 240f)
        {
            return;
        }

        var previousDepth = GUI.depth;
        try
        {
            GUI.depth = -5000;
            GUI.BeginGroup(screenRect);
            _window.DrawEmbedded(new Rect(0f, 0f, screenRect.width, screenRect.height));
        }
        finally
        {
            GUI.EndGroup();
            GUI.depth = previousDepth;
        }
    }
}
