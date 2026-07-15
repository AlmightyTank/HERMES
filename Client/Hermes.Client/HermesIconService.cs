using System.Reflection;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Loads small HERMES UI icons from resources embedded directly in Hermes.Client.dll.
/// The sprites are created lazily on the Unity UI thread when an EFT context menu first needs them.
/// </summary>
internal static class HermesIconService
{
    private const string AskHermesResourceName = "Hermes.Client.Assets.ask_hermes.png";

    private static readonly object Sync = new();
    private static Sprite? _askHermesIcon;
    private static bool _askHermesLoadAttempted;

    internal static Sprite? AskHermesIcon
    {
        get
        {
            lock (Sync)
            {
                if (_askHermesIcon is not null || _askHermesLoadAttempted)
                {
                    return _askHermesIcon;
                }

                _askHermesLoadAttempted = true;
                _askHermesIcon = LoadEmbeddedSprite(AskHermesResourceName, "HERMES Ask action");
                return _askHermesIcon;
            }
        }
    }

    private static Sprite? LoadEmbeddedSprite(string resourceName, string displayName)
    {
        try
        {
            var assembly = typeof(HermesIconService).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Plugin.Log?.LogWarning(
                    $"Could not load the {displayName} icon because embedded resource '{resourceName}' was not found.");
                return null;
            }

            if (stream.Length <= 0L || stream.Length > int.MaxValue)
            {
                Plugin.Log?.LogWarning(
                    $"Could not load the {displayName} icon because its embedded PNG length was invalid.");
                return null;
            }

            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != bytes.Length)
            {
                Plugin.Log?.LogWarning(
                    $"Could not load the {displayName} icon because its embedded PNG was incomplete.");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = displayName + " Texture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            if (!ImageConversion.LoadImage(texture, bytes, true))
            {
                UnityEngine.Object.Destroy(texture);
                Plugin.Log?.LogWarning($"Unity could not decode the embedded {displayName} PNG.");
                return null;
            }

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = displayName + " Icon";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"Failed to load the embedded {displayName} icon: {ex}");
            return null;
        }
    }
}
