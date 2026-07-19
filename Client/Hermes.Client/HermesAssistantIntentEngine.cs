using System.Text;

namespace Hermes.Client;

internal enum HermesAssistantIntent
{
    Help,
    CrossSystem,
    Loadout,
    RaidPlanner,
    Stash,
    Crafts,
    Hideout,
    Item,
    Unknown
}

internal enum HermesAssistantEntityKind
{
    None,
    Map,
    Quest,
    Craft,
    CraftingStation,
    HideoutArea,
    Item
}

internal sealed class HermesAssistantInterpretation
{
    public HermesAssistantInterpretation(
        HermesAssistantIntent intent,
        bool referencesSelectedItem,
        string normalizedPrompt,
        string subjectText)
    {
        Intent = intent;
        ReferencesSelectedItem = referencesSelectedItem;
        NormalizedPrompt = normalizedPrompt;
        SubjectText = subjectText;
    }

    public HermesAssistantIntent Intent { get; }
    public bool ReferencesSelectedItem { get; }
    public string NormalizedPrompt { get; }
    public string SubjectText { get; }
}

internal sealed class HermesAssistantEntityCandidate<T>
{
    public HermesAssistantEntityCandidate(T value, string name, int score)
    {
        Value = value;
        Name = name;
        Score = score;
    }

    public T Value { get; }
    public string Name { get; }
    public int Score { get; }
}

internal static class HermesAssistantIntentEngine
{
    private static readonly string[] SubjectStopWords =
    [
        "a", "about", "after", "am", "an", "are", "at", "be", "before", "buy", "can", "cant", "cannot", "could",
        "craft", "crafting", "do", "does", "fix", "for", "from", "get", "go", "have", "hideout", "how", "i",
        "in", "is", "it", "make", "map", "me", "my", "need", "of", "on", "please",
        "price", "produce", "quest", "raid", "recipe", "requirements", "requirement", "sell",
        "should", "station", "status", "tell", "the", "this", "to", "upgrade", "value", "what",
        "where", "which", "why", "with", "worth", "d", "ll", "m", "re", "s", "t", "ve"
    ];

