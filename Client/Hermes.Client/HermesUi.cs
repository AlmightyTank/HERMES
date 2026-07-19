using UnityEngine;

namespace Hermes.Client;

internal static class HermesUi
{
    public static float StandardSpace => Plugin.Settings.CompactMode.Value ? 3f : 6f;
    public static float SmallSpace => Plugin.Settings.CompactMode.Value ? 2f : 4f;
    public static float ToolbarHeight => Plugin.Settings.CompactMode.Value ? 25f : 29f;

    public static void DrawAppHeader(string title, string description)
    {
        GUILayout.Label(title);
        if (Plugin.Settings.ShowHelpText.Value && !string.IsNullOrWhiteSpace(description))
        {
            GUILayout.Label(description);
        }
    }

    public static void DrawPanelTitle(
        string title,
        string? description,
        string? status,
        bool loading)
    {
        GUILayout.Label(title);
        if (Plugin.Settings.ShowSectionDescriptions.Value && !string.IsNullOrWhiteSpace(description))
        {
            GUILayout.Label(description);
        }

        DrawStatusLine(status, loading);
        GUILayout.Space(StandardSpace);
    }

    public static void DrawPanelHeader(
        string title,
        string? description,
        string? status,
        bool loading,
        Action refresh)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, GUILayout.ExpandWidth(true));
        GUI.enabled = !loading;
        if (GUILayout.Button(
                loading ? "Refreshing..." : "Refresh",
                GUILayout.Width(110f),
                GUILayout.Height(ToolbarHeight)))
        {
            refresh();
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (Plugin.Settings.ShowSectionDescriptions.Value && !string.IsNullOrWhiteSpace(description))
        {
            GUILayout.Label(description);
        }

        DrawStatusLine(status, loading);
        GUILayout.Space(StandardSpace);
    }

    public static void DrawStatusLine(string? status, bool loading = false)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(loading ? "●" : "•", GUILayout.Width(18f));
        GUILayout.Label(status, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
    }

    public static void DrawEmptyState(string message, string? help = null)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(message);
        if (Plugin.Settings.ShowHelpText.Value && !string.IsNullOrWhiteSpace(help))
        {
            GUILayout.Label(help);
        }
        GUILayout.EndVertical();
    }

    public static void DrawError(string message)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("HERMES REQUEST ERROR");
        GUILayout.Label(message);
        if (Plugin.Settings.ShowHelpText.Value)
        {
            GUILayout.Label("Confirm SPT.Server is still running, then use the panel Refresh button. Use Copy diagnostics in the HERMES navigation rail when reporting a repeatable failure.");
        }
        GUILayout.EndVertical();
    }

    public static void DrawWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("HERMES WARNING");
        GUILayout.Label(message);
        GUILayout.EndVertical();
    }

    public static bool DrawTabButton(string label, bool selected, float width)
    {
        return GUILayout.Button(
            (selected ? "● " : string.Empty) + label,
            GUILayout.Width(width),
            GUILayout.Height(Plugin.Settings.CompactMode.Value ? 26f : 30f));
    }

    public static bool DrawSectionButton(string label, bool expanded, string? badge = null)
    {
        var suffix = string.IsNullOrWhiteSpace(badge) ? string.Empty : $" — {badge}";
        return GUILayout.Button(
            $"{(expanded ? "▼" : "▶")}  {label}{suffix}",
            GUILayout.Height(ToolbarHeight),
            GUILayout.ExpandWidth(true));
    }

    public static void DrawMetric(string label, string value, string? note = null, float minimumWidth = 145f)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(minimumWidth), GUILayout.ExpandWidth(true));
        GUILayout.Label(label);
        GUILayout.Label(value);
        if (Plugin.Settings.ShowSectionDescriptions.Value && !string.IsNullOrWhiteSpace(note))
        {
            GUILayout.Label(note);
        }
        GUILayout.EndVertical();
    }

    public static IReadOnlyList<T> LimitRows<T>(IEnumerable<T> source, out int hiddenCount)
    {
        var all = source as IReadOnlyList<T> ?? source.ToList();
        var limit = Plugin.Settings.GetMaximumRowsPerSection();
        hiddenCount = Math.Max(0, all.Count - limit);
        return hiddenCount > 0 ? all.Take(limit).ToList() : all;
    }

    public static void DrawHiddenRowsNotice(int hiddenCount)
    {
        if (hiddenCount > 0)
        {
            GUILayout.Label($"{hiddenCount:N0} additional row(s) are hidden by Interface → Maximum rows per section.");
        }
    }
}
