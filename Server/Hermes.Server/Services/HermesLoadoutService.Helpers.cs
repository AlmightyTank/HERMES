using System.Globalization;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

public sealed partial class HermesLoadoutService
{
    private static Dictionary<string, ActiveQuestState> GetActiveQuestStates(JsonObject profileRoot)
    {
        var activeQuests = new Dictionary<string, ActiveQuestState>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in GetArray(GetProperty(profileRoot, "Quests", "quests")))
        {
            var questId = ReadString(node, "qid", "QId", "questId", "QuestId", "_id", "id");
            if (string.IsNullOrWhiteSpace(questId))
            {
                continue;
            }

            var status = ParseQuestStatus(ReadString(node, "status", "Status") ?? string.Empty);
            if (status is not (2 or 3))
            {
                continue;
            }

            var completedConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectStrings(GetProperty(node, "completedConditions", "CompletedConditions"), completedConditions);
            activeQuests[questId] = new ActiveQuestState(status, completedConditions);
        }

        return activeQuests;
    }

    private static int CalculateReadinessScore(IEnumerable<HermesLoadoutWarning> warnings)
    {
        var score = 100;
        foreach (var warning in warnings)
        {
            score -= warning.Severity.ToLowerInvariant() switch
            {
                "critical" => 15,
                "warning" => 5,
                _ => 0
            };
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int SeverityRank(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" => 0,
            "warning" => 1,
            _ => 2
        };
    }

    private static int SlotRank(string? slotId)
    {
        var index = Array.FindIndex(EquipmentSlotOrder, slot =>
            slot.Equals(slotId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 999;
    }

    private static string FriendlySlotName(string? slotId)
    {
        return slotId switch
        {
            "FirstPrimaryWeapon" => "Primary weapon",
            "SecondPrimaryWeapon" => "Secondary weapon",
            "ArmorVest" => "Body armor",
            "TacticalVest" => "Tactical rig",
            "Headwear" => "Headwear",
            "Earpiece" => "Headset",
            "FaceCover" => "Face cover",
            "SecuredContainer" => "Secure container",
            null or "" => "Equipment",
            _ => SplitPascalCase(slotId)
        };
    }

    private static string DetermineSlotStatus(
        string? slotId,
        int conditionPercent,
        HermesLoadoutAnalysisSettings settings)
    {
        var threshold = WeaponSlots.Contains(slotId ?? string.Empty)
            ? settings.MinimumWeaponDurabilityPercent
            : ArmorSlots.Contains(slotId ?? string.Empty)
                ? settings.MinimumArmorDurabilityPercent
                : 0;
        return threshold > 0 && conditionPercent < threshold
            ? "Low condition"
            : "Equipped";
    }

    private static bool IsMagazineSlot(string? slotId)
    {
        return (slotId ?? string.Empty).Contains("magazine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoadedAmmoSlot(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("cartridge", StringComparison.Ordinal)
               || normalized.Contains("chamber", StringComparison.Ordinal)
               || normalized.Contains("patron", StringComparison.Ordinal)
               || normalized.Equals("ammo", StringComparison.Ordinal);
    }

    private static bool IsArmorSlot(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("plate", StringComparison.Ordinal)
               || normalized.Contains("soft_armor", StringComparison.Ordinal)
               || normalized.Contains("softarmor", StringComparison.Ordinal)
               || normalized.Contains("armor_front", StringComparison.Ordinal)
               || normalized.Contains("armor_back", StringComparison.Ordinal)
               || normalized.Contains("armor_side", StringComparison.Ordinal);
    }

    private static bool IsPlantOrPlacementCondition(string conditionType)
    {
        return conditionType.Contains("Plant", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("PlaceBeacon", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("LeaveItemAtLocation", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("PlaceItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFoundInRaid(InventoryNode item)
    {
        return ReadBool(item.Upd, false, "SpawnedInSession", "spawnedInSession");
    }

    private static string FriendlyMapName(string? location)
    {
        var normalized = (location ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "any" or "all" => "Any map",
            "bigmap" or "customs" => "Customs",
            "factory4_day" or "factory4_night" or "factory" => "Factory",
            "woods" => "Woods",
            "shoreline" => "Shoreline",
            "interchange" => "Interchange",
            "rezervbase" or "reserve" => "Reserve",
            "laboratory" or "lab" => "The Lab",
            "lighthouse" => "Lighthouse",
            "tarkovstreets" or "streets" => "Streets of Tarkov",
            "sandbox" or "sandbox_high" or "groundzero" or "653e6760052c01c1c805532f" => "Ground Zero",
            "labyrinth" => "The Labyrinth",
            "terminal" => "Terminal",
            _ => FormatDisplayName(location ?? "Any map")
        };
    }

    private static string FormatDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Any map";
        }

        var normalized = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.Join(
            " ",
            normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static bool IsRequiredEquipmentCondition(string conditionType)
    {
        if (conditionType.Contains("Exclusive", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return conditionType.Contains("Equipment", StringComparison.OrdinalIgnoreCase)
               || conditionType.Contains("Wear", StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyQuestCondition(string conditionType)
    {
        if (conditionType.Contains("Wear", StringComparison.OrdinalIgnoreCase))
        {
            return "Wear equipment";
        }

        if (conditionType.Equals("HandoverItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Handover item";
        }

        if (conditionType.Equals("FindItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Find item";
        }

        if (conditionType.Contains("Beacon", StringComparison.OrdinalIgnoreCase))
        {
            return "Place marker";
        }

        if (conditionType.Contains("Plant", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("LeaveItem", StringComparison.OrdinalIgnoreCase)
            || conditionType.Contains("PlaceItem", StringComparison.OrdinalIgnoreCase))
        {
            return "Plant item";
        }

        return "Required equipment";
    }

    private static bool CaliberMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        return NormalizeCaliber(left).Equals(NormalizeCaliber(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCaliber(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string FriendlyCaliber(string? caliber)
    {
        if (string.IsNullOrWhiteSpace(caliber))
        {
            return "Unknown caliber";
        }

        return caliber
            .Replace("Caliber", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("x", "×", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static double GetStackCount(InventoryNode item)
    {
        return Math.Max(1d, ReadDouble(item.Upd, 1d, "StackObjectsCount", "stackObjectsCount"));
    }

    private static int ParseQuestStatus(string status)
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
            _ => 0
        };
    }

    private static IReadOnlyList<string> ReadMongoIds(JsonNode? node)
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

    private static void CollectStrings(JsonNode? node, ISet<string> output)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
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
        }
    }

    private static (double Current, double Maximum) ReadCurrentMaximum(JsonNode? node)
    {
        return (
            ReadDouble(node, 0d, "Current", "current"),
            ReadDouble(node, 0d, "Maximum", "maximum"));
    }

    private static int ToPercent(double current, double maximum)
    {
        return maximum <= 0d
            ? 0
            : Math.Clamp(Convert.ToInt32(Math.Round(current / maximum * 100d)), 0, 100);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var output = new List<char>(value.Length + 8) { value[0] };
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsWhiteSpace(value[index - 1]))
            {
                output.Add(' ');
            }

            output.Add(value[index]);
        }

        return new string(output.ToArray());
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasEntries(JsonNode? node)
    {
        return node switch
        {
            JsonArray array => array.Count > 0,
            JsonObject obj => obj.Count > 0,
            JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
            JsonValue value when value.TryGetValue<string>(out var text) => !string.IsNullOrWhiteSpace(text),
            _ => false
        };
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var direct))
            {
                return direct;
            }

            var pair = obj.FirstOrDefault(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Key))
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
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value?.ToString();
    }

    private static double ReadDouble(JsonNode? node, double fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (jsonValue.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonNode? node, int fallback, params string[] names)
    {
        return Convert.ToInt32(Math.Round(ReadDouble(node, fallback, names)));
    }

    private static bool ReadBool(JsonNode? node, bool fallback, params string[] names)
    {
        var value = names.Length == 0 ? node : GetProperty(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static HermesLoadoutValueSummary DisabledValueSummary()
    {
        return new HermesLoadoutValueSummary(
            false,
            "Value and insurance analysis is disabled in the HERMES BepInEx settings.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            "Disabled",
            0,
            0,
            0,
            0,
            0,
            0,
            "DISABLED",
            [],
            [],
            []);
    }

    private static HermesLoadoutSummaryResponse NotFound(string message)
    {
        return new HermesLoadoutSummaryResponse(
            false,
            message,
            "UNAVAILABLE",
            0,
            0,
            0,
            new HermesVitalsSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            [],
            [],
            [],
            new HermesMedicalReadiness(0, 0, false, false, false, false, false, 0, 0, []),
            [],
            [],
            new HermesLoadoutValueSummary(
                false,
                message,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                "Unavailable",
                0,
                0,
                0,
                0,
                0,
                0,
                "UNAVAILABLE",
                [],
                [],
                []),
            [],
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private sealed record LoadoutCacheEntry(HermesLoadoutSummaryResponse Response);
}
