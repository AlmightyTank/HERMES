using Hermes.Client.Models;

namespace Hermes.Client;

internal sealed class HermesAssistantConversationSubject
{
    public HermesAssistantConversationSubject(
        HermesAssistantEntityKind kind,
        string name,
        HermesItemSummary? item = null,
        string? selectedInstanceKey = null)
    {
        Kind = kind;
        Name = name.Trim();
        Item = item;
        SelectedInstanceKey = selectedInstanceKey;
    }

    public HermesAssistantEntityKind Kind { get; }
    public string Name { get; }
    public HermesItemSummary? Item { get; }
    public string? SelectedInstanceKey { get; }

    public string DisplayKind => Kind switch
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

internal sealed class HermesAssistantContextResolution
{
    public HermesAssistantContextResolution(
        string prompt,
        bool usedContext,
        HermesAssistantConversationSubject? subject,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey,
        string? note = null)
    {
        Prompt = prompt;
        UsedContext = usedContext;
        Subject = subject;
        SelectedItem = selectedItem;
        SelectedInstanceKey = selectedInstanceKey;
        Note = note;
    }

    public string Prompt { get; }
    public bool UsedContext { get; }
    public HermesAssistantConversationSubject? Subject { get; }
    public HermesItemSummary? SelectedItem { get; }
    public string? SelectedInstanceKey { get; }
    public string? Note { get; }
}

internal sealed class HermesAssistantConversationContext
{
    private sealed class PendingChoice
    {
        public PendingChoice(HermesAssistantEntityKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public HermesAssistantEntityKind Kind { get; }
        public string Name { get; }
    }

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 0,
        ["1"] = 0,
        ["second"] = 1,
        ["2"] = 1,
        ["third"] = 2,
        ["3"] = 2,
        ["fourth"] = 3,
        ["4"] = 3,
        ["fifth"] = 4,
        ["5"] = 4,
        ["sixth"] = 5,
        ["6"] = 5,
        ["seventh"] = 6,
        ["7"] = 6,
        ["eighth"] = 7,
        ["8"] = 7,
        ["ninth"] = 8,
        ["9"] = 8,
        ["tenth"] = 9,
        ["10"] = 9
    };

    private readonly List<HermesAssistantConversationSubject> _subjects = [];
    private readonly List<PendingChoice> _pendingChoices = [];
    private HermesItemSummary? _selectedItem;
    private string? _selectedInstanceKey;

    public HermesAssistantConversationSubject? Current => _subjects.FirstOrDefault();
    public IReadOnlyList<HermesAssistantConversationSubject> Recent => _subjects;
    public bool HasPendingChoices => _pendingChoices.Count > 0;

    public void Reset()
    {
        _subjects.Clear();
        _pendingChoices.Clear();
        _selectedItem = null;
        _selectedInstanceKey = null;
    }

    public void ForgetCurrent()
    {
        if (_subjects.Count > 0)
        {
            _subjects.RemoveAt(0);
        }

        _pendingChoices.Clear();
    }

    public void UpdateSelectedItem(HermesItemSummary? item, string? selectedInstanceKey)
    {
        _selectedItem = item;
        _selectedInstanceKey = selectedInstanceKey;
    }

    public HermesAssistantContextResolution Resolve(
        string prompt,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        UpdateSelectedItem(selectedItem, selectedInstanceKey);
        if (!Plugin.Settings.EnableAssistantFollowUpContext.Value)
        {
            return new HermesAssistantContextResolution(prompt, false, null, selectedItem, selectedInstanceKey);
        }

        var pending = ResolvePendingChoice(prompt);
        if (pending is not null)
        {
            Remember(pending);
            return CreateResolution(prompt, pending, "Resolved from the previous ambiguity list.");
        }

        var normalized = HermesAssistantIntentEngine.Normalize(prompt);
        var subject = SelectSubject(normalized, prompt);
        if (subject is null || !ShouldUseContext(normalized, prompt))
        {
            return new HermesAssistantContextResolution(prompt, false, null, selectedItem, selectedInstanceKey);
        }

        return CreateResolution(prompt, subject, $"Follow-up resolved to {subject.DisplayKind.ToLowerInvariant()} {subject.Name}.");
    }

