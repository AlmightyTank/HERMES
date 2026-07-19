using System.Collections.Concurrent;
using System.Security.Cryptography;
using Hermes.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesActionService
{
    private const int ProposalTtlSeconds = 90;
    private const int DuplicateProposalWindowSeconds = 12;
    private const int MaximumHistoryEntriesPerSession = 24;

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
            if (_pendingByFingerprint.TryGetValue(fingerprint, out var existingId)
                && _pendingById.TryGetValue(existingId, out var existing)
                && existing.Proposal.ExpiresUnixTime > now
                && now - existing.Proposal.CreatedUnixTime <= DuplicateProposalWindowSeconds)
            {
                return new HermesActionProposalResponse(
                    true,
                    "Reused the existing pending test-action proposal instead of creating a duplicate request.",
                    WithRemaining(existing.Proposal, now));
            }

            var proposalId = CreateToken(16);
            var confirmationToken = CreateToken(24);
            var proposal = new HermesActionProposal(
                proposalId,
                confirmationToken,
                actionKind,
                "Run harmless HERMES test action",
                true,
                true,
                true,
                now,
                now + ProposalTtlSeconds,
                ProposalTtlSeconds,
                new HermesActionPreview(
                    "Harmless action pipeline verification",
                    ["No profile inventory item will be modified"],
                    "1 dry-run confirmation",
                    "0 roubles",
                    "HERMES internal action pipeline",
                    "A success message and history row are created; no SPT profile data is changed.",
                    [
                        "Alpha1 does not execute real buy, sell, move, craft, hideout, or profile-changing actions.",
                        "This token expires quickly and can be used only once."
                    ],
                    null));

            _pendingById[proposalId] = new PendingAction(sessionKey, fingerprint, proposal, false, null);
            _pendingByFingerprint[fingerprint] = proposalId;
            return new HermesActionProposalResponse(true, null, proposal);
        }
    }

    public HermesActionResultResponse Confirm(MongoId sessionId, string proposalId, string token)
        => Resolve(sessionId, proposalId, token, cancel: false);

    public HermesActionResultResponse Cancel(MongoId sessionId, string proposalId, string token)
        => Resolve(sessionId, proposalId, token, cancel: true);

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

    private HermesActionResultResponse Resolve(
        MongoId sessionId,
        string proposalId,
        string token,
        bool cancel)
    {
        var now = Now();
        var sessionKey = sessionId.ToString();
        proposalId = (proposalId ?? string.Empty).Trim();
        token = (token ?? string.Empty).Trim();

        lock (_sync)
        {
            if (!_pendingById.TryGetValue(proposalId, out var pending)
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

            var successEntry = CreateHistoryEntry(
                proposal,
                "Succeeded",
                "Harmless alpha1 test action confirmed. The confirmation pipeline worked and no inventory/profile action was performed.",
                now,
                executed: true);
            StoreResolved(pending, successEntry);
            return new HermesActionResultResponse(
                true,
                true,
                false,
                false,
                false,
                "Succeeded",
                successEntry.Message,
                proposal,
                successEntry);
        }
    }

    private void StoreResolved(PendingAction pending, HermesActionHistoryEntry entry)
    {
        var resolved = pending with { Resolved = true, HistoryEntry = entry };
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
            if (pair.Value.Resolved)
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

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string CreateToken(int byteCount)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private sealed record PendingAction(
        string SessionKey,
        string Fingerprint,
        HermesActionProposal Proposal,
        bool Resolved,
        HermesActionHistoryEntry? HistoryEntry);
}
