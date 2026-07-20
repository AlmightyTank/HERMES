using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

public sealed partial class HermesHideoutService
{
    private static string NormalizeKnowledgeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    private static IReadOnlyList<string> ReadMongoIdList(JsonNode? node)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMongoIds(node, output);
        return output.ToList();
    }

    private static void CollectMongoIds(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && MongoId.IsValidMongoId(text))
            {
                output.Add(text);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectMongoIds(child, output);
            }
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (MongoId.IsValidMongoId(pair.Key))
                {
                    output.Add(pair.Key);
                }
                CollectMongoIds(pair.Value, output);
            }
        }
    }

    private static int ParseQuestStatusCode(string status)
    {
        if (int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "availableforstart" => 1,
            "started" => 2,
            "availableforfinish" => 3,
            "success" or "completed" or "complete" => 4,
            "fail" => 5,
            "failrestartable" => 6,
            "markedasfailed" => 7,
            "expired" => 8,
            "availableafter" => 9,
            _ => 0
        };
    }

    private static string QuestStatusDisplay(int statusCode)
    {
        return statusCode switch
        {
            1 => "Available to start",
            2 => "Active",
            3 => "Ready to finish",
            4 => "Completed",
            5 => "Failed",
            6 => "Failed — restartable",
            7 => "Marked as failed",
            8 => "Expired",
            9 => "Available later",
            _ => "Future / locked"
        };
    }

    private static int QuestStatusRank(string status)
    {
        return status switch
        {
            "Active" => 0,
            "Ready to finish" => 1,
            "Available to start" => 2,
            "Available later" => 3,
            "Future / locked" => 4,
            "Completed" => 5,
            _ => 6
        };
    }

    private static string FriendlyQuestConditionType(string conditionType)
    {
        return conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase)
            ? "Hand over"
            : conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase)
                ? "Find"
                : FormatDisplayName(conditionType);
    }

    private static IReadOnlyList<RequirementDefinition> ParseRequirements(IEnumerable<JsonNode?> nodes)
    {
        var output = new List<RequirementDefinition>();
        foreach (var node in nodes)
        {
            if (node is not JsonObject requirement)
            {
                continue;
            }

            output.Add(new RequirementDefinition(
                ReadString(requirement, "type", "Type") ?? "Requirement",
                ReadString(requirement, "templateId", "TemplateId"),
                ReadDouble(requirement, 0d, "count", "Count", "resource", "Resource"),
                ReadNullableInt(requirement, "areaType", "AreaType"),
                ReadInt(requirement, 0, "requiredLevel", "RequiredLevel"),
                ReadString(requirement, "traderId", "TraderId"),
                ReadInt(requirement, 0, "loyaltyLevel", "LoyaltyLevel"),
                ReadString(requirement, "skillName", "SkillName"),
                ReadInt(requirement, 0, "skillLevel", "SkillLevel"),
                ReadString(requirement, "questId", "QuestId"),
                ReadBool(requirement, false, "isSpawnedInSession", "IsSpawnedInSession")));
        }

        return output;
    }

    private static bool IsItemRequirement(RequirementDefinition requirement)
    {
        return !string.IsNullOrWhiteSpace(requirement.TemplateId)
               && (requirement.Type.Contains("Item", StringComparison.OrdinalIgnoreCase)
                   || requirement.Type.Contains("Tool", StringComparison.OrdinalIgnoreCase)
                   || requirement.Type.Contains("Resource", StringComparison.OrdinalIgnoreCase)
                   || (!requirement.AreaType.HasValue
                       && string.IsNullOrWhiteSpace(requirement.TraderId)
                       && string.IsNullOrWhiteSpace(requirement.SkillName)
                       && string.IsNullOrWhiteSpace(requirement.QuestId)));
    }

    private static bool IsCompletedQuestStatus(string status)
    {
        return status.Equals("4", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('_', ' ').Replace('-', ' ');
        normalized = Regex.Replace(normalized, "(?<=[a-z0-9])(?=[A-Z])", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        return normalized;
    }

    private static string CreateOpaqueKey(string category, string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"HERMES:{category}:{source}"));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var pair in obj)
        {
            if (names.Any(name => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonNode?> GetArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.ToList();
        }

        if (node is JsonObject obj)
        {
            return obj.Select(pair => pair.Value).ToList();
        }

        return [];
    }

    private static string? ReadString(JsonNode? node, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static int ReadInt(JsonNode? node, int fallback, params string[] names)
    {
        var value = ReadDouble(node, fallback, names);
        return Convert.ToInt32(Math.Round(value));
    }

    private static int? ReadNullableInt(JsonNode? node, params string[] names)
    {
        var value = ReadNullableDouble(node, names);
        return value.HasValue ? Convert.ToInt32(Math.Round(value.Value)) : null;
    }

    private static long? ReadNullableLong(JsonNode? node, params string[] names)
    {
        var value = ReadNullableDouble(node, names);
        return value.HasValue ? Convert.ToInt64(Math.Round(value.Value)) : null;
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        return ReadNullableDouble(node, names) ?? fallback;
    }

    private static double? ReadNullableDouble(JsonNode? node, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        if (names.Length > 0)
        {
            node = GetProperty(node, names);
        }

        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue != 0;
        }

        if (value.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static void CollectStrings(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                CollectStrings(child, output);
            }

            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (MongoId.IsValidMongoId(pair.Key))
                {
                    output.Add(pair.Key);
                }

                CollectStrings(pair.Value, output);
            }
        }
    }
}
