using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Hermes.Server.Services;

/// <summary>
/// Resolves the actual active PMC profile behind an SPT request session.
/// HERMES must not use the account/session token alone as a profile identity because a launcher
/// session can outlive a profile switch. The returned scope is stable for one PMC profile and
/// changes immediately when a different PMC profile becomes active.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class HermesProfileScopeService(
    ProfileHelper profileHelper,
    JsonUtil jsonUtil)
{
    public HermesProfileScope? Resolve(MongoId sessionId)
    {
        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return null;
        }

        var profileJson = jsonUtil.Serialize(profile) ?? "{}";
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(profileJson) as JsonObject;
        }
        catch
        {
            root = null;
        }

        var info = GetObject(root, "Info", "info");
        var profileId = FirstNonEmpty(
            ReadString(root, "_id", "Id", "id"),
            ReadString(info, "Id", "id", "ProfileId", "profileId"));
        var accountId = FirstNonEmpty(
            ReadString(root, "aid", "AID", "AccountId", "accountId"),
            ReadString(info, "AccountId", "accountId"));
        var side = ReadString(info, "Side", "side") ?? string.Empty;
        var gameVersion = ReadString(info, "GameVersion", "gameVersion") ?? string.Empty;
        var nickname = ReadString(info, "Nickname", "nickname") ?? string.Empty;

        // PMC profiles always have an id in normal SPT data. The fallback deliberately uses
        // several stable identity fields rather than profile contents, so inventory/quest changes
        // never create a new scope.
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profileId = FirstNonEmpty(accountId, nickname, sessionId.ToString())!;
        }

        var identitySeed = string.Join(
            "|",
            "HERMES_PROFILE_SCOPE_V2",
            sessionId.ToString(),
            profileId,
            accountId ?? string.Empty,
            side,
            gameVersion);
        var scopeKey = Hash(identitySeed);
        var contextToken = Hash("HERMES_CONTEXT|" + identitySeed);

        return new HermesProfileScope(
            sessionId,
            profileId,
            accountId ?? string.Empty,
            scopeKey,
            contextToken,
            profileJson);
    }

    private static JsonObject? GetObject(JsonObject? root, params string[] names)
        => GetProperty(root, names) as JsonObject;

    private static JsonNode? GetProperty(JsonObject? root, params string[] names)
    {
        if (root is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            foreach (var pair in root)
            {
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject? root, params string[] names)
    {
        var node = GetProperty(root, names);
        if (node is null)
        {
            return null;
        }

        try
        {
            var value = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            var value = node.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record HermesProfileScope(
    MongoId SessionId,
    string ProfileId,
    string AccountId,
    string ScopeKey,
    string ContextToken,
    string ProfileJson);
