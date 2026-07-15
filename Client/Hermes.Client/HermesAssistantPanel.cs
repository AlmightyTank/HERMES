using System.Text;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantPanel
{
    private enum AssistantIntent
    {
        Help,
        Loadout,
        RaidPlanner,
        Stash,
        Crafts,
        Hideout,
        Item,
        Unknown
    }

    private sealed class AssistantAction
    {
        public AssistantAction(string label, string tabName)
        {
            Label = label;
            TabName = tabName;
        }

        public string Label { get; }
        public string TabName { get; }
    }

    private sealed class AssistantMessage
    {
        public AssistantMessage(bool isUser, string text, string source, IReadOnlyList<AssistantAction>? actions = null)
        {
            IsUser = isUser;
            Text = text;
            Source = source;
            Actions = actions ?? Array.Empty<AssistantAction>();
        }

        public bool IsUser { get; }
        public string Text { get; }
        public string Source { get; }
        public IReadOnlyList<AssistantAction> Actions { get; }
    }

    private static readonly string[] SuggestedPrompts =
    [
        "Am I ready for a raid?",
        "What is the best raid for me right now?",
        "What items can I safely sell?",
        "What crafts are ready now?",
        "What hideout upgrades need attention?"
    ];

    private static GUIStyle? _messageStyle;

    private readonly List<AssistantMessage> _messages = [];
    private Vector2 _conversationScroll;
    private string _input = string.Empty;
    private string _status = "Ask HERMES about your current profile, loadout, quests, stash, hideout, crafts, or a selected item.";
    private string _lastPrompt = string.Empty;
    private bool _loading;
    private int _requestVersion;
    private bool _scrollToBottom;

    public HermesAssistantPanel()
    {
        AddWelcomeMessage();
    }

    public void Draw(
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey,
        Action<string> navigate)
    {
        HermesUi.DrawPanelTitle(
            "HERMES ASSISTANT",
            "Deterministic local answers generated from HERMES profile, market, quest, hideout, craft, stash, and loadout services. No external AI service is used.",
            _status,
            _loading);

        DrawContext(selectedItem);
        GUILayout.Space(HermesUi.SmallSpace);
        DrawConversation(navigate);
        GUILayout.Space(HermesUi.StandardSpace);
        DrawPromptComposer(selectedItem, selectedInstanceKey);
    }

    public void Clear()
    {
        _requestVersion++;
        _messages.Clear();
        _input = string.Empty;
        _lastPrompt = string.Empty;
        _status = "Conversation cleared. Ask HERMES a new question.";
        _loading = false;
        AddWelcomeMessage();
    }

    public async Task RefreshLastAsync(HermesItemSummary? selectedItem, string? selectedInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(_lastPrompt) || _loading)
        {
            _status = "There is no previous Assistant question to refresh.";
            return;
        }

        await SubmitPromptAsync(_lastPrompt, selectedItem, selectedInstanceKey, false);
    }

    private void DrawContext(HermesItemSummary? selectedItem)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("CONTEXT", GUILayout.Width(76f));
        if (selectedItem is not null && Plugin.Settings.IncludeSelectedItemInAssistant.Value)
        {
            GUILayout.Label($"Selected item: {selectedItem.Name}", GUILayout.ExpandWidth(true));
        }
        else
        {
            GUILayout.Label("Current PMC profile and configured HERMES readiness/reservation settings", GUILayout.ExpandWidth(true));
        }

        GUILayout.Label("LOCAL • READ ONLY", GUILayout.Width(130f));
        GUILayout.EndHorizontal();
    }

    private void DrawConversation(Action<string> navigate)
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        _conversationScroll = GUILayout.BeginScrollView(
            _conversationScroll,
            GUILayout.ExpandWidth(true),
            GUILayout.ExpandHeight(true));

        foreach (var message in _messages)
        {
            DrawMessage(message, navigate);
            GUILayout.Space(HermesUi.SmallSpace);
        }

        if (_loading)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("HERMES • ANALYZING");
            GUILayout.Label("Reading the current local profile snapshot and relevant HERMES services...");
            GUILayout.EndVertical();
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();

        if (_scrollToBottom)
        {
            _conversationScroll.y = float.MaxValue;
            _scrollToBottom = false;
        }
    }

    private static void DrawMessage(AssistantMessage message, Action<string> navigate)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(message.IsUser ? "YOU" : "HERMES", GUILayout.Width(72f));
        GUILayout.Label(message.Source, GUILayout.ExpandWidth(true));
        if (!message.IsUser && GUILayout.Button("Copy", GUILayout.Width(58f)))
        {
            GUIUtility.systemCopyBuffer = message.Text;
        }
        GUILayout.EndHorizontal();

        GUILayout.Label(message.Text, GetMessageStyle());

        if (message.Actions.Count > 0)
        {
            GUILayout.Space(HermesUi.SmallSpace);
            GUILayout.BeginHorizontal();
            foreach (var action in message.Actions)
            {
                if (GUILayout.Button(action.Label, GUILayout.Height(HermesUi.ToolbarHeight)))
                {
                    navigate(action.TabName);
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
    }

    private void DrawPromptComposer(HermesItemSummary? selectedItem, string? selectedInstanceKey)
    {
        if (Plugin.Settings.ShowAssistantSuggestedPrompts.Value)
        {
            GUILayout.BeginHorizontal();
            foreach (var prompt in GetSuggestedPrompts(selectedItem).Take(3))
            {
                if (GUILayout.Button(prompt, GUILayout.Height(HermesUi.ToolbarHeight), GUILayout.ExpandWidth(true)))
                {
                    _input = prompt;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(HermesUi.SmallSpace);
        }

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("HermesAssistantInput");
        _input = GUILayout.TextField(
            _input,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(HermesUi.ToolbarHeight));

        GUI.enabled = !_loading && !string.IsNullOrWhiteSpace(_input);
        if (GUILayout.Button(_loading ? "Analyzing..." : "Ask", GUILayout.Width(105f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            var prompt = _input;
            _input = string.Empty;
            _ = SubmitPromptAsync(prompt, selectedItem, selectedInstanceKey, true);
        }

        GUI.enabled = true;
        if (GUILayout.Button("Clear chat", GUILayout.Width(95f), GUILayout.Height(HermesUi.ToolbarHeight)))
        {
            Clear();
        }
        GUILayout.EndHorizontal();

        if (Event.current.type == EventType.KeyDown
            && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
            && GUI.GetNameOfFocusedControl() == "HermesAssistantInput"
            && !_loading
            && !string.IsNullOrWhiteSpace(_input))
        {
            var prompt = _input;
            _input = string.Empty;
            Event.current.Use();
            _ = SubmitPromptAsync(prompt, selectedItem, selectedInstanceKey, true);
        }
    }

    private static IEnumerable<string> GetSuggestedPrompts(HermesItemSummary? selectedItem)
    {
        if (selectedItem is not null && Plugin.Settings.IncludeSelectedItemInAssistant.Value)
        {
            yield return "What is this item worth?";
            yield return "Do I need this item for quests or hideout?";
            yield return "Where should I sell this item?";
        }

        foreach (var prompt in SuggestedPrompts)
        {
            yield return prompt;
        }
    }

    private async Task SubmitPromptAsync(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey,
        bool appendUserMessage)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || _loading)
        {
            return;
        }

        var requestVersion = ++_requestVersion;
        _lastPrompt = prompt;
        _loading = true;
        _status = $"Analyzing: {prompt}";
        if (appendUserMessage)
        {
            AddMessage(new AssistantMessage(true, prompt, "QUESTION"));
        }

        try
        {
            var intent = DetectIntent(prompt, selectedItem);
            var response = await BuildResponseAsync(intent, prompt, selectedItem, selectedInstanceKey);
            if (requestVersion != _requestVersion)
            {
                return;
            }

            AddMessage(response);
            _status = $"Answered locally using {response.Source.ToLowerInvariant()} data.";
        }
        catch (Exception ex)
        {
            if (requestVersion == _requestVersion)
            {
                AddMessage(new AssistantMessage(
                    false,
                    HermesApiClient.DescribeFailure(ex, "Assistant analysis"),
                    "REQUEST ERROR"));
                _status = "Assistant analysis failed. Check diagnostics and retry.";
            }

            Plugin.Log.LogError(ex);
        }
        finally
        {
            if (requestVersion == _requestVersion)
            {
                _loading = false;
            }
        }
    }

    private static AssistantIntent DetectIntent(string prompt, HermesItemSummary? selectedItem)
    {
        var text = prompt.ToLowerInvariant();
        if (ContainsAny(text, "what can you do", "help", "commands", "capabilities"))
        {
            return AssistantIntent.Help;
        }

        if (ContainsAny(text, "best raid", "which raid", "what raid", "best map", "which map", "raid planner", "active quests", "quests can i"))
        {
            return AssistantIntent.RaidPlanner;
        }

        if (ContainsAny(text, "safe to sell", "safely sell", "stash cleanup", "clean my stash", "cleanup", "duplicate", "stash value", "stash"))
        {
            return AssistantIntent.Stash;
        }

        if (selectedItem is not null && ReferencesSelectedItem(text))
        {
            return AssistantIntent.Item;
        }

        if (ContainsAny(text, "craft", "recipe", "profitable", "overnight"))
        {
            return AssistantIntent.Crafts;
        }

        if (ContainsAny(text, "hideout", "upgrade", "station"))
        {
            return AssistantIntent.Hideout;
        }

        if (ContainsAny(text, "ready", "loadout", "ammo", "magazine", "medical", "bleed", "fracture", "pain", "armor", "weapon", "insured", "insurance", "risk", "hydration", "energy"))
        {
            return AssistantIntent.Loadout;
        }

        if (ContainsAny(text, "worth", "price", "flea", "trader", "buy", "sell", "item"))
        {
            return AssistantIntent.Item;
        }

        return AssistantIntent.Unknown;
    }

    private static async Task<AssistantMessage> BuildResponseAsync(
        AssistantIntent intent,
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        return intent switch
        {
            AssistantIntent.Help => BuildHelpResponse(),
            AssistantIntent.Loadout => await BuildLoadoutResponseAsync(prompt),
            AssistantIntent.RaidPlanner => await BuildRaidPlannerResponseAsync(),
            AssistantIntent.Stash => await BuildStashResponseAsync(),
            AssistantIntent.Crafts => await BuildCraftResponseAsync(prompt),
            AssistantIntent.Hideout => await BuildHideoutResponseAsync(),
            AssistantIntent.Item => await BuildItemResponseAsync(prompt, selectedItem, selectedInstanceKey),
            _ => await BuildUnknownResponseAsync(prompt, selectedItem, selectedInstanceKey)
        };
    }

    private static AssistantMessage BuildHelpResponse()
    {
        var text = string.Join(Environment.NewLine,
        [
            "I can answer local, read-only questions about:",
            "• Current loadout readiness, ammunition, medical coverage, armor, insurance, and raid risk",
            "• Active quests grouped into Raid Planner map plans",
            "• Stash reservations, safe-to-sell surplus, cleanup candidates, duplicates, and damaged items",
            "• Hideout upgrades, missing materials, active production, and available crafts",
            "• Selected-item trader, flea, quest, hideout, and crafting use",
            string.Empty,
            "Alpha12.0 uses deterministic intent matching and current HERMES data. It does not buy, sell, insure, equip, move, craft, or complete anything."
        ]);

        return new AssistantMessage(
            false,
            text,
            "LOCAL CAPABILITIES",
            [
                new AssistantAction("Open Item Search", "Item Search"),
                new AssistantAction("Open Loadout", "Loadout")
            ]);
    }

    private static async Task<AssistantMessage> BuildLoadoutResponseAsync(string prompt)
    {
        var response = await HermesApiClient.GetLoadoutSummaryAsync(
            Plugin.Settings.CreateLoadoutRequestSettings());
        if (!response.Found)
        {
            return Failure(response.Message, "LOADOUT");
        }

        var text = prompt.ToLowerInvariant();
        var builder = new StringBuilder();
        builder.AppendLine($"Readiness: {response.Readiness} ({response.ReadinessScore}/100)");
        builder.AppendLine($"Critical issues: {response.CriticalCount:N0} • Total warnings: {response.WarningCount:N0}");

        if (ContainsAny(text, "ammo", "magazine", "round"))
        {
            builder.AppendLine();
            builder.AppendLine("Weapons and ammunition:");
            if (response.Weapons.Count == 0)
            {
                builder.AppendLine("• No equipped firearm was detected.");
            }
            else
            {
                foreach (var weapon in response.Weapons.Take(5))
                {
                    var spareRounds = weapon.SpareMagazineRounds + weapon.LooseCompatibleRounds;
                    builder.AppendLine($"• {weapon.Name}: {weapon.LoadedRounds:N0}/{weapon.MagazineCapacity:N0} loaded, {weapon.CompatibleSpareMagazineCount:N0} spare magazine(s), {spareRounds:N0} compatible spare round(s) — {weapon.Status}");
                }
            }
        }
        else if (ContainsAny(text, "medical", "bleed", "fracture", "pain", "healing"))
        {
            var medical = response.Medical;
            builder.AppendLine();
            builder.AppendLine("Medical coverage:");
            builder.AppendLine($"• Healing resource: {medical.TotalHealingResource:N0}");
            builder.AppendLine($"• Light bleed: {YesNo(medical.HasLightBleedTreatment)}");
            builder.AppendLine($"• Heavy bleed: {YesNo(medical.HasHeavyBleedTreatment)}");
            builder.AppendLine($"• Fracture: {YesNo(medical.HasFractureTreatment)}");
            builder.AppendLine($"• Pain: {YesNo(medical.HasPainTreatment)}");
            builder.AppendLine($"• Surgery: {YesNo(medical.HasSurgeryKit)}");
        }
        else if (ContainsAny(text, "insurance", "insured", "risk", "value"))
        {
            var value = response.ValueSummary;
            if (value.Found)
            {
                builder.AppendLine();
                builder.AppendLine("Value and insurance:");
                builder.AppendLine($"• At-risk replacement value: ₽{value.AtRiskReplacementValue:N0}");
                builder.AppendLine($"• Protected-slot value: ₽{value.ProtectedReplacementValue:N0}");
                builder.AppendLine($"• Insured: ₽{value.InsuredReplacementValue:N0}");
                builder.AppendLine($"• Uninsured: ₽{value.UninsuredReplacementValue:N0}");
                builder.AppendLine($"• Status: {value.InsuranceStatus}");
            }
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("Highest-priority findings:");
            var warnings = response.Warnings
                .OrderBy(warning => SeverityRank(warning.Severity))
                .Take(6)
                .ToList();
            if (warnings.Count == 0)
            {
                builder.AppendLine("• No readiness warnings were reported with the current F12 thresholds.");
            }
            else
            {
                foreach (var warning in warnings)
                {
                    builder.AppendLine($"• [{warning.Category}] {warning.Message}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Vitals: health {response.Vitals.HealthPercent}% • hydration {response.Vitals.HydrationPercent}% • energy {response.Vitals.EnergyPercent}%");

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "LOADOUT SNAPSHOT",
            [
                new AssistantAction("Open Loadout", "Loadout"),
                new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")
            ]);
    }

    private static async Task<AssistantMessage> BuildRaidPlannerResponseAsync()
    {
        var response = await HermesApiClient.GetLoadoutSummaryAsync(
            Plugin.Settings.CreateLoadoutRequestSettings());
        if (!response.Found)
        {
            return Failure(response.Message, "RAID PLANNER");
        }

        var plans = response.RaidPlans
            .Where(plan => plan.ActiveQuestCount > 0)
            .OrderBy(plan => plan.MissingRequirementCount)
            .ThenByDescending(plan => plan.ActiveQuestCount)
            .ThenByDescending(plan => plan.ObjectiveCount - plan.CompletedObjectiveCount)
            .ThenBy(plan => plan.MapName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (plans.Count == 0)
        {
            return new AssistantMessage(
                false,
                "No map-specific active quest plan is currently available. Check whether active quests contain raid-location objectives.",
                "RAID PLANNER",
                [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
        }

        var best = plans[0];
        var builder = new StringBuilder();
        builder.AppendLine($"Recommended map: {best.MapName}");
        builder.AppendLine($"Plan status: {best.Status}");
        builder.AppendLine($"Active quests: {best.ActiveQuestCount:N0}");
        builder.AppendLine($"Incomplete objectives: {Math.Max(0, best.ObjectiveCount - best.CompletedObjectiveCount):N0}");
        builder.AppendLine($"Missing pre-raid requirements: {best.MissingRequirementCount:N0}");

        var missing = best.CombinedRequirements
            .Where(requirement => !requirement.IsSatisfied && !requirement.AcquireInRaid)
            .Take(5)
            .ToList();
        var acquire = best.CombinedRequirements
            .Where(requirement => requirement.AcquireInRaid)
            .Take(4)
            .ToList();

        if (missing.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Before deployment:");
            foreach (var requirement in missing)
            {
                builder.AppendLine($"• {requirement.RequiredEquipment} × {FormatCount(requirement.MissingQuantity)} — {requirement.RequirementKind}");
            }
        }

        if (acquire.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Acquire during raid:");
            foreach (var requirement in acquire)
            {
                builder.AppendLine($"• {requirement.RequiredEquipment} — {requirement.Note}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Other strong options:");
        foreach (var plan in plans.Skip(1).Take(3))
        {
            builder.AppendLine($"• {plan.MapName}: {plan.ActiveQuestCount:N0} quest(s), {Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount):N0} incomplete objective(s), {plan.MissingRequirementCount:N0} missing requirement(s)");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "RAID PLANNER",
            [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
    }

    private static async Task<AssistantMessage> BuildStashResponseAsync()
    {
        var response = await HermesApiClient.GetStashSummaryAsync(
            Plugin.Settings.CreateStashRequestSettings());
        if (!response.Found)
        {
            return Failure(response.Message, "STASH");
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Potentially sellable quantity: {FormatCount(response.PotentiallySellQuantity)}");
        builder.AppendLine($"Estimated best-destination value: ₽{response.PotentialBestSaleValue:N0}");
        builder.AppendLine($"Safe-to-sell instances: {response.SafeToSellInstanceCount:N0}");
        builder.AppendLine($"Surplus instances: {response.SellSurplusInstanceCount:N0}");
        builder.AppendLine($"Cleanup candidates: {response.CleanupCandidateInstanceCount:N0} • recoverable cells: {response.RecoverableCells:N0}");

        var top = response.Recommendations
            .Where(item => item.PotentiallySellQuantity > 0d && item.PotentialBestSaleValue > 0L)
            .OrderByDescending(item => item.PotentialBestSaleValue)
            .Take(6)
            .ToList();
        if (top.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Top sale candidates after configured reservations:");
            foreach (var item in top)
            {
                builder.AppendLine($"• {item.Name}: sell {FormatCount(item.PotentiallySellQuantity)} • ₽{item.PotentialBestSaleValue:N0} via {item.BestSaleDestination ?? "best available destination"}");
            }
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("No supported item currently has sellable surplus after configured quest and hideout reservations.");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "STASH ANALYSIS",
            [new AssistantAction("Open Stash", "Stash")]);
    }

    private static async Task<AssistantMessage> BuildCraftResponseAsync(string prompt)
    {
        var response = await HermesApiClient.GetCraftsAsync();
        if (!response.Found)
        {
            return Failure(response.Message, "CRAFTS");
        }

        var text = prompt.ToLowerInvariant();
        IEnumerable<HermesCraftSummary> selected = response.Crafts;
        string heading;
        if (ContainsAny(text, "overnight"))
        {
            var minimum = Plugin.Settings.GetOvernightMinimumHours() * 3600;
            var maximum = Plugin.Settings.GetOvernightMaximumHours() * 3600;
            selected = selected.Where(craft => craft.DurationSeconds >= minimum && craft.DurationSeconds <= maximum && craft.IsAvailable);
            heading = $"Overnight crafts ({Plugin.Settings.GetOvernightMinimumHours()}-{Plugin.Settings.GetOvernightMaximumHours()} hours)";
        }
        else if (ContainsAny(text, "profitable", "profit"))
        {
            selected = selected.Where(craft =>
                craft.EstimatedEconomicProfit >= Plugin.Settings.GetMinimumCraftProfit()
                && GetProfitPercent(craft) >= Plugin.Settings.GetMinimumCraftProfitPercent());
            heading = "Profitable crafts under current F12 thresholds";
        }
        else
        {
            selected = selected.Where(craft => craft.CanStartNow);
            heading = "Crafts ready now";
        }

        var rows = selected
            .OrderByDescending(craft => craft.EstimatedEconomicProfitPerHour)
            .ThenByDescending(craft => craft.EstimatedEconomicProfit)
            .Take(8)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"{heading}: {rows.Count:N0} shown");
        if (rows.Count == 0)
        {
            builder.AppendLine("• No recipe matches the requested state and current configured thresholds.");
        }
        else
        {
            foreach (var craft in rows)
            {
                builder.AppendLine($"• {craft.OutputQuantity:N0} × {craft.OutputName} at {craft.StationName} L{craft.RequiredStationLevel}: {FormatDuration(craft.DurationSeconds)} • economic profit ₽{craft.EstimatedEconomicProfit:N0} • ₽{craft.EstimatedEconomicProfitPerHour:N0}/h");
            }
        }

        var active = response.Crafts.Count(craft => craft.IsActive && !craft.IsComplete);
        var complete = response.Crafts.Count(craft => craft.IsComplete);
        builder.AppendLine();
        builder.AppendLine($"Active productions: {active:N0} • Ready to collect: {complete:N0}");

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "CRAFT ANALYSIS",
            [new AssistantAction("Open Crafts", "Crafts")]);
    }

    private static async Task<AssistantMessage> BuildHideoutResponseAsync()
    {
        var response = await HermesApiClient.GetHideoutSummaryAsync();
        if (!response.Found)
        {
            return Failure(response.Message, "HIDEOUT");
        }

        var actionable = response.Areas
            .Where(area => !area.IsConstructing && area.TargetLevel.HasValue && area.CurrentLevel < area.MaximumLevel)
            .OrderBy(area => area.MissingItemTypes)
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"Ready upgrades: {response.ReadyAreaCount:N0}");
        builder.AppendLine($"Material-blocked upgrades: {response.MaterialBlockedAreaCount:N0}");
        builder.AppendLine($"Progression-blocked upgrades: {response.ProgressionBlockedAreaCount:N0}");
        builder.AppendLine($"Active production: {response.Resources.ActiveProductionCount:N0} • completed production: {response.Resources.CompletedProductionCount:N0}");

        if (actionable.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Areas needing attention:");
            foreach (var area in actionable)
            {
                builder.AppendLine($"• {area.Name} L{area.CurrentLevel} → L{area.TargetLevel}: {area.Status} • {area.MissingItemTypes:N0} missing item type(s) • handbook estimate ₽{area.EstimatedMissingHandbookCost:N0}");
            }
        }

        if (response.Resources.GeneratorActive)
        {
            builder.AppendLine();
            builder.AppendLine($"Generator is active with {response.Resources.FuelContainerCount:N0} fuel container(s) and {response.Resources.FuelResourceRemaining:N0} total fuel resource remaining.");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "HIDEOUT SNAPSHOT",
            [new AssistantAction("Open Hideout", "Hideout")]);
    }

    private static async Task<AssistantMessage> BuildItemResponseAsync(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        var useSelectedContext = selectedItem is not null
                                 && Plugin.Settings.IncludeSelectedItemInAssistant.Value
                                 && ReferencesSelectedItem(prompt);
        var item = useSelectedContext
            ? selectedItem
            : await ResolveItemFromPromptAsync(prompt);

        if (item is null)
        {
            return new AssistantMessage(
                false,
                "I could not identify a supported player-facing item in that question. Select an item through Item Search or Ask HERMES, then ask about “this item.”",
                "ITEM RESOLUTION",
                [new AssistantAction("Open Item Search", "Item Search")]);
        }

        var useSelectedInstance = useSelectedContext
                                  && selectedItem!.ItemKey.Equals(item.ItemKey, StringComparison.OrdinalIgnoreCase);
        var traderTask = HermesApiClient.GetTraderSummaryAsync(
            item.ItemKey,
            useSelectedInstance ? selectedInstanceKey : null);
        var marketTask = HermesApiClient.GetMarketSummaryAsync(item.ItemKey);
        var usageTask = HermesApiClient.GetItemHideoutUsageAsync(item.ItemKey);
        await Task.WhenAll(traderTask, marketTask, usageTask);

        var trader = traderTask.Result;
        var market = marketTask.Result;
        var usage = usageTask.Result;
        var builder = new StringBuilder();
        builder.AppendLine(item.Name);
        if (trader.UsesSelectedStashInstance)
        {
            builder.AppendLine($"Exact instance: {trader.SelectedInstanceLabel} • condition {trader.SelectedInstanceConditionPercent}%");
        }

        builder.AppendLine();
        builder.AppendLine("Value and destinations:");
        if (trader.BestSellOffer is not null)
        {
            builder.AppendLine($"• Best trader sale: ₽{trader.BestSellOffer.RoubleEquivalent:N0} to {trader.BestSellOffer.TraderName}");
        }
        else
        {
            builder.AppendLine("• No supported trader buyer was found.");
        }

        if (market.EstimatedNetSale.HasValue)
        {
            builder.AppendLine($"• Estimated flea net: ₽{market.EstimatedNetSale.Value:N0} after estimated fee");
        }
        else
        {
            builder.AppendLine($"• Flea sale: {market.SellUnavailableReason ?? "No reliable local flea estimate is available."}");
        }

        if (market.CheapestAvailableTraderBuyPrice.HasValue)
        {
            builder.AppendLine($"• Cheapest available trader replacement: ₽{market.CheapestAvailableTraderBuyPrice.Value:N0} from {market.CheapestAvailableTraderName}");
        }
        if (market.LowestPrice.HasValue)
        {
            builder.AppendLine($"• Lowest supported market replacement: ₽{market.LowestPrice.Value:N0} • source: {market.MarketPriceSource}");
        }

        if (usage.Found)
        {
            var activeQuestUses = usage.QuestUses.Count(use => use.IsActive && !use.ConditionCompleted);
            var futureQuestUses = usage.QuestUses.Count(use => !use.IsActive && !use.QuestCompleted);
            var nextUpgrades = usage.UpgradeUses.Count(use => use.IsNextUpgrade && !use.IsMet);
            builder.AppendLine();
            builder.AppendLine("Progression use:");
            builder.AppendLine($"• Owned: {FormatCount(usage.OwnedQuantity)} • FIR: {FormatCount(usage.OwnedFoundInRaidQuantity)}");
            builder.AppendLine($"• Active quest requirements: {activeQuestUses:N0} • future quest requirements: {futureQuestUses:N0}");
            builder.AppendLine($"• Next hideout upgrade uses: {nextUpgrades:N0}");
            builder.AppendLine($"• Produced by recipes: {usage.ProducedBy.Count:N0} • used by recipes: {usage.UsedBy.Count:N0}");
        }

        var bestTrader = trader.BestSellOffer?.RoubleEquivalent ?? 0L;
        var flea = market.EstimatedNetSale ?? 0L;
        builder.AppendLine();
        builder.AppendLine(flea > bestTrader
            ? $"Recommendation: flea estimate is approximately ₽{flea - bestTrader:N0} above the best trader sale, subject to FIR eligibility and current offer reliability."
            : bestTrader > 0L
                ? $"Recommendation: {trader.BestSellOffer!.TraderName} currently beats or matches the supported flea net estimate."
                : "Recommendation: review this item manually because HERMES could not resolve a supported sale destination.");

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "ITEM • TRADER • MARKET • USAGE",
            [new AssistantAction("Open Item Search", "Item Search")]);
    }

    private static async Task<AssistantMessage> BuildUnknownResponseAsync(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        if (selectedItem is not null
            && Plugin.Settings.IncludeSelectedItemInAssistant.Value
            && ReferencesSelectedItem(prompt))
        {
            return await BuildItemResponseAsync(prompt, selectedItem, selectedInstanceKey);
        }

        var resolved = await ResolveItemFromPromptAsync(prompt);
        if (resolved is not null)
        {
            return await BuildItemResponseAsync(prompt, resolved, null);
        }

        return new AssistantMessage(
            false,
            "I could not map that question to a supported Alpha12.0 intent. Try asking about loadout readiness, the best raid, safe-to-sell stash items, ready crafts, hideout upgrades, or a selected item. Follow-up conversational context is planned for a later Alpha12 build.",
            "LOCAL INTENT ENGINE",
            [
                new AssistantAction("Open Loadout", "Loadout"),
                new AssistantAction("Open Stash", "Stash"),
                new AssistantAction("Open Crafts", "Crafts")
            ]);
    }

    private static async Task<HermesItemSummary?> ResolveItemFromPromptAsync(string prompt)
    {
        var queries = BuildItemQueries(prompt).ToList();
        foreach (var query in queries)
        {
            if (query.Length < Plugin.Settings.GetMinimumSearchCharacters())
            {
                continue;
            }

            var response = await HermesApiClient.SearchAsync(query, 8);
            var exact = response.Results.FirstOrDefault(item =>
                item.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                || item.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            if (response.Results.Count == 1)
            {
                return response.Results[0];
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildItemQueries(string prompt)
    {
        var trimmed = prompt.Trim().TrimEnd('?', '.', '!');
        yield return trimmed;

        var normalized = trimmed;
        var prefixes = new[]
        {
            "what is", "what's", "how much is", "how much are", "where should i sell", "where can i sell",
            "where can i buy", "should i sell", "do i need", "tell me about", "price of", "value of"
        };
        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..].Trim();
                break;
            }
        }

        foreach (var article in new[] { "a ", "an ", "the " })
        {
            if (normalized.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[article.Length..].Trim();
                break;
            }
        }

        var suffixes = new[]
        {
            " worth", " on flea", " on the flea", " to trader", " for quests", " for hideout", " for crafting"
        };
        foreach (var suffix in suffixes)
        {
            var index = normalized.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                normalized = normalized[..index].Trim();
            }
        }

        if (!normalized.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalized;
        }
    }

    private void AddWelcomeMessage()
    {
        AddMessage(new AssistantMessage(
            false,
            "Alpha12.0 Assistant is online. Ask a question using the buttons below or type your own. Answers are built from current local HERMES data and never perform game actions.",
            "LOCAL ASSISTANT",
            [
                new AssistantAction("Open Loadout", "Loadout"),
                new AssistantAction("Open Stash", "Stash"),
                new AssistantAction("Open Crafts", "Crafts")
            ]));
    }

    private void AddMessage(AssistantMessage message)
    {
        _messages.Add(message);
        var maximum = Plugin.Settings.GetMaximumAssistantMessages();
        while (_messages.Count > maximum)
        {
            _messages.RemoveAt(0);
        }

        _scrollToBottom = true;
    }

    private static GUIStyle GetMessageStyle()
    {
        return _messageStyle ??= new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            richText = false
        };
    }

    private static AssistantMessage Failure(string? message, string source)
    {
        return new AssistantMessage(
            false,
            message ?? $"HERMES could not read the current {source.ToLowerInvariant()} data.",
            source);
    }

    private static bool ReferencesSelectedItem(string text)
    {
        var normalized = $" {text.Trim().ToLowerInvariant()} ";
        return ContainsAny(
            normalized,
            " this item ",
            " selected item ",
            " that item ",
            " sell this ",
            " buy this ",
            " need this ",
            " does this ",
            " is this ",
            " what is this ",
            " it worth ",
            " sell it ",
            " buy it ",
            " need it ");
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string YesNo(bool value) => value ? "Covered" : "Missing";

    private static int SeverityRank(string severity)
    {
        return severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            ? 0
            : severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;
    }

    private static double GetProfitPercent(HermesCraftSummary craft)
    {
        return craft.EstimatedEconomicInputValue <= 0L
            ? craft.EstimatedEconomicProfit > 0L ? 100d : 0d
            : craft.EstimatedEconomicProfit / (double)craft.EstimatedEconomicInputValue * 100d;
    }

    private static string FormatCount(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.0001d
            ? Math.Round(value).ToString("N0")
            : value.ToString("0.##");
    }

    private static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1d)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1d)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{span.Minutes}m";
    }
}
