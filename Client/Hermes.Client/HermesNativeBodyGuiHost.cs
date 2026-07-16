using System.Reflection;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Temporary bridge used during the native conversion. It draws only the selected HERMES
/// workspace body inside a native RectTransform. Native uGUI now owns the header, navigation,
/// search field, and global actions; individual data panels can be migrated one at a time.
/// </summary>
internal sealed class HermesNativeBodyGuiHost : MonoBehaviour
{
    private static readonly MethodInfo DrawActiveTabMethod = typeof(HermesWindow).GetMethod(
        "DrawActiveTabContent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawActiveTabContent");

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
        if (_window == null
            || _rectTransform == null
            || !isActiveAndEnabled
            || !HermesNativeWorkspaceRuntime.Active)
        {
            return;
        }

        var camera = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? _canvas.worldCamera
            : null;
        _rectTransform.GetWorldCorners(_worldCorners);
        var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, _worldCorners[0]);
        var topRight = RectTransformUtility.WorldToScreenPoint(camera, _worldCorners[2]);
        var left = Mathf.Max(0f, bottomLeft.x);
        var right = Mathf.Min(Screen.width, topRight.x);
        var top = Mathf.Max(0f, Screen.height - topRight.y);
        var bottom = Mathf.Min(Screen.height, Screen.height - bottomLeft.y);
        var screenRect = Rect.MinMaxRect(left, top, right, bottom);
        if (screenRect.width < 260f || screenRect.height < 180f)
        {
            return;
        }

        var previousDepth = GUI.depth;
        var previousSkin = GUI.skin;
        var previousColor = GUI.color;
        var previousEnabled = GUI.enabled;
        var groupBegun = false;
        var areaBegun = false;
        try
        {
            GUI.depth = -5000;
            GUI.skin = HermesEftTheme.Skin;
            GUI.color = Color.white;
            GUI.enabled = true;

            GUI.BeginGroup(screenRect);
            groupBegun = true;
            GUILayout.BeginArea(new Rect(0f, 0f, screenRect.width, screenRect.height));
            areaBegun = true;
            DrawActiveTabMethod.Invoke(_window, null);
        }
        catch (TargetInvocationException ex)
        {
            Plugin.Log?.LogError($"HERMES native body rendering failed: {ex.InnerException ?? ex}");
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"HERMES native body rendering failed: {ex}");
        }
        finally
        {
            if (areaBegun)
            {
                GUILayout.EndArea();
            }
            if (groupBegun)
            {
                GUI.EndGroup();
            }
            GUI.enabled = previousEnabled;
            GUI.color = previousColor;
            GUI.skin = previousSkin;
            GUI.depth = previousDepth;
        }
    }
}