    public bool TryHandleCommand(string prompt, out string response)
    {
        response = string.Empty;
        if (!Plugin.Settings.EnableAssistantFollowUpContext.Value)
        {
            return false;
        }

        var normalized = HermesAssistantIntentEngine.Normalize(prompt);
        if (HermesAssistantIntentEngine.ContainsAny(
                normalized,
                "what are we talking about",
                "what is the current context",
                "what s the current context",
                "what was i asking about",
                "remind me what we were discussing",
                "show conversation context"))
        {
            response = Describe();
            return true;
        }

        if (HermesAssistantIntentEngine.ContainsAny(normalized, "clear context", "forget everything", "clear all context"))
        {
            var hadContext = Current is not null || _pendingChoices.Count > 0;
            Reset();
            response = hadContext
                ? "Cleared all remembered Assistant conversation subjects."
                : "There was no remembered conversation context to clear.";
            return true;
        }

        if (HermesAssistantIntentEngine.ContainsAny(
                normalized,
                "forget that",
                "forget this",
                "forget the subject",
                "clear subject"))
        {
            var previous = Current;
            ForgetCurrent();
            response = previous is null
                ? "There was no remembered conversation subject to forget."
                : $"Forgot {previous.DisplayKind.ToLowerInvariant()} {previous.Name}.";
            return true;
        }

        return false;
    }

