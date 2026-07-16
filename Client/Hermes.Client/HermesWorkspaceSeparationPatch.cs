using System.Reflection;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

internal static class HermesWorkspaceSeparation
{
    internal static readonly HermesRaidPlannerPanel RaidPlanner = new();

    private static readonly FieldInfo? ActiveTabField = typeof(HermesWindow).GetField(
        "_activeTab",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadoutViewField = typeof(HermesLoadoutPanel).GetField(
        "_view",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadoutScrollField = typeof(HermesLoadoutPanel).GetField(
        "_scroll",
        BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool IsRaidPlanner(HermesWindow window)
    {
        return string.Equals(
            ActiveTabField?.GetValue(window)?.ToString(),
            "RaidPlanner",
            StringComparison.Ordinal);
    }

    internal static bool IsRaidPlannerView(HermesLoadoutPanel panel)
    {
        return string.Equals(
            LoadoutViewField?.GetValue(panel)?.ToString(),
            "RaidPlanner",
            StringComparison.Ordinal);
    }

    internal static void ForceLoadoutOverviewIfNeeded(HermesLoadoutPanel panel)
    {
        if (!IsRaidPlannerView(panel) || LoadoutViewField is null)
        {
            return;
        }

        var overview = Enum.Parse(LoadoutViewField.FieldType, "Overview");
        LoadoutViewField.SetValue(panel, overview);
        LoadoutScrollField?.SetValue(panel, Vector2.zero);
    }

    internal static void DrawLoadoutTabs(HermesLoadoutPanel panel)
    {
        ForceLoadoutOverviewIfNeeded(panel);
        GUILayout.BeginHorizontal();
        DrawLoadoutTab(panel, "Overview", "Overview", 95f);
        DrawLoadoutTab(panel, "Weapons & Ammo", "Weapons", 135f);
        DrawLoadoutTab(panel, "Armor", "Armor", 80f);
        DrawLoadoutTab(panel, "Medical", "Medical", 85f);
        DrawLoadoutTab(panel, "Quest Gear", "Quests", 100f);
        if (Plugin.Settings.ShowValueAndInsurance.Value)
        {
            DrawLoadoutTab(panel, "Value & Insurance", "ValueInsurance", 130f);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static void DrawLoadoutTab(
        HermesLoadoutPanel panel,
        string label,
        string viewName,
        float width)
    {
        if (LoadoutViewField is null)
        {
            return;
        }

        var current = LoadoutViewField.GetValue(panel)?.ToString();
        var selected = string.Equals(current, viewName, StringComparison.Ordinal);
        if (GUILayout.Button(
                label.ToUpperInvariant(),
                HermesEftTheme.Tab(selected),
                GUILayout.Width(width),
                GUILayout.Height(28f)))
        {
            var view = Enum.Parse(LoadoutViewField.FieldType, viewName);
            LoadoutViewField.SetValue(panel, view);
            LoadoutScrollField?.SetValue(panel, Vector2.zero);
        }
    }

    internal static async Task RefreshRaidPlannerAsync(bool clearCaches)
    {
        if (clearCaches)
        {
            try
            {
                await HermesApiClient.ClearCachesAsync();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"HERMES could not clear caches before Raid Planner refresh: {ex.Message}");
            }
        }

        await RaidPlanner.RefreshFromServerAsync(true);
    }
}

internal sealed class HermesWindowRaidPlannerDrawPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesWindow).GetMethod(
                   "DrawActiveTabContent",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawActiveTabContent");
    }

    [PatchPrefix]
    private static bool Prefix(HermesWindow __instance)
    {
        if (!HermesWorkspaceSeparation.IsRaidPlanner(__instance))
        {
            return true;
        }

        HermesWorkspaceSeparation.RaidPlanner.Draw();
        return false;
    }
}

internal sealed class HermesWindowRaidPlannerRefreshPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesWindow).GetMethod(
                   "RefreshCurrentDataAsync",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "RefreshCurrentDataAsync");
    }

    [PatchPrefix]
    private static bool Prefix(
        HermesWindow __instance,
        bool clearCaches,
        ref Task __result)
    {
        if (!HermesWorkspaceSeparation.IsRaidPlanner(__instance))
        {
            return true;
        }

        __result = HermesWorkspaceSeparation.RefreshRaidPlannerAsync(clearCaches);
        return false;
    }
}

internal sealed class HermesWindowRaidPlannerClearPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesWindow).GetMethod(
                   "ClearCurrentTab",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "ClearCurrentTab");
    }

    [PatchPrefix]
    private static bool Prefix(HermesWindow __instance)
    {
        if (!HermesWorkspaceSeparation.IsRaidPlanner(__instance))
        {
            return true;
        }

        HermesWorkspaceSeparation.RaidPlanner.Clear();
        return false;
    }
}

internal sealed class HermesLoadoutTabsWithoutRaidPlannerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesLoadoutPanel).GetMethod(
                   "DrawViewTabs",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesLoadoutPanel).FullName, "DrawViewTabs");
    }

    [PatchPrefix]
    private static bool Prefix(HermesLoadoutPanel __instance)
    {
        HermesWorkspaceSeparation.DrawLoadoutTabs(__instance);
        return false;
    }
}

internal sealed class HermesLoadoutOpenViewSeparationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesLoadoutPanel).GetMethod(
                   "OpenView",
                   BindingFlags.Instance | BindingFlags.Public)
               ?? throw new MissingMethodException(typeof(HermesLoadoutPanel).FullName, "OpenView");
    }

    [PatchPrefix]
    private static bool Prefix(string viewName)
    {
        return !string.Equals(viewName, "Raid Planner", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(viewName, "RaidPlanner", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HermesLoadoutDefaultViewSeparationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesLoadoutPanel).GetMethod(
                   "InitializeDefaults",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesLoadoutPanel).FullName, "InitializeDefaults");
    }

    [PatchPostfix]
    private static void Postfix(HermesLoadoutPanel __instance)
    {
        HermesWorkspaceSeparation.ForceLoadoutOverviewIfNeeded(__instance);
    }
}

internal sealed class HermesLoadoutSummaryViewGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HermesLoadoutPanel).GetMethod(
                   "DrawSummary",
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(typeof(HermesLoadoutPanel).FullName, "DrawSummary");
    }

    [PatchPrefix]
    private static void Prefix(HermesLoadoutPanel __instance)
    {
        HermesWorkspaceSeparation.ForceLoadoutOverviewIfNeeded(__instance);
    }
}