    private static readonly Dictionary<string, string[]> MapAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ground Zero"] = ["ground zero", "groundzero", "gz"],
        ["Customs"] = ["customs"],
        ["Factory"] = ["factory"],
        ["Woods"] = ["woods"],
        ["Shoreline"] = ["shoreline"],
        ["Reserve"] = ["reserve"],
        ["Interchange"] = ["interchange"],
        ["Lighthouse"] = ["lighthouse"],
        ["Streets of Tarkov"] = ["streets of tarkov", "streets", "sot"],
        ["The Lab"] = ["the lab", "labs", "laboratory"],
        ["Labyrinth"] = ["labyrinth"]
    };

    private static readonly string[] HideoutAreaAliases =
    [
        "air filtering unit", "bitcoin farm", "booze generator", "generator", "hall of fame",
        "heating", "illumination", "intelligence center", "intel center", "lavatory", "library",
        "medstation", "nutrition unit", "rest space", "scav case", "security", "shooting range",
        "solar power", "stash", "vents", "water collector", "weapon rack", "workbench"
    ];

    public static HermesAssistantInterpretation Interpret(string prompt, bool hasSelectedItem)
    {
        var normalized = Normalize(prompt);
        var referencesSelectedItem = hasSelectedItem && ReferencesSelectedItem(normalized);
        var subject = ExtractSubject(prompt);

        if (ContainsAny(normalized, "what can you do", "help", "commands", "capabilities"))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Help, referencesSelectedItem, normalized, subject);
        }

        if (ContainsAny(
                normalized,
                "what should i do next",
                "what do i do next",
                "what should i do first",
                "what is most important",
                "what's most important",
                "prioritize my next steps",
                "prioritize my tasks",
                "why that priority",
                "why this priority",
                "next best step",
                "best use of my time",
                "should i craft or raid",
                "craft or raid",
                "how should i prepare",
                "prepare me for",
                "what should i fix first",
                "what do i need before i go"))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.CrossSystem, referencesSelectedItem, normalized, subject);
        }

        if (referencesSelectedItem)
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Item, true, normalized, subject);
        }

        if (ContainsAny(
                normalized,
                "best raid", "which raid", "what raid",
                "best map", "which map", "what map",
                "map should i", "map do i", "where should i go", "where do i go",
                "where should i raid", "where do i raid", "what should i run",
                "raid planner", "active quests", "quests can i", "quest on", "quests on")
            || ContainsMapAlias(normalized))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.RaidPlanner, false, normalized, subject);
        }

        if (ContainsAny(normalized, "safe to sell", "safely sell", "stash cleanup", "clean my stash", "cleanup", "duplicate", "stash value", "my stash", "most valuable", "free stash space")
            || (normalized.Contains("stash", StringComparison.Ordinal)
                && !ContainsAny(normalized, "upgrade stash", "stash level", "hideout stash")))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Stash, false, normalized, subject);
        }

        if (ContainsAny(normalized, "craft", "recipe", "profitable", "profit", "overnight", "make", "produce"))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Crafts, false, normalized, subject);
        }

        if (ContainsAny(normalized, "hideout", "upgrade", "area") || ContainsHideoutAreaAlias(normalized))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Hideout, false, normalized, subject);
        }

        if (ContainsAny(normalized, "ready", "loadout", "ammo", "round", "magazine", "medical", "bleed", "fracture", "pain", "armor", "weapon", "insured", "insurance", "risk", "hydration", "energy", "what should i bring", "bring with me", "before i go"))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Loadout, false, normalized, subject);
        }

        var asksWhatIsNeededForSubject = ContainsAny(
            normalized,
            "what do i need for",
            "requirements for",
            "what is needed for");
        if (ContainsAny(
                normalized,
                "worth", "price", "flea", "trader", "where should i sell", "where can i buy",
                "should i sell", "should i buy", "tell me about", "item")
            || (normalized.Contains("do i need", StringComparison.Ordinal) && !asksWhatIsNeededForSubject))
        {
            return new HermesAssistantInterpretation(HermesAssistantIntent.Item, false, normalized, subject);
        }

        return new HermesAssistantInterpretation(HermesAssistantIntent.Unknown, false, normalized, subject);
    }

    public static IReadOnlyList<HermesAssistantEntityCandidate<T>> RankCandidates<T>(
        string prompt,
        IEnumerable<T> values,
        Func<T, string> nameSelector,
        Func<T, IEnumerable<string>>? aliasSelector = null)
    {
        var output = new List<HermesAssistantEntityCandidate<T>>();
        foreach (var value in values)
        {
            var name = nameSelector(value)?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var aliases = new List<string> { name };
            if (aliasSelector is not null)
            {
                aliases.AddRange(aliasSelector(value).Where(alias => !string.IsNullOrWhiteSpace(alias)));
            }

            var score = aliases.Max(alias => Score(prompt, alias));
            if (score > 0)
            {
                output.Add(new HermesAssistantEntityCandidate<T>(value, name, score));
            }
        }

        return output
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Name.Length)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsAmbiguous<T>(IReadOnlyList<HermesAssistantEntityCandidate<T>> candidates)
    {
        return candidates.Count > 1
               && (candidates[0].Score == 100
                   ? candidates[1].Score == 100
                   : candidates[1].Score >= candidates[0].Score - 6);
    }

    public static bool IsConfident(int score)
    {
        return score >= Plugin.Settings.GetAssistantEntityConfidencePercent();
    }

    public static string? ResolveMapAlias(string prompt, IEnumerable<string> availableMapNames)
    {
        var normalized = $" {Normalize(prompt)} ";
        var available = availableMapNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pair in MapAliases)
        {
            if (!pair.Value.Any(alias => normalized.Contains($" {Normalize(alias)} ", StringComparison.Ordinal)))
            {
                continue;
            }

            var exact = available.FirstOrDefault(name =>
                name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var aliasMatch = available.FirstOrDefault(name =>
                pair.Value.Any(alias => Normalize(name).Equals(Normalize(alias), StringComparison.Ordinal)));
            if (aliasMatch is not null)
            {
                return aliasMatch;
            }
        }

        return null;
    }

    public static string ExtractSubject(string prompt)
    {
        var tokens = Tokenize(prompt)
            .Where(token => !SubjectStopWords.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return string.Join(" ", tokens);
    }

    public static bool ReferencesSelectedItem(string text)
    {
        var normalized = $" {Normalize(text)} ";
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

    public static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static int Score(string prompt, string candidate)
    {
        var normalizedPrompt = Normalize(prompt);
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0 || normalizedPrompt.Length == 0)
        {
            return 0;
        }

        if (normalizedPrompt.Equals(normalizedCandidate, StringComparison.Ordinal))
        {
            return 100;
        }

        var paddedPrompt = $" {normalizedPrompt} ";
        var paddedCandidate = $" {normalizedCandidate} ";
        if (paddedPrompt.Contains(paddedCandidate, StringComparison.Ordinal))
        {
            return 98;
        }

        var subject = ExtractSubject(prompt);
        if (subject.Equals(normalizedCandidate, StringComparison.Ordinal))
        {
            return 100;
        }

        if (subject.Length >= 3 && $" {subject} ".Contains(paddedCandidate, StringComparison.Ordinal))
        {
            return 96;
        }

        if (subject.Length >= 3 && paddedCandidate.Contains($" {subject} ", StringComparison.Ordinal))
        {
            return 92;
        }

        var promptTokens = Tokenize(subject.Length > 0 ? subject : normalizedPrompt).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateTokens = Tokenize(normalizedCandidate).ToList();
        if (candidateTokens.Count == 0)
        {
            return 0;
        }

        var matchingTokens = candidateTokens.Count(promptTokens.Contains);
        if (matchingTokens == candidateTokens.Count)
        {
            return 90;
        }

        var coverage = matchingTokens / (double)candidateTokens.Count;
        var score = Convert.ToInt32(Math.Round(coverage * 78d));

        if (Plugin.Settings.EnableAssistantFuzzyEntityMatching.Value)
        {
            var comparison = subject.Length > 0 ? subject : normalizedPrompt;
            if (comparison.Length >= 3 && normalizedCandidate.Length >= 3)
            {
                var distance = LevenshteinDistance(comparison, normalizedCandidate);
                var maximum = Math.Max(comparison.Length, normalizedCandidate.Length);
                var similarity = maximum == 0 ? 0d : 1d - distance / (double)maximum;
                score = Math.Max(score, Convert.ToInt32(Math.Round(similarity * 82d)));
            }
        }

        return Math.Clamp(score, 0, 100);
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 0)
            .ToList();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool ContainsMapAlias(string normalizedPrompt)
    {
        var padded = $" {normalizedPrompt} ";
        return MapAliases.Values
            .SelectMany(aliases => aliases)
            .Any(alias => padded.Contains($" {Normalize(alias)} ", StringComparison.Ordinal));
    }

    private static bool ContainsHideoutAreaAlias(string normalizedPrompt)
    {
        var padded = $" {normalizedPrompt} ";
        return HideoutAreaAliases.Any(alias =>
            padded.Contains($" {Normalize(alias)} ", StringComparison.Ordinal));
    }
}
