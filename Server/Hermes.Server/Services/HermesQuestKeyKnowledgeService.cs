using System.Reflection;
using System.Text;
using System.Text.Json;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesQuestKeyKnowledgeService
{
    private const string ResourceSuffix = ".quest_key_knowledge.json";
    private readonly HermesQuestKeyKnowledgeDocument _document;
    private readonly string? _loadError;
    private readonly Dictionary<string, List<HermesQuestKeyKnowledgeEntry>> _byQuestId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<HermesQuestKeyKnowledgeEntry>> _byQuestName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HermesQuestKeyKnowledgeEntry>> _byKeyTemplate =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<HermesQuestKeyKnowledgeEntry>> _byExactKeyName =
        new(StringComparer.Ordinal);
    private readonly List<(string Name, HermesQuestKeyKnowledgeEntry Entry)> _keyAliases = [];

    public HermesQuestKeyKnowledgeService()
    {
        try
        {
            _document = LoadDocument();
        }
        catch (Exception exception)
        {
            _document = new HermesQuestKeyKnowledgeDocument();
            _loadError = exception.Message;
        }

        BuildIndexes();
    }

    public IReadOnlyList<HermesQuestKeyKnowledgeEntry> FindForQuest(string questId, string questName)
    {
        var matches = new HashSet<HermesQuestKeyKnowledgeEntry>();
        AddMatches(matches, _byQuestId, (questId ?? string.Empty).Trim());
        AddMatches(matches, _byQuestName, Normalize(questName));
        return matches
            .OrderBy(entry => entry.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.KeyNames.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    public IReadOnlyList<HermesQuestKeyKnowledgeEntry> FindForKey(string templateId, string keyName)
    {
        var normalizedTemplateId = (templateId ?? string.Empty).Trim();
        var normalizedKeyName = NormalizeKeyName(keyName);
        if (normalizedTemplateId.Length == 0 && normalizedKeyName.Length == 0)
        {
            return [];
        }

        var matches = new HashSet<HermesQuestKeyKnowledgeEntry>();
        AddMatches(matches, _byKeyTemplate, normalizedTemplateId);
        AddMatches(matches, _byExactKeyName, normalizedKeyName);

        // Localized EFT names can add a leading article or location qualifier. Exact aliases use
        // the dictionary above; only the small pre-normalized alias list needs containment checks.
        if (normalizedKeyName.Length >= 12)
        {
            foreach (var (catalogName, entry) in _keyAliases)
            {
                if (KeyNamesMatch(catalogName, normalizedKeyName))
                {
                    matches.Add(entry);
                }
            }
        }

        return matches
            .OrderBy(entry => entry.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.QuestNames.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    public HermesQuestKeyKnowledgeStatusResponse GetStatus()
    {
        return new HermesQuestKeyKnowledgeStatusResponse(
            string.IsNullOrWhiteSpace(_loadError),
            _document.Entries.Count,
            _document.Entries.Sum(entry => entry.QuestNames.Count),
            _document.Source,
            _document.SourceUrl,
            _document.RetrievedOn,
            _loadError);
    }

    private void BuildIndexes()
    {
        foreach (var entry in _document.Entries)
        {
            foreach (var questId in entry.QuestIds)
            {
                AddIndex(_byQuestId, questId?.Trim() ?? string.Empty, entry);
            }

            foreach (var questName in entry.QuestNames)
            {
                AddIndex(_byQuestName, Normalize(questName), entry);
            }

            AddIndex(_byKeyTemplate, entry.KeyTemplateId?.Trim() ?? string.Empty, entry);
            foreach (var keyName in entry.KeyNames)
            {
                var normalized = NormalizeKeyName(keyName);
                if (normalized.Length == 0)
                {
                    continue;
                }

                AddIndex(_byExactKeyName, normalized, entry);
                _keyAliases.Add((normalized, entry));
            }
        }
    }

    private static void AddIndex(
        IDictionary<string, List<HermesQuestKeyKnowledgeEntry>> index,
        string key,
        HermesQuestKeyKnowledgeEntry entry)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out var entries))
        {
            entries = [];
            index[key] = entries;
        }

        if (!entries.Contains(entry))
        {
            entries.Add(entry);
        }
    }

    private static void AddMatches(
        ISet<HermesQuestKeyKnowledgeEntry> output,
        IReadOnlyDictionary<string, List<HermesQuestKeyKnowledgeEntry>> index,
        string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !index.TryGetValue(key, out var entries))
        {
            return;
        }

        foreach (var entry in entries)
        {
            output.Add(entry);
        }
    }

    private static HermesQuestKeyKnowledgeDocument LoadDocument()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded HERMES quest-key knowledge resource ending in '{ResourceSuffix}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded HERMES quest-key resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<HermesQuestKeyKnowledgeDocument>(json, new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true
               })
               ?? new HermesQuestKeyKnowledgeDocument();
    }

    private static bool KeyNamesMatch(string catalogName, string selectedName)
    {
        if (catalogName.Length == 0 || selectedName.Length == 0)
        {
            return false;
        }

        if (catalogName.Equals(selectedName, StringComparison.Ordinal))
        {
            return true;
        }

        // Localized EFT names can add a leading article or a short location qualifier.
        // Only permit containment for long, specific key names to avoid matching generic keys.
        return Math.Min(catalogName.Length, selectedName.Length) >= 12
               && (catalogName.Contains(selectedName, StringComparison.Ordinal)
                   || selectedName.Contains(catalogName, StringComparison.Ordinal));
    }

    private static string NormalizeKeyName(string? value)
    {
        var normalized = Normalize(value);
        return normalized
            .Replace(" keycard", " key", StringComparison.Ordinal)
            .Replace(" access card", " key", StringComparison.Ordinal)
            .Trim();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
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
}

public sealed class HermesQuestKeyKnowledgeDocument
{
    public string Source { get; set; } = "TarkovForge";
    public string SourceUrl { get; set; } = "https://tarkovforge.com/keys";
    public string RetrievedOn { get; set; } = string.Empty;
    public List<HermesQuestKeyKnowledgeEntry> Entries { get; set; } = [];
}

public sealed class HermesQuestKeyKnowledgeEntry
{
    public List<string> QuestIds { get; set; } = [];
    public List<string> QuestNames { get; set; } = [];
    public string MapName { get; set; } = string.Empty;
    public string? KeyTemplateId { get; set; }
    public List<string> KeyNames { get; set; } = [];
    public List<string> ObjectiveHints { get; set; } = [];
    public string Opens { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public List<string> Acquisition { get; set; } = [];
    public bool AcquireInRaid { get; set; }
}