    public string Describe()
    {
        if (Current is null)
        {
            return _selectedItem is null
                ? "No conversation subject is currently remembered. Ask about an item, quest, map, craft, station, or hideout area first."
                : $"No conversation subject is currently remembered. The selected-item context is {_selectedItem.Name}.";
        }

        var lines = new List<string>
        {
            $"Current subject: {Current.DisplayKind} — {Current.Name}"
        };
        if (_subjects.Count > 1)
        {
            lines.Add("Recent subjects:");
            foreach (var subject in _subjects.Skip(1).Take(Plugin.Settings.GetMaximumAssistantContextSubjects() - 1))
            {
                lines.Add($"• {subject.DisplayKind}: {subject.Name}");
            }
        }

        if (_selectedItem is not null
            && (Current.Kind != HermesAssistantEntityKind.Item
                || !Current.Name.Equals(_selectedItem.Name, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add($"Selected item: {_selectedItem.Name}");
        }

        if (_pendingChoices.Count > 0)
        {
            lines.Add($"Pending ambiguity choices: {_pendingChoices.Count:N0}. You can answer with 'the first one', 'the second one', or an exact name.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void RememberResponse(
        string source,
        string text,
        HermesItemSummary? selectedItem,
        string? selectedInstanceKey)
    {
        UpdateSelectedItem(selectedItem, selectedInstanceKey);
        if (!Plugin.Settings.EnableAssistantFollowUpContext.Value
            || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (source.Equals("ENTITY AMBIGUITY", StringComparison.OrdinalIgnoreCase))
        {
            CapturePendingChoices(text);
            return;
        }

        _pendingChoices.Clear();
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        HermesAssistantConversationSubject? subject = null;

        if (source.StartsWith("QUEST", StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("QUEST RESOLUTION", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.Quest, firstLine);
        }
        else if (source.Equals("RAID PLANNER • BEST MAP", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.Map, AfterPrefix(firstLine, "Recommended map:"));
        }
        else if (source.StartsWith("RAID PLANNER • ", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.Map, RemoveSuffix(firstLine, " raid plan"));
        }
        else if (source.StartsWith("CROSS-SYSTEM • ", StringComparison.OrdinalIgnoreCase)
                 && !source.EndsWith("CRAFT OR RAID", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.Map, AfterPrefix(firstLine, "Preparation plan:"));
        }
        else if (source.Equals("CRAFT • RECIPE", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.Craft, AfterMultiplication(firstLine));
        }
        else if (source.StartsWith("CRAFT STATION • ", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.CraftingStation, ExtractStationName(firstLine, source));
        }
        else if (source.Equals("HIDEOUT • AREA", StringComparison.OrdinalIgnoreCase))
        {
            subject = Create(HermesAssistantEntityKind.HideoutArea, firstLine);
        }
        else if (source.StartsWith("ITEM •", StringComparison.OrdinalIgnoreCase))
        {
            var exactSelected = selectedItem is not null
                                && selectedItem.Name.Equals(firstLine, StringComparison.OrdinalIgnoreCase)
                ? selectedItem
                : null;
            subject = Create(HermesAssistantEntityKind.Item, firstLine, exactSelected, exactSelected is null ? null : selectedInstanceKey);
        }

        if (subject is not null)
        {
            Remember(subject);
        }
    }

    private HermesAssistantContextResolution CreateResolution(
        string prompt,
        HermesAssistantConversationSubject subject,
        string note)
    {
        var expanded = ExpandPrompt(prompt, subject);
        var effectiveItem = subject.Kind == HermesAssistantEntityKind.Item && subject.Item is not null
            ? subject.Item
            : _selectedItem;
        var effectiveInstance = subject.Kind == HermesAssistantEntityKind.Item && subject.Item is not null
            ? subject.SelectedInstanceKey
            : _selectedInstanceKey;
        return new HermesAssistantContextResolution(
            expanded,
            true,
            subject,
            effectiveItem,
            effectiveInstance,
            note);
    }

    private HermesAssistantConversationSubject? SelectSubject(string normalized, string originalPrompt)
    {
        if (ReferencesSelectedItem(normalized) && _selectedItem is not null)
        {
            return new HermesAssistantConversationSubject(
                HermesAssistantEntityKind.Item,
                _selectedItem.Name,
                _selectedItem,
                _selectedInstanceKey);
        }

        var requestedKind = GetRequestedKind(normalized);
        if (requestedKind != HermesAssistantEntityKind.None)
        {
            var typed = _subjects.FirstOrDefault(subject => subject.Kind == requestedKind);
            if (typed is not null)
            {
                return typed;
            }

            if (requestedKind == HermesAssistantEntityKind.Item && _selectedItem is not null)
            {
                return new HermesAssistantConversationSubject(
                    HermesAssistantEntityKind.Item,
                    _selectedItem.Name,
                    _selectedItem,
                    _selectedInstanceKey);
            }
        }

        var subjectText = HermesAssistantIntentEngine.ExtractSubject(originalPrompt);
        if (subjectText.Length > 0
            && !IsGenericSubject(subjectText)
            && !ContainsFollowUpPronoun(normalized))
        {
            return null;
        }

        return Current;
    }

    private static HermesAssistantEntityKind GetRequestedKind(string normalized)
    {
        if (HermesAssistantIntentEngine.ContainsAny(normalized, "this quest", "that quest", "the quest", "quest objective", "what key", "which key"))
        {
            return HermesAssistantEntityKind.Quest;
        }

        if (HermesAssistantIntentEngine.ContainsAny(normalized, "this map", "that map", "the map", "on that map", "there"))
        {
            return HermesAssistantEntityKind.Map;
        }

        if (HermesAssistantIntentEngine.ContainsAny(normalized, "this recipe", "that recipe", "this craft", "that craft", "craft it", "make it"))
        {
            return HermesAssistantEntityKind.Craft;
        }

        if (HermesAssistantIntentEngine.ContainsAny(normalized, "this station", "that station", "the station"))
        {
            return HermesAssistantEntityKind.CraftingStation;
        }

        if (HermesAssistantIntentEngine.ContainsAny(normalized, "this area", "that area", "hideout area", "upgrade it"))
        {
            return HermesAssistantEntityKind.HideoutArea;
        }

        if (ReferencesSelectedItem(normalized)
            || HermesAssistantIntentEngine.ContainsAny(normalized, "sell it", "buy it", "is it worth", "how much is it"))
        {
            return HermesAssistantEntityKind.Item;
        }

        return HermesAssistantEntityKind.None;
    }

    private static bool ShouldUseContext(string normalized, string prompt)
    {
        if (ContainsFollowUpPronoun(normalized))
        {
            return true;
        }

        if (HermesAssistantIntentEngine.ContainsAny(
                normalized,
                "why",
                "why not",
                "what key",
                "which key",
                "where",
                "where is it",
                "where can i get",
                "what about it",
                "how about it",
                "do i have it",
                "what do i need",
                "what materials",
                "what is missing",
                "what am i missing",
                "materials am i missing",
                "is it ready",
                "is it profitable",
                "can i craft it",
                "should i sell it",
                "what should i bring",
                "what should i fix",
                "what should i do first",
                "and then",
                "what next"))
        {
            return true;
        }

        var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return tokenCount <= 4 && IsGenericSubject(HermesAssistantIntentEngine.ExtractSubject(prompt));
    }

    private static bool ContainsFollowUpPronoun(string normalized)
    {
        var padded = $" {normalized} ";
        return new[]
        {
            " it ", " that ", " this one ", " that one ", " same one ", " same item ",
            " same quest ", " same map ", " same craft ", " there ", " them ", " those "
        }.Any(padded.Contains);
    }

    private static bool ReferencesSelectedItem(string normalized)
    {
        return HermesAssistantIntentEngine.ReferencesSelectedItem(normalized);
    }

    private static bool IsGenericSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return true;
        }

        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "key", "keys", "use", "missing", "next", "ready", "profitable", "profit", "worth",
            "value", "price", "sell", "buy", "owned", "have", "location", "objective", "objectives",
            "requirement", "requirements", "first one", "second one", "third one", "one",
            "before", "go", "before go", "bring", "fix", "first", "materials", "parts", "get parts",
            "priority"
        };
        return generic.Contains(subject.Trim());
    }

    private HermesAssistantConversationSubject? ResolvePendingChoice(string prompt)
    {
        if (_pendingChoices.Count == 0)
        {
            return null;
        }

        var normalized = HermesAssistantIntentEngine.Normalize(prompt);
        foreach (var pair in Ordinals)
        {
            var token = pair.Key;
            if (!($" {normalized} ").Contains($" {token} ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value >= 0 && pair.Value < _pendingChoices.Count)
            {
                var choice = _pendingChoices[pair.Value];
                _pendingChoices.Clear();
                return new HermesAssistantConversationSubject(choice.Kind, choice.Name);
            }
        }

        var exact = _pendingChoices.FirstOrDefault(choice =>
            normalized.Contains(HermesAssistantIntentEngine.Normalize(choice.Name), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            _pendingChoices.Clear();
            return new HermesAssistantConversationSubject(exact.Kind, exact.Name);
        }

        return null;
    }

    private void CapturePendingChoices(string text)
    {
        _pendingChoices.Clear();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var kind = ParseAmbiguityKind(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            var name = line.TrimStart('•', '-', '*', ' ').Trim();
            if (name.Length > 0)
            {
                _pendingChoices.Add(new PendingChoice(kind, name));
            }
        }
    }

    private static HermesAssistantEntityKind ParseAmbiguityKind(string line)
    {
        var normalized = HermesAssistantIntentEngine.Normalize(line);
        if (normalized.Contains("quest", StringComparison.Ordinal)) return HermesAssistantEntityKind.Quest;
        if (normalized.Contains("map", StringComparison.Ordinal)) return HermesAssistantEntityKind.Map;
        if (normalized.Contains("crafting station", StringComparison.Ordinal)) return HermesAssistantEntityKind.CraftingStation;
        if (normalized.Contains("craft", StringComparison.Ordinal) || normalized.Contains("recipe", StringComparison.Ordinal)) return HermesAssistantEntityKind.Craft;
        if (normalized.Contains("hideout area", StringComparison.Ordinal)) return HermesAssistantEntityKind.HideoutArea;
        if (normalized.Contains("item", StringComparison.Ordinal)) return HermesAssistantEntityKind.Item;
        return HermesAssistantEntityKind.None;
    }

    private void Remember(HermesAssistantConversationSubject subject)
    {
        if (subject.Kind == HermesAssistantEntityKind.None || string.IsNullOrWhiteSpace(subject.Name))
        {
            return;
        }

        _subjects.RemoveAll(existing =>
            existing.Kind == subject.Kind
            && existing.Name.Equals(subject.Name, StringComparison.OrdinalIgnoreCase));
        _subjects.Insert(0, subject);
        var maximum = Plugin.Settings.GetMaximumAssistantContextSubjects();
        while (_subjects.Count > maximum)
        {
            _subjects.RemoveAt(_subjects.Count - 1);
        }
    }

    private static string ExpandPrompt(string prompt, HermesAssistantConversationSubject subject)
    {
        var trimmed = prompt.Trim();
        var suffix = subject.Kind switch
        {
            HermesAssistantEntityKind.Map => $" for map {subject.Name}",
            HermesAssistantEntityKind.Quest => $" for quest {subject.Name}",
            HermesAssistantEntityKind.Craft => $" for craft {subject.Name}",
            HermesAssistantEntityKind.CraftingStation => $" at crafting station {subject.Name}",
            HermesAssistantEntityKind.HideoutArea => $" for hideout area {subject.Name}",
            HermesAssistantEntityKind.Item => $" for item {subject.Name}",
            _ => $" about {subject.Name}"
        };
        return trimmed.TrimEnd('?', '.', '!') + suffix + "?";
    }

    private static HermesAssistantConversationSubject? Create(
        HermesAssistantEntityKind kind,
        string? name,
        HermesItemSummary? item = null,
        string? selectedInstanceKey = null)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new HermesAssistantConversationSubject(kind, name, item, selectedInstanceKey);
    }

    private static string AfterPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : value.Trim();
    }

    private static string RemoveSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length].Trim()
            : value.Trim();
    }

    private static string AfterMultiplication(string value)
    {
        var index = value.IndexOf('×');
        return index >= 0 && index + 1 < value.Length
            ? value[(index + 1)..].Trim()
            : value.Trim();
    }

    private static string ExtractStationName(string firstLine, string source)
    {
        var atIndex = firstLine.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (atIndex >= 0)
        {
            var tail = firstLine[(atIndex + 4)..];
            var colon = tail.IndexOf(':');
            return (colon >= 0 ? tail[..colon] : tail).Trim();
        }

        return source["CRAFT STATION • ".Length..].Trim();
    }
}
