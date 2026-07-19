using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesActionService(
    ProfileHelper profileHelper,
    HermesPreparedProfileSnapshotService preparedProfiles,
    HermesCatalogService catalogService,
    HermesCacheService cacheService,
    HermesStashAnalysisService stashAnalysisService,
    HermesLoadoutService loadoutService,
    HermesChangeTrackingService changeTrackingService,
    SaveServer saveServer)
{
    private const int ProposalTtlSeconds = 90;
    private const int DuplicateProposalWindowSeconds = 12;
    private const int MaximumHistoryEntriesPerSession = 24;
    private const string InventoryTagActionKind = "HERMES_INVENTORY_TAG";

    private static readonly Dictionary<string, int> TagColorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 0,
        ["orange"] = 1,
        ["yellow"] = 2,
        ["green"] = 3,
        ["blue"] = 4,
        ["violet"] = 5,
        ["purple"] = 5,
        ["grey"] = 6,
        ["gray"] = 6,
        ["black"] = 6,
        ["white"] = 6,
        ["default"] = 6
    };

    private static readonly Dictionary<int, string> TagColorLabels = new()
    {
        [0] = "red",
        [1] = "orange",
        [2] = "yellow",
        [3] = "green",
        [4] = "blue",
        [5] = "violet",
        [6] = "grey"
    };

    private readonly ConcurrentDictionary<string, PendingAction> _pendingById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingByFingerprint =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<HermesActionHistoryEntry>> _historyBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public HermesActionProposalResponse ProposeTestAction(MongoId sessionId)
    {
        PurgeExpired();

        var now = Now();
        var sessionKey = sessionId.ToString();
        const string actionKind = "HERMES_TEST_CONFIRMATION";
        var fingerprint = $"{sessionKey}:{actionKind}";

        lock (_sync)
        {
            if (TryReuseProposal(fingerprint, now, out var reused))
            {
                return new HermesActionProposalResponse(
                    true,
                    "Reused the existing pending test-action proposal instead of creating a duplicate request.",
                    reused);
            }

            var proposal = CreateProposal(
                now,
                actionKind,
                "Run harmless HERMES test action",
                canExecute: true,
                isHarmlessTestAction: true,
                new HermesActionPreview(
                    "Harmless action pipeline verification",
                    ["No profile inventory item will be modified"],
                    "1 dry-run confirmation",
                    "0 roubles",
                    "HERMES internal action pipeline",
                    "A success message and history row are created; no SPT profile data is changed.",
                    [
                        "This harmless test action does not execute real buy, sell, move, craft, hideout, or profile-changing actions.",
                        "This token expires quickly and can be used only once."
                    ],
                    null));

            _pendingById[proposal.ProposalId] = new PendingAction(
                sessionKey,
                fingerprint,
                proposal,
                new TestActionPayload(),
                false,
                false,
                null);
            _pendingByFingerprint[fingerprint] = proposal.ProposalId;
            return new HermesActionProposalResponse(true, null, proposal);
        }
    }

    public HermesActionProposalResponse ProposeInventoryTagAction(
        MongoId sessionId,
        string operation,
        string tagName,
        string tagColor,
        IEnumerable<string> publicInstanceKeys)
    {
        PurgeExpired();

        var now = Now();
        var sessionKey = sessionId.ToString();
        var normalizedOperation = NormalizeTagOperation(operation);
        var normalizedName = NormalizeTagText(tagName, 48);
        var normalizedColor = NormalizeTagColorText(tagColor);
        var selectedKeys = publicInstanceKeys
            .Select(key => (key ?? string.Empty).Trim())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedOperation is null)
        {
            return MissingProposal("Choose apply, change, or remove for the inventory tag action.");
        }

        if (selectedKeys.Count == 0)
        {
            return MissingProposal("Select one or more matching owned copies before proposing an inventory tag action.");
        }

        if (!normalizedOperation.Equals("remove", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(normalizedName))
        {
            return MissingProposal("Enter a tag name before proposing this inventory tag action.");
        }

        if (!normalizedOperation.Equals("remove", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(normalizedColor))
        {
            normalizedColor = "blue";
        }

        var snapshot = BuildInventorySnapshot(sessionId, forceRefresh: true);
        if (snapshot is null)
        {
            return MissingProposal("HERMES could not read the active PMC inventory for a tag proposal.");
        }

        var selectedItems = new List<TagActionItem>();
        var missingKeys = new List<string>();
        foreach (var key in selectedKeys)
        {
            var item = snapshot.Items.FirstOrDefault(candidate =>
                CreateInstanceKey(sessionId, candidate.Id).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                missingKeys.Add(key);
                continue;
            }

            var oldTag = ReadTag(item.Upd);
            selectedItems.Add(new TagActionItem(
                item.Id,
                key,
                item.TemplateId,
                item.ParentId,
                item.SlotId,
                item.LocationFingerprint,
                catalogService.GetPlayerFacingName(new MongoId(item.TemplateId)),
                oldTag.Name,
                oldTag.Color));
        }

        var cannotExecuteReason = ValidateTagProposal(
            normalizedOperation,
            normalizedName,
            selectedItems,
            missingKeys);
        var displayOperation = normalizedOperation switch
        {
            "change" => "Change inventory tag",
            "remove" => "Reset inventory tag",
            _ => "Apply inventory tag"
        };
        var newTagText = normalizedOperation.Equals("remove", StringComparison.OrdinalIgnoreCase)
            ? "(none)"
            : FormatTag(normalizedName, normalizedColor);
        var affectedItems = selectedItems
            .Select(item => $"{item.DisplayName} [{ShortKey(item.PublicInstanceKey)}]: {FormatTag(item.OldTagName, item.OldTagColor)} -> {newTagText}")
            .ToList();
        foreach (var missingKey in missingKeys)
        {
            affectedItems.Add($"Missing selected copy [{ShortKey(missingKey)}]");
        }

        var warnings = new List<string>
        {
            "Only the explicitly selected public instance keys in this proposal are eligible for mutation.",
            "HERMES rechecks item identity, parent, slot, grid location, and old tag values before confirming.",
            "No item parent, slot, location, stack count, contained item, or inventory structure field will be changed."
        };
        if (missingKeys.Count > 0)
        {
            warnings.Add("One or more selected copies could not be found in the current inventory snapshot.");
        }

        var fingerprint = $"{sessionKey}:{InventoryTagActionKind}:{normalizedOperation}:{normalizedName}:{normalizedColor}:{string.Join(",", selectedKeys)}";
        lock (_sync)
        {
            if (TryReuseProposal(fingerprint, now, out var reused))
            {
                return new HermesActionProposalResponse(
                    true,
                    "Reused the existing pending inventory-tag proposal instead of creating a duplicate request.",
                    reused);
            }

            var proposal = CreateProposal(
                now,
                InventoryTagActionKind,
                displayOperation,
                cannotExecuteReason is null,
                isHarmlessTestAction: false,
                new HermesActionPreview(
                    displayOperation,
                    affectedItems,
                    $"{selectedItems.Count:N0} explicitly selected item(s)",
                    "0 roubles",
                    "PMC inventory tag field",
                    normalizedOperation switch
                    {
                        "change" => $"Selected item tag values change to {newTagText}.",
                        "remove" => "Selected item tag values are reset.",
                        _ => $"Selected untagged item copies receive {newTagText}."
                    },
                    warnings,
                    cannotExecuteReason));

            _pendingById[proposal.ProposalId] = new PendingAction(
                sessionKey,
                fingerprint,
                proposal,
                new InventoryTagActionPayload(normalizedOperation, normalizedName, normalizedColor, selectedItems),
                false,
                false,
                null);
            _pendingByFingerprint[fingerprint] = proposal.ProposalId;
            return new HermesActionProposalResponse(true, null, proposal);
        }
    }

    public ValueTask<HermesActionResultResponse> ConfirmAsync(MongoId sessionId, string proposalId, string token)
        => ResolveAsync(sessionId, proposalId, token, cancel: false);

    public ValueTask<HermesActionResultResponse> CancelAsync(MongoId sessionId, string proposalId, string token)
        => ResolveAsync(sessionId, proposalId, token, cancel: true);

    public HermesActionHistoryResponse GetHistory(MongoId sessionId)
    {
        var sessionKey = sessionId.ToString();
        lock (_sync)
        {
            var entries = _historyBySession.TryGetValue(sessionKey, out var history)
                ? history.OrderByDescending(entry => entry.ResolvedUnixTime).ToList()
                : [];
            return new HermesActionHistoryResponse(
                true,
                entries.Count == 0 ? "No action history for this session yet." : null,
                entries.Count,
                entries);
        }
    }

    private async ValueTask<HermesActionResultResponse> ResolveAsync(
        MongoId sessionId,
        string proposalId,
        string token,
        bool cancel)
    {
        var now = Now();
        var sessionKey = sessionId.ToString();
        proposalId = (proposalId ?? string.Empty).Trim();
        token = (token ?? string.Empty).Trim();
        PendingAction pending;

        lock (_sync)
        {
            if (!_pendingById.TryGetValue(proposalId, out pending!)
                || !string.Equals(pending.SessionKey, sessionKey, StringComparison.Ordinal))
            {
                return new HermesActionResultResponse(
                    false,
                    false,
                    false,
                    false,
                    false,
                    "Unavailable",
                    "The action proposal is no longer pending. Request a fresh proposal.",
                    null,
                    null);
            }

            var proposal = WithRemaining(pending.Proposal, now);
            if (!string.Equals(proposal.ConfirmationToken, token, StringComparison.Ordinal))
            {
                return new HermesActionResultResponse(
                    true,
                    false,
                    false,
                    false,
                    false,
                    "Rejected",
                    "The confirmation token did not match this action proposal.",
                    proposal,
                    null);
            }

            if (pending.Resolved)
            {
                return new HermesActionResultResponse(
                    true,
                    pending.HistoryEntry?.Executed == true,
                    pending.HistoryEntry?.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) == true,
                    false,
                    true,
                    pending.HistoryEntry?.Status ?? "Duplicate",
                    pending.HistoryEntry?.Message ?? "This action request was already resolved.",
                    proposal,
                    pending.HistoryEntry);
            }

            if (pending.Resolving)
            {
                return new HermesActionResultResponse(
                    true,
                    false,
                    false,
                    false,
                    true,
                    "Duplicate",
                    "This action request is already being resolved. Wait for the current confirmation result.",
                    proposal,
                    null);
            }

            if (proposal.ExpiresUnixTime <= now)
            {
                var expiredEntry = CreateHistoryEntry(
                    proposal,
                    "Expired",
                    "The confirmation token expired before the action was confirmed.",
                    now,
                    executed: false);
                StoreResolved(pending, expiredEntry);
                return new HermesActionResultResponse(
                    true,
                    false,
                    false,
                    true,
                    false,
                    "Expired",
                    expiredEntry.Message,
                    proposal,
                    expiredEntry);
            }

            if (cancel)
            {
                var cancelledEntry = CreateHistoryEntry(
                    proposal,
                    "Cancelled",
                    "The action proposal was cancelled. No profile data was changed.",
                    now,
                    executed: false);
                StoreResolved(pending, cancelledEntry);
                return new HermesActionResultResponse(
                    true,
                    false,
                    true,
                    false,
                    false,
                    "Cancelled",
                    cancelledEntry.Message,
                    proposal,
                    cancelledEntry);
            }

            if (!proposal.CanExecute)
            {
                var blockedEntry = CreateHistoryEntry(
                    proposal,
                    "Blocked",
                    proposal.Preview.CannotExecuteReason ?? "The action cannot currently execute.",
                    now,
                    executed: false);
                StoreResolved(pending, blockedEntry);
                return new HermesActionResultResponse(
                    true,
                    false,
                    false,
                    false,
                    false,
                    "Blocked",
                    blockedEntry.Message,
                    proposal,
                    blockedEntry);
            }

            _pendingById[pending.Proposal.ProposalId] = pending with { Resolving = true };
        }

        HermesActionHistoryEntry historyEntry;
        try
        {
            historyEntry = await ExecuteConfirmedAsync(sessionId, pending);
        }
        catch (Exception ex)
        {
            historyEntry = CreateHistoryEntry(
                pending.Proposal,
                "Rejected",
                $"The confirmed action failed before HERMES could safely finish it: {ex.Message}",
                Now(),
                executed: false);
        }
        lock (_sync)
        {
            StoreResolved(pending, historyEntry);
        }

        return new HermesActionResultResponse(
            true,
            historyEntry.Executed,
            false,
            false,
            false,
            historyEntry.Status,
            historyEntry.Message,
            WithRemaining(pending.Proposal, Now()),
            historyEntry);
    }

    private async ValueTask<HermesActionHistoryEntry> ExecuteConfirmedAsync(MongoId sessionId, PendingAction pending)
    {
        var now = Now();
        if (pending.Payload is TestActionPayload)
        {
            return CreateHistoryEntry(
                pending.Proposal,
                "Succeeded",
                "Harmless HERMES 1.1 test action confirmed. The confirmation pipeline worked and no inventory/profile action was performed.",
                now,
                executed: true);
        }

        if (pending.Payload is not InventoryTagActionPayload tagAction)
        {
            return CreateHistoryEntry(
                pending.Proposal,
                "Rejected",
                "HERMES did not recognize the action payload. No profile data was changed.",
                now,
                executed: false);
        }

        var validation = ValidateTagConfirmation(sessionId, tagAction);
        if (validation is not null)
        {
            return CreateHistoryEntry(
                pending.Proposal with
                {
                    CanExecute = false,
                    Preview = pending.Proposal.Preview with { CannotExecuteReason = validation }
                },
                "Rejected",
                validation,
                now,
                executed: false);
        }

        var profile = profileHelper.GetPmcProfile(sessionId);
        if (profile is null)
        {
            return CreateHistoryEntry(
                pending.Proposal,
                "Rejected",
                "HERMES could not read the active PMC profile at confirmation time. No profile data was changed.",
                now,
                executed: false);
        }

        var liveItems = GetLiveInventoryItems(profile);
        var liveSelection = new List<(TagActionItem Selected, object LiveItem)>();
        foreach (var selected in tagAction.Items)
        {
            var liveItem = FindLiveItem(liveItems, selected.ItemId);
            if (liveItem is null)
            {
                return CreateHistoryEntry(
                    pending.Proposal,
                    "Rejected",
                    $"Selected item {ShortKey(selected.PublicInstanceKey)} was missing at mutation time. No profile data was changed.",
                    Now(),
                    executed: false);
            }

            liveSelection.Add((selected, liveItem));
        }

        var mutationShapeIssue = ValidateTagMutationShape(liveSelection, tagAction);
        if (mutationShapeIssue is not null)
        {
            return CreateHistoryEntry(
                pending.Proposal,
                "Rejected",
                mutationShapeIssue,
                Now(),
                executed: false);
        }

        var applied = new List<(TagActionItem Selected, object LiveItem)>();
        try
        {
            foreach (var pair in liveSelection)
            {
                ApplyTagMutation(pair.LiveItem, tagAction);
                applied.Add(pair);
            }
        }
        catch
        {
            foreach (var pair in applied)
            {
                RestoreTagMutation(pair.LiveItem, pair.Selected);
            }

            throw;
        }

        preparedProfiles.Invalidate(sessionId);
        stashAnalysisService.Clear("Confirmed inventory tag action");
        loadoutService.Clear("Confirmed inventory tag action");
        cacheService.Clear("Confirmed inventory tag action");
        changeTrackingService.RequestRecheck(sessionId, "Confirmed inventory tag action");
        var saveSeconds = await saveServer.SaveProfileAsync(sessionId);

        return CreateHistoryEntry(
            pending.Proposal,
            "Succeeded",
            $"Inventory tag action applied to {tagAction.Items.Count:N0} selected item(s) and saved in {saveSeconds:N3}s.",
            Now(),
            executed: true);
    }

    private string? ValidateTagConfirmation(MongoId sessionId, InventoryTagActionPayload tagAction)
    {
        var current = BuildInventorySnapshot(sessionId, forceRefresh: true);
        if (current is null)
        {
            return "HERMES could not read the current PMC inventory at confirmation time.";
        }

        foreach (var selected in tagAction.Items)
        {
            if (!current.ById.TryGetValue(selected.ItemId, out var item))
            {
                return $"Selected item {ShortKey(selected.PublicInstanceKey)} is missing. Request a fresh proposal.";
            }

            if (!string.Equals(CreateInstanceKey(sessionId, item.Id), selected.PublicInstanceKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.TemplateId, selected.TemplateId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.ParentId ?? string.Empty, selected.ParentId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.SlotId ?? string.Empty, selected.SlotId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.LocationFingerprint, selected.LocationFingerprint, StringComparison.Ordinal))
            {
                return $"Selected item {ShortKey(selected.PublicInstanceKey)} moved after the proposal was created. Request a fresh proposal.";
            }

            var currentTag = ReadTag(item.Upd);
            if (!string.Equals(currentTag.Name ?? string.Empty, selected.OldTagName ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(currentTag.Color ?? string.Empty, selected.OldTagColor ?? string.Empty, StringComparison.Ordinal))
            {
                return $"Selected item {ShortKey(selected.PublicInstanceKey)} has a different tag than the confirmation preview. Request a fresh proposal.";
            }
        }

        return null;
    }

    private static string? ValidateTagProposal(
        string operation,
        string tagName,
        IReadOnlyList<TagActionItem> selectedItems,
        IReadOnlyList<string> missingKeys)
    {
        if (missingKeys.Count > 0)
        {
            return "One or more explicitly selected items are missing from the current PMC inventory.";
        }

        if (selectedItems.Count == 0)
        {
            return "No selected inventory items could be resolved.";
        }

        if (!operation.Equals("remove", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(tagName))
        {
            return "A tag name is required.";
        }

        if (operation.Equals("apply", StringComparison.OrdinalIgnoreCase)
            && selectedItems.Any(item => !string.IsNullOrWhiteSpace(item.OldTagName)))
        {
            return "Apply tag is only available for selected items that do not already have a tag. Use Change existing tag instead.";
        }

        if ((operation.Equals("change", StringComparison.OrdinalIgnoreCase)
             || operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
            && selectedItems.Any(item => string.IsNullOrWhiteSpace(item.OldTagName)))
        {
            return "Change and reset tag actions require every selected item to already have a tag.";
        }

        return null;
    }

    private static string? ValidateTagMutationShape(
        IReadOnlyList<(TagActionItem Selected, object LiveItem)> liveSelection,
        InventoryTagActionPayload action)
    {
        foreach (var pair in liveSelection)
        {
            var item = pair.LiveItem;
            var upd = GetMember(item, "upd", "Upd");
            if (action.Operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                var resetUpdMember = FindMember(item.GetType(), "upd", "Upd");
                if (resetUpdMember is null)
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} does not expose an update field for tags.";
                }

                if (upd is null && !CanSetMember(resetUpdMember))
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses a read-only update field for tags.";
                }

                var resetUpdType = upd?.GetType() ?? GetMemberType(resetUpdMember);
                if (upd is null && !HasUsableParameterlessConstructor(resetUpdType))
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} cannot safely create an update field for tags.";
                }

                var resetTagMember = FindMember(resetUpdType, "Tag", "tag");
                if (resetTagMember is null)
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} does not expose an inventory tag field.";
                }

                var resetTag = upd is null ? null : GetMember(upd, "Tag", "tag");
                if (resetTag is null && !CanSetMember(resetTagMember))
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses a read-only inventory tag field.";
                }

                var resetTagType = resetTag?.GetType() ?? GetMemberType(resetTagMember);
                if (resetTag is null && !HasUsableParameterlessConstructor(resetTagType))
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} cannot safely create an inventory tag field.";
                }

                if (!CanAssignString(resetTagType, "Name", "name") || !CanAssignTagColor(resetTagType, "Color", "color"))
                {
                    return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses an unsupported inventory tag schema.";
                }

                continue;
            }

            var updMember = FindMember(item.GetType(), "upd", "Upd");
            if (updMember is null)
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} does not expose an update field for tags.";
            }

            if (upd is null && !CanSetMember(updMember))
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses a read-only update field for tags.";
            }

            var updType = upd?.GetType() ?? GetMemberType(updMember);
            if (upd is null && !HasUsableParameterlessConstructor(updType))
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} cannot safely create an update field for tags.";
            }

            var tagMember = FindMember(updType, "Tag", "tag");
            if (tagMember is null)
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} does not expose an inventory tag field.";
            }

            var tag = upd is null ? null : GetMember(upd, "Tag", "tag");
            if (tag is null && !CanSetMember(tagMember))
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses a read-only inventory tag field.";
            }

            var tagType = tag?.GetType() ?? GetMemberType(tagMember);
            if (tag is null && !HasUsableParameterlessConstructor(tagType))
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} cannot safely create an inventory tag field.";
            }

            if (!CanAssignString(tagType, "Name", "name") || !CanAssignTagColor(tagType, "Color", "color"))
            {
                return $"Selected item {ShortKey(pair.Selected.PublicInstanceKey)} uses an unsupported inventory tag schema.";
            }
        }

        return null;
    }

    private static void ApplyTagMutation(object item, InventoryTagActionPayload action)
    {
        var upd = GetMember(item, "upd", "Upd");
        if (action.Operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            upd ??= EnsureChildObject(item, "Upd", "upd");
            if (upd is null)
            {
                throw new InvalidOperationException("The inventory item upd object could not be created.");
            }

            var resetTag = GetMember(upd, "Tag", "tag") ?? EnsureChildObject(upd, "Tag", "tag");
            if (resetTag is null)
            {
                throw new InvalidOperationException("The inventory item tag object could not be created.");
            }

            SetMember(resetTag, string.Empty, "Name", "name");
            SetTagColorMember(resetTag, "red", "Color", "color");
            return;
        }

        upd ??= EnsureChildObject(item, "Upd", "upd");
        if (upd is null)
        {
            throw new InvalidOperationException("The inventory item upd object could not be created.");
        }

        var tag = GetMember(upd, "Tag", "tag") ?? EnsureChildObject(upd, "Tag", "tag");
        if (tag is null)
        {
            throw new InvalidOperationException("The inventory item tag object could not be created.");
        }

        SetMember(tag, action.TagName, "Name", "name");
        SetTagColorMember(tag, action.TagColor, "Color", "color");
    }

    private static void RestoreTagMutation(object item, TagActionItem original)
    {
        var upd = GetMember(item, "upd", "Upd");
        if (string.IsNullOrWhiteSpace(original.OldTagName))
        {
            if (upd is not null)
            {
                ResetTagMember(upd);
            }

            return;
        }

        upd ??= EnsureChildObject(item, "Upd", "upd");
        if (upd is null)
        {
            return;
        }

        var tag = GetMember(upd, "Tag", "tag") ?? EnsureChildObject(upd, "Tag", "tag");
        if (tag is null)
        {
            return;
        }

        SetMember(tag, original.OldTagName, "Name", "name");
        SetTagColorMember(tag, original.OldTagColor, "Color", "color");
    }

    private static object? EnsureChildObject(object owner, params string[] names)
    {
        var member = FindMember(owner.GetType(), names);
        if (member is null)
        {
            return null;
        }

        var current = GetMemberValue(owner, member);
        if (current is not null)
        {
            return current;
        }

        var memberType = GetMemberType(member);
        var created = Activator.CreateInstance(memberType);
        SetMemberValue(owner, member, created);
        return created;
    }

    private static void ResetTagMember(object upd)
    {
        if (upd is JsonObject jsonObject)
        {
            foreach (var name in new[] { "Tag", "tag" })
            {
                if (jsonObject.Remove(name))
                {
                    return;
                }
            }

            return;
        }

        if (upd is System.Collections.IDictionary dictionary)
        {
            foreach (var name in new[] { "Tag", "tag" })
            {
                if (dictionary.Contains(name))
                {
                    dictionary.Remove(name);
                    return;
                }
            }

            return;
        }

        SetMember(upd, null, "Tag", "tag");
    }

    private static List<object> GetLiveInventoryItems(object profile)
    {
        var inventory = GetMember(profile, "Inventory", "inventory");
        var rawItems = GetMember(inventory, "items", "Items");
        return rawItems is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>().ToList()
            : [];
    }

    private static object? FindLiveItem(IEnumerable<object> liveItems, string itemId)
        => liveItems.FirstOrDefault(item =>
            string.Equals(ReadMemberString(item, "_id", "Id", "id"), itemId, StringComparison.OrdinalIgnoreCase));

    private InventorySnapshot? BuildInventorySnapshot(MongoId sessionId, bool forceRefresh)
    {
        var preparedProfile = preparedProfiles.Get(sessionId, forceRefresh);
        if (preparedProfile is null)
        {
            return null;
        }

        var inventory = GetProperty(preparedProfile.Root, "Inventory", "inventory");
        if (inventory is null)
        {
            return null;
        }

        var items = new List<InventoryItemNode>();
        foreach (var node in GetArray(GetProperty(inventory, "items", "Items")))
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var id = ReadString(item, "_id", "Id", "id");
            var templateId = ReadString(item, "_tpl", "Template", "template");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }

            items.Add(new InventoryItemNode(
                id,
                templateId,
                ReadString(item, "parentId", "ParentId"),
                ReadString(item, "slotId", "SlotId"),
                FingerprintJson(GetProperty(item, "location", "Location")),
                GetProperty(item, "upd", "Upd") as JsonObject));
        }

        return new InventorySnapshot(
            items,
            items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase));
    }

    private static (string? Name, string? Color) ReadTag(JsonObject? upd)
    {
        var tag = GetProperty(upd, "Tag", "tag");
        return (
            NormalizeTagText(ReadString(tag, "Name", "name"), 48),
            NormalizeTagColorText(ReadString(tag, "Color", "color")));
    }

    private static JsonNode? GetProperty(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<JsonNode?> GetArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        return [];
    }

    private static string? ReadString(JsonNode? node, params string[] names)
    {
        var value = GetProperty(node, names);
        return value?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : value?.ToString();
    }

    private static string FingerprintJson(JsonNode? node)
        => node is null ? string.Empty : node.ToJsonString();

    private bool TryReuseProposal(string fingerprint, long now, out HermesActionProposal? proposal)
    {
        if (_pendingByFingerprint.TryGetValue(fingerprint, out var existingId)
            && _pendingById.TryGetValue(existingId, out var existing)
            && !existing.Resolved
            && existing.Proposal.ExpiresUnixTime > now
            && now - existing.Proposal.CreatedUnixTime <= DuplicateProposalWindowSeconds)
        {
            proposal = WithRemaining(existing.Proposal, now);
            return true;
        }

        proposal = null;
        return false;
    }

    private static HermesActionProposal CreateProposal(
        long now,
        string actionKind,
        string displayName,
        bool canExecute,
        bool isHarmlessTestAction,
        HermesActionPreview preview)
        => new(
            CreateToken(16),
            CreateToken(24),
            actionKind,
            displayName,
            canExecute,
            isHarmlessTestAction,
            true,
            now,
            now + ProposalTtlSeconds,
            ProposalTtlSeconds,
            preview);

    private static HermesActionProposalResponse MissingProposal(string message)
        => new(false, message, null);

    private void StoreResolved(PendingAction pending, HermesActionHistoryEntry entry)
    {
        var resolved = pending with { Resolved = true, Resolving = false, HistoryEntry = entry };
        _pendingById[pending.Proposal.ProposalId] = resolved;
        _pendingByFingerprint.TryRemove(pending.Fingerprint, out _);

        if (!_historyBySession.TryGetValue(pending.SessionKey, out var history))
        {
            history = [];
            _historyBySession[pending.SessionKey] = history;
        }

        history.Add(entry);
        history.Sort((left, right) => right.ResolvedUnixTime.CompareTo(left.ResolvedUnixTime));
        if (history.Count > MaximumHistoryEntriesPerSession)
        {
            history.RemoveRange(MaximumHistoryEntriesPerSession, history.Count - MaximumHistoryEntriesPerSession);
        }
    }

    private static HermesActionHistoryEntry CreateHistoryEntry(
        HermesActionProposal proposal,
        string status,
        string message,
        long resolvedUnixTime,
        bool executed)
        => new(
            CreateToken(12),
            proposal.ProposalId,
            proposal.ActionKind,
            proposal.DisplayName,
            status,
            message,
            proposal.CreatedUnixTime,
            resolvedUnixTime,
            executed,
            proposal.IsHarmlessTestAction,
            proposal.Preview);

    private void PurgeExpired()
    {
        var now = Now();
        foreach (var pair in _pendingById)
        {
            if (pair.Value.Resolved || pair.Value.Resolving)
            {
                continue;
            }

            if (pair.Value.Proposal.ExpiresUnixTime <= now)
            {
                _pendingById.TryRemove(pair.Key, out var removed);
                if (removed is not null)
                {
                    _pendingByFingerprint.TryRemove(removed.Fingerprint, out _);
                }
            }
        }
    }

    private static HermesActionProposal WithRemaining(HermesActionProposal proposal, long now)
        => proposal with
        {
            ExpiresInSeconds = (int)Math.Max(0L, proposal.ExpiresUnixTime - now)
        };

    private static string? NormalizeTagOperation(string operation)
        => (operation ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "apply" => "apply",
            "change" => "change",
            "remove" => "remove",
            _ => null
        };

    private static string NormalizeTagText(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string NormalizeTagColorText(string? value)
    {
        var normalized = NormalizeTagText(value, 32).ToLowerInvariant();
        if (int.TryParse(normalized, out var colorId))
        {
            return TagColorLabels.TryGetValue(colorId, out var label)
                ? label
                : normalized;
        }

        return TagColorIds.ContainsKey(normalized)
            ? normalized
            : string.Empty;
    }

    private static int TagColorId(string? value)
    {
        var normalized = NormalizeTagColorText(value);
        return TagColorIds.TryGetValue(normalized, out var colorId)
            ? colorId
            : TagColorIds["blue"];
    }

    private static string FormatTag(string? name, string? color)
        => string.IsNullOrWhiteSpace(name)
            ? "(none)"
            : string.IsNullOrWhiteSpace(color)
                ? name.Trim()
                : $"{name.Trim()} / {NormalizeTagColorText(color)}";

    private static string ShortKey(string key)
        => key.Length <= 8 ? key : key[..8];

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string CreateToken(int byteCount)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private static string CreateInstanceKey(MongoId sessionId, string itemId)
    {
        var input = $"HERMES:{sessionId}:{itemId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static object? GetMember(object? owner, params string[] names)
    {
        if (owner is null)
        {
            return null;
        }

        var member = FindMember(owner.GetType(), names);
        return member is null ? null : GetMemberValue(owner, member);
    }

    private static string? ReadMemberString(object owner, params string[] names)
        => GetMember(owner, names)?.ToString();

    private static void SetMember(object owner, object? value, params string[] names)
    {
        var member = FindMember(owner.GetType(), names)
            ?? throw new MissingMemberException(owner.GetType().FullName, string.Join("/", names));
        SetMemberValue(owner, member, value);
    }

    private static MemberInfo? FindMember(Type type, params string[] names)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var name in names)
        {
            var property = type.GetProperty(name, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property;
            }

            var field = type.GetField(name, flags);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object)
        };

    private static bool HasUsableParameterlessConstructor(Type type)
        => type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null;

    private static bool CanAssignString(Type ownerType, params string[] names)
    {
        var member = FindMember(ownerType, names);
        if (member is null)
        {
            return false;
        }

        var memberType = GetMemberType(member);
        return CanSetMember(member)
               && (memberType == typeof(string) || memberType == typeof(object));
    }

    private static bool CanAssignTagColor(Type ownerType, params string[] names)
    {
        var member = FindMember(ownerType, names);
        if (member is null || !CanSetMember(member))
        {
            return false;
        }

        var memberType = Nullable.GetUnderlyingType(GetMemberType(member)) ?? GetMemberType(member);
        return memberType == typeof(string)
               || memberType == typeof(int)
               || memberType == typeof(double)
               || memberType == typeof(object);
    }

    private static void SetTagColorMember(object owner, string? color, params string[] names)
    {
        var member = FindMember(owner.GetType(), names)
            ?? throw new MissingMemberException(owner.GetType().FullName, string.Join("/", names));
        var value = CreateTagColorValue(GetMemberType(member), color);
        SetMemberValue(owner, member, value);
    }

    private static object? CreateTagColorValue(Type targetType, string? color)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;
        var normalized = NormalizeTagColorText(color);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return nullableType is not null || !effectiveType.IsValueType
                ? null
                : Activator.CreateInstance(effectiveType);
        }

        var colorId = TagColorId(normalized);
        if (effectiveType == typeof(int))
        {
            return colorId;
        }

        if (effectiveType == typeof(double))
        {
            return (double)colorId;
        }

        if (effectiveType == typeof(string))
        {
            return normalized;
        }

        return colorId;
    }

    private static bool CanSetMember(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.CanWrite,
            FieldInfo => true,
            _ => false
        };

    private static object? GetMemberValue(object owner, MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.GetValue(owner),
            FieldInfo field => field.GetValue(owner),
            _ => null
        };

    private static void SetMemberValue(object owner, MemberInfo member, object? value)
    {
        switch (member)
        {
            case PropertyInfo { CanWrite: true } property:
                property.SetValue(owner, value);
                break;
            case FieldInfo field:
                field.SetValue(owner, value);
                break;
            default:
                throw new MissingMemberException(owner.GetType().FullName, member.Name);
        }
    }

    private abstract record ActionPayload;

    private sealed record TestActionPayload : ActionPayload;

    private sealed record InventoryTagActionPayload(
        string Operation,
        string TagName,
        string TagColor,
        IReadOnlyList<TagActionItem> Items) : ActionPayload;

    private sealed record TagActionItem(
        string ItemId,
        string PublicInstanceKey,
        string TemplateId,
        string? ParentId,
        string? SlotId,
        string LocationFingerprint,
        string DisplayName,
        string? OldTagName,
        string? OldTagColor);

    private sealed record InventoryItemNode(
        string Id,
        string TemplateId,
        string? ParentId,
        string? SlotId,
        string LocationFingerprint,
        JsonObject? Upd);

    private sealed record InventorySnapshot(
        IReadOnlyList<InventoryItemNode> Items,
        IReadOnlyDictionary<string, InventoryItemNode> ById);

    private sealed record PendingAction(
        string SessionKey,
        string Fingerprint,
        HermesActionProposal Proposal,
        ActionPayload Payload,
        bool Resolved,
        bool Resolving,
        HermesActionHistoryEntry? HistoryEntry);
}
