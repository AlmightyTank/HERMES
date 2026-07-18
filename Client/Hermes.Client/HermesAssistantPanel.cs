using System.Text;
using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesAssistantPanel
{
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

    private sealed class ItemResolution
    {
        public HermesItemSummary? Item { get; init; }
        public IReadOnlyList<HermesItemSummary> Alternatives { get; init; } = Array.Empty<HermesItemSummary>();
        public int Score { get; init; }
        public bool IsAmbiguous { get; init; }
    }

    private sealed class DynamicEntityMatch
    {
        public HermesAssistantEntityKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Score { get; init; }
    }

    private static readonly string[] SuggestedPrompts =
    [
        "What should I do next?",
        "What is the best raid for me right now?",
        "Should I craft or raid?",
        "How should I prepare for Ground Zero?",
        "What items can I safely sell?",
        "What crafts are ready now?",
        "What hideout upgrades need attention?"
    ];

    private static GUIStyle? _messageStyle;

    private readonly List<AssistantMessage> _messages = [];
    private readonly HermesAssistantConversationContext _conversationContext = new();
    private Vector2 _conversationScroll;
    private string _input = string.Empty;
    private string _status = "Ask HERMES about your current profile, loadout, quests, stash, hideout, crafts, or a selected item.";
    private string _lastPrompt = string.Empty;
    private bool _loading;
    private int _requestVersion;
    private bool _scrollToBottom;
    private string _profileContextToken = string.Empty;

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

        DrawContext(selectedItem, selectedInstanceKey);
        GUILayout.Space(HermesUi.SmallSpace);
        DrawConversation(navigate);
        GUILayout.Space(HermesUi.StandardSpace);
        DrawPromptComposer(selectedItem, selectedInstanceKey);
    }

    public void Clear()
    {
        _requestVersion++;
        _messages.Clear();
        _conversationContext.Reset();
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

    private void DrawContext(HermesItemSummary? selectedItem, string? selectedInstanceKey)
    {
        _conversationContext.UpdateSelectedItem(selectedItem, selectedInstanceKey);
        if (!Plugin.Settings.ShowAssistantConversationContext.Value)
        {
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label("CONTEXT", GUILayout.Width(76f));
        var current = _conversationContext.Current;
        if (current is not null)
        {
            GUILayout.Label($"Conversation: {current.DisplayKind} — {current.Name}", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Forget", GUILayout.Width(62f), GUILayout.Height(HermesUi.ToolbarHeight)))
            {
                _conversationContext.ForgetCurrent();
                _status = "Forgot the current Assistant conversation subject.";
            }
        }
        else
        {
            GUILayout.Label("Conversation: no remembered subject", GUILayout.ExpandWidth(true));
        }

        GUILayout.Label("LOCAL • READ ONLY", GUILayout.Width(130f));
        GUILayout.EndHorizontal();

        if (selectedItem is not null && Plugin.Settings.IncludeSelectedItemInAssistant.Value)
        {
            GUILayout.Label($"Selected item: {selectedItem.Name}");
        }
        else if (Plugin.Settings.EnableAssistantFollowUpContext.Value)
        {
            GUILayout.Label("Follow-up context is enabled for resolved items, quests, maps, crafts, stations, and hideout areas.");
        }
        else
        {
            GUILayout.Label("Follow-up context is disabled in BepInEx/F12.");
        }

        GUILayout.EndVertical();
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
        _loading = true;
        _status = $"Analyzing: {prompt}";

        try
        {
            await EnsureProfileContextAsync();
            if (requestVersion != _requestVersion)
            {
                return;
            }

            _lastPrompt = prompt;
            if (appendUserMessage)
            {
                AddMessage(new AssistantMessage(true, prompt, "QUESTION"));
            }

            if (_conversationContext.TryHandleCommand(prompt, out var contextResponse))
            {
                AddMessage(new AssistantMessage(false, contextResponse, "CONVERSATION CONTEXT"));
                _status = "Updated local conversation context.";
                return;
            }

            var contextResolution = _conversationContext.Resolve(
                prompt,
                selectedItem,
                selectedInstanceKey);
            var effectivePrompt = contextResolution.Prompt;
            var contextOwnsItemSelection = contextResolution.UsedContext
                                            && contextResolution.Subject?.Kind == HermesAssistantEntityKind.Item;
            var effectiveSelectedItem = contextOwnsItemSelection
                ? contextResolution.SelectedItem
                : contextResolution.SelectedItem ?? selectedItem;
            var effectiveInstanceKey = contextOwnsItemSelection
                ? contextResolution.SelectedInstanceKey
                : contextResolution.SelectedInstanceKey ?? selectedInstanceKey;
            var interpretation = HermesAssistantIntentEngine.Interpret(
                effectivePrompt,
                effectiveSelectedItem is not null && Plugin.Settings.IncludeSelectedItemInAssistant.Value);
            var response = await BuildResponseAsync(
                interpretation,
                effectivePrompt,
                effectiveSelectedItem,
                effectiveInstanceKey);
            if (requestVersion != _requestVersion)
            {
                return;
            }

            _conversationContext.RememberResponse(
                response.Source,
                response.Text,
                effectiveSelectedItem,
                effectiveInstanceKey);
            if (contextResolution.UsedContext)
            {
                response = new AssistantMessage(
                    response.IsUser,
                    response.Text,
                    response.Source + " • FOLLOW-UP",
                    response.Actions);
            }

            AddMessage(response);
            _status = contextResolution.UsedContext && contextResolution.Subject is not null
                ? $"Answered using remembered {contextResolution.Subject.DisplayKind.ToLowerInvariant()} context: {contextResolution.Subject.Name}."
                : $"Answered locally using {response.Source.ToLowerInvariant()} data.";
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

    private async Task EnsureProfileContextAsync()
    {
        if (!Plugin.Settings.ResetAssistantContextOnProfileChange.Value)
        {
            return;
        }

        HermesProfileContextResponse response;
        try
        {
            response = await HermesApiClient.GetProfileContextAsync();
        }
        catch (Exception ex)
        {
            if (Plugin.Settings.DetailedLogging.Value)
            {
                Plugin.Log.LogWarning($"[HERMES Assistant] Profile context check failed: {ex.Message}");
            }
            return;
        }

        if (!response.Found || string.IsNullOrWhiteSpace(response.ContextToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_profileContextToken))
        {
            _profileContextToken = response.ContextToken;
            return;
        }

        if (_profileContextToken.Equals(response.ContextToken, StringComparison.Ordinal))
        {
            return;
        }

        _profileContextToken = response.ContextToken;
        _conversationContext.Reset();
        _messages.Clear();
        _lastPrompt = string.Empty;
        AddWelcomeMessage();
        AddMessage(new AssistantMessage(
            false,
            "The active PMC profile changed. HERMES cleared the previous conversation subject so follow-up questions cannot use another profile's context.",
            "CONTEXT RESET"));
    }

    private static async Task<AssistantMessage> BuildResponseAsync(
        HermesAssistantInterpretation interpretation,
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        return interpretation.Intent switch
        {
            HermesAssistantIntent.Help => BuildHelpResponse(),
            HermesAssistantIntent.CrossSystem => await BuildCrossSystemResponseAsync(prompt),
            HermesAssistantIntent.Loadout => await BuildLoadoutResponseAsync(prompt),
            HermesAssistantIntent.RaidPlanner => await BuildRaidPlannerResponseAsync(prompt),
            HermesAssistantIntent.Stash => await BuildStashResponseAsync(),
            HermesAssistantIntent.Crafts => await BuildCraftResponseAsync(prompt),
            HermesAssistantIntent.Hideout => await BuildHideoutResponseAsync(prompt),
            HermesAssistantIntent.Item => await BuildItemResponseAsync(prompt, selectedItem, selectedInstanceKey),
            _ => await BuildDynamicEntityResponseAsync(prompt, selectedItem, selectedInstanceKey)
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
            "HERMES also remembers resolved items, quests, maps, crafts, stations, and hideout areas for follow-up questions. You can ask \"why?\", \"what key?\", \"where do I use it?\", or choose an ambiguity result with \"the second one\". It does not buy, sell, insure, equip, move, craft, or complete anything."
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


    private static async Task<AssistantMessage> BuildCrossSystemResponseAsync(string prompt)
    {
        if (!Plugin.Settings.EnableAssistantCrossSystemReasoning.Value)
        {
            return new AssistantMessage(
                false,
                "Cross-system reasoning is disabled in BepInEx/F12. Enable Assistant → Enable cross-system reasoning to rank combined next steps.",
                "CROSS-SYSTEM REASONING");
        }

        var loadoutTask = HermesApiClient.GetLoadoutSummaryAsync(
            Plugin.Settings.CreateLoadoutRequestSettings());
        var stashTask = HermesApiClient.GetStashSummaryAsync(
            Plugin.Settings.CreateStashRequestSettings());
        var craftsTask = HermesApiClient.GetCraftsAsync();
        var hideoutTask = HermesApiClient.GetHideoutSummaryAsync();
        await Task.WhenAll(loadoutTask, stashTask, craftsTask, hideoutTask);

        var loadout = await loadoutTask;
        var stash = await stashTask;
        var crafts = await craftsTask;
        var hideout = await hideoutTask;
        if (!loadout.Found && !stash.Found && !crafts.Found && !hideout.Found)
        {
            return new AssistantMessage(
                false,
                "HERMES could not read any of the current loadout, Raid Planner, stash, craft, or hideout snapshots.",
                "CROSS-SYSTEM REASONING");
        }

        var mapName = loadout.Found
            ? HermesAssistantIntentEngine.ResolveMapAlias(
                prompt,
                loadout.RaidPlans.Select(plan => plan.MapName))
            : null;
        if (!string.IsNullOrWhiteSpace(mapName))
        {
            var plan = loadout.RaidPlans.FirstOrDefault(candidate =>
                candidate.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase));
            if (plan is not null)
            {
                return BuildMapPreparationResponse(loadout, plan);
            }
        }

        if (ContainsAny(prompt, "craft or raid", "should i craft or raid"))
        {
            return BuildCraftOrRaidResponse(loadout, crafts);
        }

        var recommendations = HermesAssistantReasoningEngine.BuildRecommendations(
            loadout,
            stash,
            crafts,
            hideout,
            Plugin.Settings.PreferPreparedRaidRecommendations.Value,
            Plugin.Settings.IncludeEconomicAssistantRecommendations.Value,
            Plugin.Settings.GetMaximumAssistantRecommendations());
        var builder = new StringBuilder();
        builder.AppendLine("Ranked next steps:");
        if (recommendations.Count == 0)
        {
            builder.AppendLine("• No actionable recommendation was found in the current snapshots. Open the individual tabs to inspect their complete data.");
        }
        else
        {
            for (var index = 0; index < recommendations.Count; index++)
            {
                var recommendation = recommendations[index];
                builder.AppendLine($"{index + 1}. [{recommendation.Category}] {recommendation.Title}");
                builder.AppendLine($"   Why: {recommendation.Detail}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Snapshot used:");
        builder.AppendLine($"• Loadout: {(loadout.Found ? $"{loadout.Readiness} {loadout.ReadinessScore}/100" : "unavailable")}");
        builder.AppendLine($"• Raid plans: {(loadout.Found ? loadout.RaidPlans.Count(plan => plan.ActiveQuestCount > 0).ToString("N0") : "unavailable")}");
        builder.AppendLine($"• Stash surplus: {(stash.Found ? FormatCount(stash.PotentiallySellQuantity) : "unavailable")}");
        builder.AppendLine($"• Ready crafts: {(crafts.Found ? crafts.Crafts.Count(craft => craft.CanStartNow).ToString("N0") : "unavailable")}");
        builder.AppendLine($"• Ready hideout upgrades: {(hideout.Found ? hideout.ReadyAreaCount.ToString("N0") : "unavailable")}");

        var actions = recommendations
            .Select(recommendation => new AssistantAction($"Open {recommendation.Category}", recommendation.TabName))
            .GroupBy(action => action.TabName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(4)
            .ToList();
        if (actions.Count == 0)
        {
            actions.Add(new AssistantAction("Open Loadout", "Loadout"));
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "CROSS-SYSTEM REASONING",
            actions);
    }

    private static AssistantMessage BuildMapPreparationResponse(
        HermesLoadoutSummaryResponse loadout,
        HermesRaidPlanSummary plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Preparation plan: {plan.MapName}");
        builder.AppendLine($"Raid plan: {plan.Status} • {plan.ActiveQuestCount:N0} active quest(s) • {Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount):N0} incomplete objective(s)");
        builder.AppendLine($"Current loadout: {loadout.Readiness} ({loadout.ReadinessScore}/100)");
        AppendPlanRequirements(builder, plan);

        var warnings = loadout.Warnings
            .OrderBy(warning => SeverityRank(warning.Severity))
            .ThenBy(warning => warning.Category, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        builder.AppendLine();
        builder.AppendLine("Before deployment:");
        if (warnings.Count == 0 && plan.MissingRequirementCount == 0)
        {
            builder.AppendLine("• No loadout or encoded pre-raid requirement issue was detected with the current F12 thresholds.");
        }
        else
        {
            foreach (var warning in warnings)
            {
                builder.AppendLine($"• [{warning.Category}] {warning.Message}");
            }
        }

        var acquire = plan.CombinedRequirements
            .Where(requirement => requirement.AcquireInRaid && !requirement.IsSatisfied)
            .ToList();
        if (acquire.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Acquire during raid:");
            foreach (var requirement in acquire.Take(6))
            {
                builder.AppendLine($"• {requirement.RequiredEquipment}: {requirement.Note}");
            }
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            $"CROSS-SYSTEM • {plan.MapName.ToUpperInvariant()}",
            [
                new AssistantAction("Open Raid Planner", "Loadout/Raid Planner"),
                new AssistantAction("Open Loadout", "Loadout")
            ]);
    }

    private static AssistantMessage BuildCraftOrRaidResponse(
        HermesLoadoutSummaryResponse loadout,
        HermesCraftsResponse crafts)
    {
        var bestRaid = loadout.Found
            ? HermesAssistantReasoningEngine.RankRaids(
                    loadout,
                    Plugin.Settings.PreferPreparedRaidRecommendations.Value)
                .FirstOrDefault()
            : null;
        var bestCraft = crafts.Found
            ? crafts.Crafts
                .Where(craft => craft.CanStartNow && !craft.IsActive && !craft.IsComplete)
                .OrderByDescending(craft => craft.EstimatedEconomicProfitPerHour)
                .ThenByDescending(craft => craft.EstimatedEconomicProfit)
                .FirstOrDefault()
            : null;

        var builder = new StringBuilder();
        builder.AppendLine("Craft versus raid:");
        if (bestRaid is not null)
        {
            builder.AppendLine($"• Raid: {bestRaid.Plan.MapName} — {bestRaid.Plan.Status}; {string.Join("; ", bestRaid.Reasons)}.");
        }
        else
        {
            builder.AppendLine("• Raid: no active map-specific quest plan is available.");
        }

        if (bestCraft is not null)
        {
            builder.AppendLine($"• Craft: {bestCraft.OutputQuantity:N0} × {bestCraft.OutputName} at {bestCraft.StationName} — ready now, {FormatDuration(bestCraft.DurationSeconds)}, estimated profit ₽{bestCraft.EstimatedEconomicProfit:N0} (₽{bestCraft.EstimatedEconomicProfitPerHour:N0}/h).");
        }
        else
        {
            builder.AppendLine("• Craft: no ready recipe matches the current state.");
        }

        builder.AppendLine();
        if (loadout.Found && loadout.CriticalCount > 0)
        {
            builder.AppendLine("Recommendation: fix the critical loadout findings first. A ready craft can run while you correct the loadout.");
        }
        else if (bestRaid is not null && bestRaid.Plan.MissingRequirementCount == 0)
        {
            builder.AppendLine(bestCraft is not null
                ? $"Recommendation: start {bestCraft.OutputName}, then run {bestRaid.Plan.MapName} while the station works."
                : $"Recommendation: run {bestRaid.Plan.MapName}; it is the strongest current quest plan and has no missing encoded pre-raid requirement.");
        }
        else if (bestCraft is not null)
        {
            builder.AppendLine($"Recommendation: start {bestCraft.OutputName} while resolving the missing raid requirements.");
        }
        else
        {
            builder.AppendLine("Recommendation: inspect Raid Planner and Crafts directly; neither side currently has a clear ready action.");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "CROSS-SYSTEM • CRAFT OR RAID",
            [
                new AssistantAction("Open Raid Planner", "Loadout/Raid Planner"),
                new AssistantAction("Open Crafts", "Crafts")
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

    private static async Task<AssistantMessage> BuildRaidPlannerResponseAsync(string prompt)
    {
        var response = await HermesApiClient.GetLoadoutSummaryAsync(
            Plugin.Settings.CreateLoadoutRequestSettings());
        if (!response.Found)
        {
            return Failure(response.Message, "RAID PLANNER");
        }

        var plans = response.RaidPlans
            .Where(plan => plan.ActiveQuestCount > 0)
            .ToList();
        if (plans.Count == 0)
        {
            return new AssistantMessage(
                false,
                "No map-specific active quest plan is currently available. Check whether active quests contain raid-location objectives.",
                "RAID PLANNER",
                [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
        }

        var questRows = plans
            .SelectMany(plan => plan.Quests.Select(quest => new { Plan = plan, Quest = quest }))
            .GroupBy(row => row.Quest.QuestName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var questCandidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            questRows,
            row => row.Quest.QuestName);

        var mapAlias = HermesAssistantIntentEngine.ResolveMapAlias(prompt, plans.Select(plan => plan.MapName));
        var mapCandidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            plans,
            plan => plan.MapName);

        var questTop = questCandidates.FirstOrDefault();
        var mapTop = mapCandidates.FirstOrDefault();
        var explicitlyQuestFocused = ContainsAny(prompt, "quest", "objective", "what do i need for", "requirements for");
        if (questTop is not null
            && HermesAssistantIntentEngine.IsConfident(questTop.Score)
            && (explicitlyQuestFocused || mapTop is null || questTop.Score > mapTop.Score))
        {
            if (HermesAssistantIntentEngine.IsAmbiguous(questCandidates))
            {
                return BuildAmbiguityResponse(
                    "quest",
                    questCandidates.Select(candidate => candidate.Name),
                    "Loadout/Raid Planner");
            }

            return BuildQuestPlanResponse(response, questTop.Name);
        }

        HermesRaidPlanSummary? selectedMap = null;
        if (!string.IsNullOrWhiteSpace(mapAlias))
        {
            selectedMap = plans.FirstOrDefault(plan =>
                plan.MapName.Equals(mapAlias, StringComparison.OrdinalIgnoreCase));
        }
        else if (mapTop is not null && HermesAssistantIntentEngine.IsConfident(mapTop.Score))
        {
            if (HermesAssistantIntentEngine.IsAmbiguous(mapCandidates))
            {
                return BuildAmbiguityResponse(
                    "map",
                    mapCandidates.Select(candidate => candidate.Name),
                    "Loadout/Raid Planner");
            }

            selectedMap = mapTop.Value;
        }

        if (selectedMap is not null)
        {
            return BuildMapPlanResponse(selectedMap);
        }

        var rankedPlans = HermesAssistantReasoningEngine.RankRaids(
            response,
            Plugin.Settings.PreferPreparedRaidRecommendations.Value);
        var bestRanking = rankedPlans[0];
        var best = bestRanking.Plan;
        var builder = new StringBuilder();
        builder.AppendLine($"Recommended map: {best.MapName}");
        builder.AppendLine($"Plan status: {best.Status}");
        builder.AppendLine($"Active quests: {best.ActiveQuestCount:N0}");
        builder.AppendLine($"Incomplete objectives: {Math.Max(0, best.ObjectiveCount - best.CompletedObjectiveCount):N0}");
        builder.AppendLine($"Missing pre-raid requirements: {best.MissingRequirementCount:N0}");
        builder.AppendLine($"Why: {string.Join("; ", bestRanking.Reasons)}.");
        AppendPlanRequirements(builder, best);

        builder.AppendLine();
        builder.AppendLine("Other strong options:");
        foreach (var ranking in rankedPlans.Skip(1).Take(3))
        {
            var plan = ranking.Plan;
            builder.AppendLine($"• {plan.MapName}: {plan.ActiveQuestCount:N0} quest(s), {Math.Max(0, plan.ObjectiveCount - plan.CompletedObjectiveCount):N0} incomplete objective(s), {plan.MissingRequirementCount:N0} missing requirement(s)");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "RAID PLANNER • BEST MAP",
            [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
    }

    private static AssistantMessage BuildMapPlanResponse(HermesRaidPlanSummary plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{plan.MapName} raid plan");
        builder.AppendLine($"Status: {plan.Status}");
        builder.AppendLine($"Active quests: {plan.ActiveQuestCount:N0}");
        builder.AppendLine($"Objectives: {plan.CompletedObjectiveCount:N0}/{plan.ObjectiveCount:N0} complete");
        builder.AppendLine($"Missing pre-raid requirements: {plan.MissingRequirementCount:N0}");

        if (plan.Quests.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Active quests:");
            foreach (var quest in plan.Quests
                         .OrderByDescending(quest => quest.MissingRequirementCount)
                         .ThenBy(quest => quest.QuestName, StringComparer.OrdinalIgnoreCase)
                         .Take(10))
            {
                builder.AppendLine($"• {quest.QuestName} — {quest.CompletedObjectiveCount:N0}/{quest.ObjectiveCount:N0} objectives • {quest.Status}");
            }
        }

        AppendPlanRequirements(builder, plan);
        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            $"RAID PLANNER • {plan.MapName.ToUpperInvariant()}",
            [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
    }

    private static AssistantMessage BuildQuestPlanResponse(
        HermesLoadoutSummaryResponse response,
        string questName)
    {
        var matches = response.RaidPlans
            .SelectMany(plan => plan.Quests
                .Where(quest => quest.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase))
                .Select(quest => new { Plan = plan, Quest = quest }))
            .ToList();
        if (matches.Count == 0)
        {
            return new AssistantMessage(
                false,
                $"I recognized {questName}, but it is not present in the current active Raid Planner snapshot.",
                "QUEST RESOLUTION",
                [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
        }

        var match = matches
            .OrderByDescending(row => row.Quest.ObjectiveCount - row.Quest.CompletedObjectiveCount)
            .First();
        var builder = new StringBuilder();
        builder.AppendLine(match.Quest.QuestName);
        builder.AppendLine($"Trader: {match.Quest.TraderName}");
        builder.AppendLine($"Map: {match.Plan.MapName}");
        builder.AppendLine($"Status: {match.Quest.Status}");
        builder.AppendLine($"Objectives: {match.Quest.CompletedObjectiveCount:N0}/{match.Quest.ObjectiveCount:N0} complete");

        var objectives = match.Quest.Objectives
            .Where(objective => !objective.IsCompleted || Plugin.Settings.ShowCompletedQuestObjectives.Value)
            .Take(10)
            .ToList();
        if (objectives.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Objectives:");
            foreach (var objective in objectives)
            {
                builder.AppendLine($"• [{objective.Status}] {objective.Description}");
            }
        }

        var requirements = match.Plan.CombinedRequirements
            .Where(requirement => requirement.QuestNames.Any(name =>
                name.Equals(match.Quest.QuestName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (requirements.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Quest requirements:");
            foreach (var requirement in requirements.Take(10))
            {
                if (requirement.AcquireInRaid)
                {
                    builder.AppendLine($"• Acquire during raid: {requirement.RequiredEquipment} — {requirement.Note}");
                }
                else if (requirement.IsSatisfied)
                {
                    builder.AppendLine($"• Ready: {requirement.RequiredEquipment} × {FormatCount(requirement.RequiredQuantity)}");
                }
                else
                {
                    builder.AppendLine($"• Missing: {requirement.RequiredEquipment} × {FormatCount(requirement.MissingQuantity)} — {requirement.RequirementKind}");
                }
            }
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "QUEST • RAID PLANNER",
            [new AssistantAction("Open Raid Planner", "Loadout/Raid Planner")]);
    }

    private static void AppendPlanRequirements(StringBuilder builder, HermesRaidPlanSummary plan)
    {
        var missing = plan.CombinedRequirements
            .Where(requirement => !requirement.IsSatisfied && !requirement.AcquireInRaid)
            .Take(6)
            .ToList();
        var acquire = plan.CombinedRequirements
            .Where(requirement => requirement.AcquireInRaid)
            .Take(5)
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

        var outputRepresentatives = response.Crafts
            .Where(craft => !string.IsNullOrWhiteSpace(craft.OutputName))
            .GroupBy(craft => craft.OutputName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(craft => craft.CanStartNow)
                .ThenByDescending(craft => craft.IsAvailable)
                .ThenByDescending(craft => craft.EstimatedEconomicProfit)
                .First())
            .ToList();
        var outputCandidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            outputRepresentatives,
            craft => craft.OutputName);
        var stationNames = response.Crafts
            .Select(craft => craft.StationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stationCandidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            stationNames,
            name => name);

        var outputTop = outputCandidates.FirstOrDefault();
        var stationTop = stationCandidates.FirstOrDefault();
        var asksForSpecificRecipe = ContainsAny(
            prompt,
            "why can", "why cant", "why can't", "cannot craft", "can't craft", "can i craft",
            "what do i need", "requirements", "recipe for", "make ", "produce ", "craft ");
        if (outputTop is not null
            && HermesAssistantIntentEngine.IsConfident(outputTop.Score)
            && (asksForSpecificRecipe || stationTop is null || outputTop.Score >= stationTop.Score))
        {
            if (HermesAssistantIntentEngine.IsAmbiguous(outputCandidates))
            {
                return BuildAmbiguityResponse(
                    "craft output",
                    outputCandidates.Select(candidate => candidate.Name),
                    "Crafts");
            }

            return await BuildSpecificCraftResponseAsync(response, outputTop.Name);
        }

        if (stationTop is not null && HermesAssistantIntentEngine.IsConfident(stationTop.Score))
        {
            if (HermesAssistantIntentEngine.IsAmbiguous(stationCandidates))
            {
                return BuildAmbiguityResponse(
                    "crafting station",
                    stationCandidates.Select(candidate => candidate.Name),
                    "Crafts");
            }

            return BuildStationCraftResponse(response, stationTop.Name, prompt);
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

        return BuildCraftListResponse(response, selected, heading, "CRAFT ANALYSIS");
    }

    private static async Task<AssistantMessage> BuildSpecificCraftResponseAsync(
        HermesCraftsResponse response,
        string outputName)
    {
        var matching = response.Crafts
            .Where(craft => craft.OutputName.Equals(outputName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.IsAvailable)
            .ThenByDescending(craft => craft.EstimatedEconomicProfit)
            .ToList();
        if (matching.Count == 0)
        {
            return new AssistantMessage(
                false,
                $"I recognized {outputName}, but no current craft recipe was returned for it.",
                "CRAFT RESOLUTION",
                [new AssistantAction("Open Crafts", "Crafts")]);
        }

        var selected = matching[0];
        var detail = await HermesApiClient.GetCraftDetailAsync(selected.CraftKey);
        var builder = new StringBuilder();
        builder.AppendLine($"{selected.OutputQuantity:N0} × {selected.OutputName}");
        builder.AppendLine($"Station: {selected.StationName} level {selected.RequiredStationLevel:N0}");
        builder.AppendLine($"Status: {selected.Status}");
        builder.AppendLine($"Duration: {FormatDuration(selected.DurationSeconds)}");
        builder.AppendLine($"Available: {(selected.IsAvailable ? "Yes" : "No")} • Ready now: {(selected.CanStartNow ? "Yes" : "No")}");

        if (detail.Found)
        {
            if (!string.IsNullOrWhiteSpace(detail.RequiredQuestName))
            {
                builder.AppendLine($"Quest unlock: {detail.RequiredQuestName} — {(detail.RequiredQuestComplete ? "complete" : "not complete")}");
            }

            var missing = detail.Ingredients
                .Where(ingredient => ingredient.Missing > 0d || !ingredient.IsMet)
                .OrderByDescending(ingredient => ingredient.Missing)
                .ThenBy(ingredient => ingredient.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            builder.AppendLine();
            if (missing.Count == 0)
            {
                builder.AppendLine("Ingredients: all listed requirements are currently met.");
            }
            else
            {
                builder.AppendLine("Missing requirements:");
                foreach (var ingredient in missing.Take(10))
                {
                    var tool = ingredient.IsReusableTool ? " reusable tool" : string.Empty;
                    builder.AppendLine($"• {ingredient.Name}: own {FormatCount(ingredient.Owned)}/{FormatCount(ingredient.Required)} • missing {FormatCount(ingredient.Missing)}{tool}");
                }
            }

            var reservedOrUnavailable = detail.Ingredients
                .Where(ingredient => ingredient.UnavailableQuantity > 0d)
                .Take(5)
                .ToList();
            if (reservedOrUnavailable.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Acquisition gaps:");
                foreach (var ingredient in reservedOrUnavailable)
                {
                    builder.AppendLine($"• {ingredient.Name}: {FormatCount(ingredient.UnavailableQuantity)} cannot currently be sourced by the configured plan");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Estimated additional cash: ₽{selected.EstimatedAdditionalCashCost:N0}");
        builder.AppendLine($"Economic input value: ₽{selected.EstimatedEconomicInputValue:N0}");
        builder.AppendLine($"Output value: ₽{selected.EstimatedOutputValue:N0}");
        builder.AppendLine($"Economic profit: ₽{selected.EstimatedEconomicProfit:N0} • ₽{selected.EstimatedEconomicProfitPerHour:N0}/h");

        if (matching.Count > 1)
        {
            builder.AppendLine();
            builder.AppendLine($"HERMES found {matching.Count:N0} recipes for this output. The best currently usable recipe is shown above.");
            foreach (var alternative in matching.Skip(1).Take(3))
            {
                builder.AppendLine($"• Alternative: {alternative.StationName} L{alternative.RequiredStationLevel} — {alternative.Status}");
            }
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "CRAFT • RECIPE",
            [new AssistantAction("Open Crafts", "Crafts")]);
    }

    private static AssistantMessage BuildStationCraftResponse(
        HermesCraftsResponse response,
        string stationName,
        string prompt)
    {
        IEnumerable<HermesCraftSummary> selected = response.Crafts.Where(craft =>
            craft.StationName.Equals(stationName, StringComparison.OrdinalIgnoreCase));
        string heading;
        if (ContainsAny(prompt, "profitable", "profit"))
        {
            selected = selected.Where(craft => craft.EstimatedEconomicProfit >= Plugin.Settings.GetMinimumCraftProfit());
            heading = $"Profitable crafts at {stationName}";
        }
        else if (ContainsAny(prompt, "available", "ready", "can craft", "can i make"))
        {
            selected = selected.Where(craft => craft.CanStartNow);
            heading = $"Crafts ready now at {stationName}";
        }
        else
        {
            heading = $"Crafts at {stationName}";
        }

        return BuildCraftListResponse(response, selected, heading, $"CRAFT STATION • {stationName.ToUpperInvariant()}");
    }

    private static AssistantMessage BuildCraftListResponse(
        HermesCraftsResponse response,
        IEnumerable<HermesCraftSummary> selected,
        string heading,
        string source)
    {
        var rows = selected
            .OrderByDescending(craft => craft.CanStartNow)
            .ThenByDescending(craft => craft.EstimatedEconomicProfitPerHour)
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
                builder.AppendLine($"• {craft.OutputQuantity:N0} × {craft.OutputName} at {craft.StationName} L{craft.RequiredStationLevel}: {craft.Status} • {FormatDuration(craft.DurationSeconds)} • profit ₽{craft.EstimatedEconomicProfit:N0} • ₽{craft.EstimatedEconomicProfitPerHour:N0}/h");
            }
        }

        var active = response.Crafts.Count(craft => craft.IsActive && !craft.IsComplete);
        var complete = response.Crafts.Count(craft => craft.IsComplete);
        builder.AppendLine();
        builder.AppendLine($"Active productions: {active:N0} • Ready to collect: {complete:N0}");

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            source,
            [new AssistantAction("Open Crafts", "Crafts")]);
    }

    private static async Task<AssistantMessage> BuildHideoutResponseAsync(string prompt)
    {
        var response = await HermesApiClient.GetHideoutSummaryAsync();
        if (!response.Found)
        {
            return Failure(response.Message, "HIDEOUT");
        }

        var areaCandidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            response.Areas,
            area => area.Name,
            area => GetHideoutAliases(area.Name));
        var areaTop = areaCandidates.FirstOrDefault();
        if (areaTop is not null && HermesAssistantIntentEngine.IsConfident(areaTop.Score))
        {
            if (HermesAssistantIntentEngine.IsAmbiguous(areaCandidates))
            {
                return BuildAmbiguityResponse(
                    "hideout area",
                    areaCandidates.Select(candidate => candidate.Name),
                    "Hideout");
            }

            return await BuildHideoutAreaResponseAsync(response, areaTop.Value);
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

    private static async Task<AssistantMessage> BuildHideoutAreaResponseAsync(
        HermesHideoutSummaryResponse summary,
        HermesHideoutAreaSummary area)
    {
        var detail = await HermesApiClient.GetHideoutAreaAsync(area.AreaKey);
        var builder = new StringBuilder();
        builder.AppendLine(area.Name);
        builder.AppendLine($"Level: {area.CurrentLevel:N0}/{area.MaximumLevel:N0}");
        if (area.TargetLevel.HasValue)
        {
            builder.AppendLine($"Next target: level {area.TargetLevel.Value:N0}");
        }
        builder.AppendLine($"Status: {area.Status}");
        if (area.IsConstructing && area.SecondsUntilComplete.HasValue)
        {
            builder.AppendLine($"Construction remaining: {FormatDuration(Convert.ToInt32(Math.Min(int.MaxValue, area.SecondsUntilComplete.Value)))}");
        }

        if (detail.Found)
        {
            var missing = detail.Requirements
                .Where(requirement => !requirement.IsMet || requirement.Missing > 0d)
                .OrderByDescending(requirement => requirement.Missing)
                .ThenBy(requirement => requirement.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            builder.AppendLine();
            if (missing.Count == 0)
            {
                builder.AppendLine("Requirements: all listed requirements are currently met.");
            }
            else
            {
                builder.AppendLine("Missing requirements:");
                foreach (var requirement in missing.Take(12))
                {
                    if (requirement.Type.Equals("Item", StringComparison.OrdinalIgnoreCase))
                    {
                        var fir = requirement.FoundInRaidRequired ? " • FIR required" : string.Empty;
                        var source = string.IsNullOrWhiteSpace(requirement.AcquisitionSource)
                            ? string.Empty
                            : $" • {requirement.AcquisitionSource}";
                        builder.AppendLine($"• {requirement.Name}: own {FormatCount(requirement.Owned)}/{FormatCount(requirement.Required)} • missing {FormatCount(requirement.Missing)}{fir}{source}");
                    }
                    else
                    {
                        var details = string.IsNullOrWhiteSpace(requirement.Details)
                            ? string.Empty
                            : $" — {requirement.Details}";
                        builder.AppendLine($"• {requirement.Type}: {requirement.Name}{details}");
                    }
                }
            }

            builder.AppendLine();
            builder.AppendLine($"Estimated missing acquisition cost: ₽{detail.EstimatedMissingAcquisitionCost:N0}");
            if (detail.ConstructionSeconds > 0)
            {
                builder.AppendLine($"Construction duration: {FormatDuration(detail.ConstructionSeconds)}");
            }
        }

        if (area.Name.Contains("Generator", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            builder.AppendLine($"Generator active: {(summary.Resources.GeneratorActive ? "Yes" : "No")}");
            builder.AppendLine($"Fuel containers: {summary.Resources.FuelContainerCount:N0} • total fuel resource: {summary.Resources.FuelResourceRemaining:N0}");
            if (summary.Resources.EstimatedGeneratorRuntimeSeconds.HasValue)
            {
                builder.AppendLine($"Estimated runtime: {FormatDuration(Convert.ToInt32(Math.Min(int.MaxValue, summary.Resources.EstimatedGeneratorRuntimeSeconds.Value)))}");
            }
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "HIDEOUT • AREA",
            [new AssistantAction("Open Hideout", "Hideout")]);
    }

    private static IEnumerable<string> GetHideoutAliases(string areaName)
    {
        if (areaName.Contains("Intelligence", StringComparison.OrdinalIgnoreCase))
        {
            yield return "intel center";
        }

        if (areaName.Contains("Medical", StringComparison.OrdinalIgnoreCase))
        {
            yield return "medstation";
        }

        if (areaName.Contains("Nutrition", StringComparison.OrdinalIgnoreCase))
        {
            yield return "nutrition unit";
        }

        if (areaName.Contains("Shooting", StringComparison.OrdinalIgnoreCase))
        {
            yield return "shooting range";
        }
    }

    private static async Task<AssistantMessage> BuildItemResponseAsync(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        var useSelectedContext = selectedItem is not null
                                 && Plugin.Settings.IncludeSelectedItemInAssistant.Value
                                 && ReferencesSelectedItem(prompt);
        if (useSelectedContext)
        {
            return await BuildResolvedItemResponseAsync(
                prompt,
                selectedItem!,
                selectedInstanceKey,
                true);
        }

        var resolution = await ResolveItemFromPromptAsync(prompt);
        if (resolution.IsAmbiguous)
        {
            return BuildAmbiguityResponse(
                "item",
                resolution.Alternatives.Select(item =>
                    string.IsNullOrWhiteSpace(item.ShortName) || item.ShortName.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
                        ? item.Name
                        : $"{item.Name} ({item.ShortName})"),
                "Item Search");
        }

        if (resolution.Item is null)
        {
            return new AssistantMessage(
                false,
                "I could not identify a supported player-facing item in that question. Use a more exact item name, select an item through Item Search, or use Ask HERMES from an item row.",
                "ITEM RESOLUTION",
                [new AssistantAction("Open Item Search", "Item Search")]);
        }

        return await BuildResolvedItemResponseAsync(prompt, resolution.Item, null, false);
    }

    private static async Task<AssistantMessage> BuildResolvedItemResponseAsync(
        string prompt,
        HermesItemSummary item,
        string? selectedInstanceKey,
        bool useSelectedInstance)
    {
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

            if (ContainsAny(prompt, "need", "quest", "hideout", "craft"))
            {
                var active = usage.QuestUses
                    .Where(use => use.IsActive && !use.ConditionCompleted)
                    .Take(5)
                    .ToList();
                var upgrades = usage.UpgradeUses
                    .Where(use => !use.IsMet)
                    .OrderByDescending(use => use.IsNextUpgrade)
                    .Take(5)
                    .ToList();
                if (active.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("Active quest uses:");
                    foreach (var use in active)
                    {
                        builder.AppendLine($"• {use.QuestName}: {use.ProgressText}");
                    }
                }
                if (upgrades.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("Hideout uses:");
                    foreach (var use in upgrades)
                    {
                        builder.AppendLine($"• {use.AreaName} L{use.TargetLevel}: own {FormatCount(use.Owned)}/{FormatCount(use.Required)} • missing {FormatCount(use.Missing)}");
                    }
                }
            }
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

    private static async Task<AssistantMessage> BuildDynamicEntityResponseAsync(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        if (selectedItem is not null
            && Plugin.Settings.IncludeSelectedItemInAssistant.Value
            && ReferencesSelectedItem(prompt))
        {
            return await BuildResolvedItemResponseAsync(prompt, selectedItem, selectedInstanceKey, true);
        }

        var loadoutTask = HermesApiClient.GetLoadoutSummaryAsync(
            Plugin.Settings.CreateLoadoutRequestSettings());
        var craftTask = HermesApiClient.GetCraftsAsync();
        var hideoutTask = HermesApiClient.GetHideoutSummaryAsync();
        var itemTask = ResolveItemFromPromptAsync(prompt);
        await Task.WhenAll(loadoutTask, craftTask, hideoutTask, itemTask);

        var loadout = loadoutTask.Result;
        var crafts = craftTask.Result;
        var hideout = hideoutTask.Result;
        var itemResolution = itemTask.Result;
        var matches = new List<DynamicEntityMatch>();

        if (loadout.Found)
        {
            var questNames = loadout.RaidPlans
                .SelectMany(plan => plan.Quests)
                .Select(quest => quest.QuestName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var questTop = HermesAssistantIntentEngine.RankCandidates(prompt, questNames, name => name).FirstOrDefault();
            if (questTop is not null)
            {
                matches.Add(new DynamicEntityMatch
                {
                    Kind = HermesAssistantEntityKind.Quest,
                    Name = questTop.Name,
                    Score = questTop.Score
                });
            }

            var mapAlias = HermesAssistantIntentEngine.ResolveMapAlias(
                prompt,
                loadout.RaidPlans.Select(plan => plan.MapName));
            if (!string.IsNullOrWhiteSpace(mapAlias))
            {
                matches.Add(new DynamicEntityMatch
                {
                    Kind = HermesAssistantEntityKind.Map,
                    Name = mapAlias,
                    Score = 100
                });
            }
            else
            {
                var mapTop = HermesAssistantIntentEngine.RankCandidates(
                    prompt,
                    loadout.RaidPlans,
                    plan => plan.MapName).FirstOrDefault();
                if (mapTop is not null)
                {
                    matches.Add(new DynamicEntityMatch
                    {
                        Kind = HermesAssistantEntityKind.Map,
                        Name = mapTop.Name,
                        Score = mapTop.Score
                    });
                }
            }
        }

        if (crafts.Found)
        {
            var outputNames = crafts.Crafts
                .Select(craft => craft.OutputName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var outputTop = HermesAssistantIntentEngine.RankCandidates(prompt, outputNames, name => name).FirstOrDefault();
            if (outputTop is not null)
            {
                matches.Add(new DynamicEntityMatch
                {
                    Kind = HermesAssistantEntityKind.Craft,
                    Name = outputTop.Name,
                    Score = outputTop.Score
                });
            }

            var stationNames = crafts.Crafts
                .Select(craft => craft.StationName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var stationTop = HermesAssistantIntentEngine.RankCandidates(prompt, stationNames, name => name).FirstOrDefault();
            if (stationTop is not null)
            {
                matches.Add(new DynamicEntityMatch
                {
                    Kind = HermesAssistantEntityKind.CraftingStation,
                    Name = stationTop.Name,
                    Score = stationTop.Score
                });
            }
        }

        if (hideout.Found)
        {
            var areaTop = HermesAssistantIntentEngine.RankCandidates(
                prompt,
                hideout.Areas,
                area => area.Name,
                area => GetHideoutAliases(area.Name)).FirstOrDefault();
            if (areaTop is not null)
            {
                matches.Add(new DynamicEntityMatch
                {
                    Kind = HermesAssistantEntityKind.HideoutArea,
                    Name = areaTop.Name,
                    Score = areaTop.Score
                });
            }
        }

        if (itemResolution.Item is not null)
        {
            matches.Add(new DynamicEntityMatch
            {
                Kind = HermesAssistantEntityKind.Item,
                Name = itemResolution.Item.Name,
                Score = itemResolution.Score
            });
        }

        var ranked = matches
            .Where(match => HermesAssistantIntentEngine.IsConfident(match.Score))
            .OrderByDescending(match => match.Score)
            .ThenBy(match => DynamicEntityPriority(match.Kind))
            .ToList();
        if (ranked.Count > 1
            && ranked[1].Score >= ranked[0].Score - 5
            && (ranked[0].Kind != ranked[1].Kind
                || !ranked[0].Name.Equals(ranked[1].Name, StringComparison.OrdinalIgnoreCase)))
        {
            return BuildAmbiguityResponse(
                "subject",
                ranked.Select(match => $"{FriendlyEntityKind(match.Kind)}: {match.Name}"),
                "Assistant");
        }

        var best = ranked.FirstOrDefault();
        if (best is not null)
        {
            switch (best.Kind)
            {
                case HermesAssistantEntityKind.Quest when loadout.Found:
                    return BuildQuestPlanResponse(loadout, best.Name);
                case HermesAssistantEntityKind.Map when loadout.Found:
                {
                    var plan = loadout.RaidPlans.FirstOrDefault(candidate =>
                        candidate.MapName.Equals(best.Name, StringComparison.OrdinalIgnoreCase));
                    if (plan is not null)
                    {
                        return BuildMapPlanResponse(plan);
                    }
                    break;
                }
                case HermesAssistantEntityKind.Craft when crafts.Found:
                    return await BuildSpecificCraftResponseAsync(crafts, best.Name);
                case HermesAssistantEntityKind.CraftingStation when crafts.Found:
                    return BuildStationCraftResponse(crafts, best.Name, prompt);
                case HermesAssistantEntityKind.HideoutArea when hideout.Found:
                {
                    var area = hideout.Areas.FirstOrDefault(candidate =>
                        candidate.Name.Equals(best.Name, StringComparison.OrdinalIgnoreCase));
                    if (area is not null)
                    {
                        return await BuildHideoutAreaResponseAsync(hideout, area);
                    }
                    break;
                }
                case HermesAssistantEntityKind.Item when itemResolution.Item is not null:
                    return await BuildResolvedItemResponseAsync(prompt, itemResolution.Item, null, false);
            }
        }

        if (itemResolution.IsAmbiguous)
        {
            return BuildAmbiguityResponse(
                "item",
                itemResolution.Alternatives.Select(item => item.Name),
                "Item Search");
        }

        return new AssistantMessage(
            false,
            "I could not confidently identify the requested item, quest, map, craft, crafting station, or hideout area. Use a more exact player-facing name, or ask a general question about loadout readiness, the best raid, stash surplus, crafts, or hideout upgrades.",
            "LOCAL INTENT • ENTITY ENGINE",
            [
                new AssistantAction("Open Item Search", "Item Search"),
                new AssistantAction("Open Raid Planner", "Loadout/Raid Planner"),
                new AssistantAction("Open Crafts", "Crafts")
            ]);
    }

    private static async Task<ItemResolution> ResolveItemFromPromptAsync(string prompt)
    {
        var queries = BuildItemQueries(prompt)
            .Where(query => query.Length >= Plugin.Settings.GetMinimumSearchCharacters())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matches = new Dictionary<string, HermesItemSummary>(StringComparer.OrdinalIgnoreCase);
        var exactMatches = new Dictionary<string, HermesItemSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var response = await HermesApiClient.SearchAsync(query, 12);
            foreach (var item in response.Results)
            {
                matches[item.ItemKey] = item;
                if (item.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                    || item.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatches[item.ItemKey] = item;
                }
            }
        }

        if (exactMatches.Count == 1)
        {
            var exact = exactMatches.Values.First();
            return new ItemResolution
            {
                Item = exact,
                Alternatives = [exact],
                Score = 100
            };
        }

        if (exactMatches.Count > 1)
        {
            return new ItemResolution
            {
                Alternatives = exactMatches.Values
                    .Take(Plugin.Settings.GetMaximumAssistantAmbiguityChoices())
                    .ToList(),
                Score = 100,
                IsAmbiguous = true
            };
        }

        if (matches.Count == 0)
        {
            return new ItemResolution();
        }

        var candidates = HermesAssistantIntentEngine.RankCandidates(
            prompt,
            matches.Values,
            item => item.Name,
            item => string.IsNullOrWhiteSpace(item.ShortName) ? [] : [item.ShortName]);
        var top = candidates.FirstOrDefault();
        if (top is null)
        {
            return new ItemResolution();
        }

        var alternatives = candidates
            .Take(Plugin.Settings.GetMaximumAssistantAmbiguityChoices())
            .Select(candidate => candidate.Value)
            .ToList();
        var ambiguous = HermesAssistantIntentEngine.IsAmbiguous(candidates)
                        || !HermesAssistantIntentEngine.IsConfident(top.Score);
        return new ItemResolution
        {
            Item = ambiguous ? null : top.Value,
            Alternatives = alternatives,
            Score = top.Score,
            IsAmbiguous = ambiguous && alternatives.Count > 1
        };
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

        var subject = HermesAssistantIntentEngine.ExtractSubject(trimmed);
        if (!string.IsNullOrWhiteSpace(subject)
            && !subject.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            && !subject.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return subject;
        }
    }

    private void AddWelcomeMessage()
    {
        AddMessage(new AssistantMessage(
            false,
            "Assistant is online with follow-up context and cross-system reasoning. Ask a question, then continue with phrases such as \"why?\", \"what key?\", or \"where do I use it?\" Answers are built from current local HERMES data and never perform game actions.",
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

    private static AssistantMessage BuildAmbiguityResponse(
        string entityType,
        IEnumerable<string> names,
        string tabName)
    {
        var choices = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Plugin.Settings.GetMaximumAssistantAmbiguityChoices())
            .ToList();
        var builder = new StringBuilder();
        builder.AppendLine($"I found more than one possible {entityType} match. Use one of these exact player-facing names:");
        foreach (var choice in choices)
        {
            builder.AppendLine($"• {choice}");
        }

        return new AssistantMessage(
            false,
            builder.ToString().TrimEnd(),
            "ENTITY AMBIGUITY",
            [new AssistantAction($"Open {tabName}", tabName)]);
    }

    private static int DynamicEntityPriority(HermesAssistantEntityKind kind)
    {
        return kind switch
        {
            HermesAssistantEntityKind.Quest => 0,
            HermesAssistantEntityKind.Map => 1,
            HermesAssistantEntityKind.HideoutArea => 2,
            HermesAssistantEntityKind.CraftingStation => 3,
            HermesAssistantEntityKind.Craft => 4,
            HermesAssistantEntityKind.Item => 5,
            _ => 10
        };
    }

    private static string FriendlyEntityKind(HermesAssistantEntityKind kind)
    {
        return kind switch
        {
            HermesAssistantEntityKind.Map => "Map",
            HermesAssistantEntityKind.Quest => "Quest",
            HermesAssistantEntityKind.Craft => "Craft",
            HermesAssistantEntityKind.CraftingStation => "Crafting station",
            HermesAssistantEntityKind.HideoutArea => "Hideout area",
            HermesAssistantEntityKind.Item => "Item",
            _ => "Subject"
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
        return HermesAssistantIntentEngine.ReferencesSelectedItem(text);
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return HermesAssistantIntentEngine.ContainsAny(text, values);
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
