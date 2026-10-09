// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    /// <summary>
    /// Adds a proposed network rule to a sandbox's draft policy, as the sandbox's denial analysis would. Returns the chunk id.
    /// A non-empty <paramref name="securityNotes"/> marks the chunk as security flagged.
    /// </summary>
    public string ProposeChunk(
        string sandbox,
        string ruleName,
        Openshell.Sandbox.V1.NetworkPolicyRule rule,
        string rationale = "",
        string securityNotes = "",
        string binary = "",
        string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        ArgumentNullException.ThrowIfNull(rule);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            var chunk = new DraftChunkEntry
            {
                Id = NextId("chunk"),
                RuleName = ruleName,
                Rule = rule.Clone(),
                Rationale = rationale,
                SecurityNotes = securityNotes,
                Binary = binary,
                CreatedAt = Time.GetUtcNow(),
            };
            entry.Chunks.Add(chunk);
            entry.DraftVersion++;
            var pending = (uint)entry.Chunks.Count(c => c.Status == "pending");
            AddRecord(entry, WatchKind.Draft, draft: new DraftPolicyUpdate(entry.DraftVersion, 1, pending, $"proposed rule '{ruleName}'"));
            return chunk.Id;
        }
    }
}

internal sealed class FakePolicyClient(FakeGateway gateway, string workspace) : IPolicyClient
{
    public ValueTask<DraftPolicy> GetDraftAsync(string sandbox, string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Policy.GetDraft", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var chunks = entry.Chunks
                .Where(c => string.IsNullOrEmpty(statusFilter) || string.Equals(c.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
                .Select(c => ToDomain(entry, c))
                .ToArray();
            return new DraftPolicy(chunks, "", entry.DraftVersion, null);
        }));
    }

    public ValueTask<PolicyChangeResult> ApproveChunkAsync(string sandbox, string chunkId, string reviewToken, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewToken);
        return ValueTask.FromResult(gateway.Run("Policy.ApproveChunk", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var chunk = Pending(entry, chunkId);
            return string.Equals(ReviewToken(entry, chunk), reviewToken, StringComparison.Ordinal)
                ? ApplyApproved(entry, [chunk])
                : throw FakeGateway.Conflict("the review token is stale; fetch the draft again");
        }));
    }

    public ValueTask RejectChunkAsync(string sandbox, string chunkId, string? reason = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        gateway.Run("Policy.RejectChunk", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var chunk = Pending(entry, chunkId);
            chunk.Status = "rejected";
            chunk.RejectionReason = reason ?? "";
            chunk.DecidedAt = gateway.Time.GetUtcNow();
            Record(entry, "rejected", $"rejected rule '{chunk.RuleName}'", chunk.Id);
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<ApproveAllResult> ApproveAllAsync(string sandbox, ApproveAllOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Policy.ApproveAll", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var pending = entry.Chunks.Where(c => c.Status == "pending").ToArray();
            var approve = new List<DraftChunkEntry>();
            foreach (var chunk in pending)
            {
                var eligible = options?.Approvals is { } approvals
                    ? approvals.Any(a => a.ChunkId == chunk.Id && a.ReviewToken == ReviewToken(entry, chunk))
                    : chunk.SecurityNotes.Length == 0 || options?.IncludeSecurityFlagged == true;
                if (eligible)
                {
                    approve.Add(chunk);
                }
            }

            var skipped = (uint)(pending.Length - approve.Count);
            if (approve.Count == 0)
            {
                return new ApproveAllResult(entry.PolicyVersion, FakeGateway.HashPolicy(entry.Policy), 0, skipped);
            }

            var change = ApplyApproved(entry, approve);
            return new ApproveAllResult(change.PolicyVersion, change.PolicyHash, (uint)approve.Count, skipped);
        }));
    }

    public ValueTask EditChunkAsync(string sandbox, string chunkId, WireSandbox.NetworkPolicyRule proposedRule, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        ArgumentNullException.ThrowIfNull(proposedRule);
        gateway.Run("Policy.EditChunk", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var chunk = Pending(entry, chunkId);
            chunk.Rule = proposedRule.Clone();
            chunk.Revision++;
            entry.DraftVersion++;
            Record(entry, "edited", $"edited rule '{chunk.RuleName}'", chunk.Id);
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<PolicyChangeResult> UndoChunkAsync(string sandbox, string chunkId, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        return ValueTask.FromResult(gateway.Run("Policy.UndoChunk", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var chunk = Find(entry, chunkId);
            if (chunk.Status != "approved")
            {
                throw FakeGateway.Conflict($"chunk '{chunkId}' is {chunk.Status}, not approved");
            }

            var policy = entry.Policy.Clone();
            policy.NetworkPolicies.Remove(chunk.RuleName);
            var revision = gateway.AddRevision(entry, policy, new Dictionary<string, string> { ["undo"] = chunk.Id });
            chunk.Status = "pending";
            chunk.DecidedAt = null;
            chunk.Revision++;
            Record(entry, "undone", $"undid rule '{chunk.RuleName}'", chunk.Id);
            gateway.Touch(entry);
            return new PolicyChangeResult(revision.Version, revision.Hash);
        }));
    }

    public ValueTask<uint> ClearChunksAsync(string sandbox, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Policy.ClearChunks", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var count = (uint)entry.Chunks.Count;
            entry.Chunks.Clear();
            Record(entry, "cleared", $"cleared {count.ToString(CultureInfo.InvariantCulture)} chunks", "");
            return count;
        }));
    }

    public ValueTask<IReadOnlyList<DraftHistoryEntry>> GetDraftHistoryAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run<IReadOnlyList<DraftHistoryEntry>>("Policy.GetDraftHistory", workspace, sandbox, cancellationToken, () =>
            [.. gateway.FindSandbox(workspace, sandbox).History]));
    }

    public ValueTask<PolicyStatusResult> GetStatusAsync(string sandbox, PolicyStatusOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Policy.GetStatus", workspace, sandbox, cancellationToken, () =>
        {
            var revisions = Revisions(gateway.FindSandbox(workspace, sandbox), options?.Global == true);
            var active = revisions.Where(r => r.Status == PolicyLoadStatus.Loaded).Select(r => r.Version).DefaultIfEmpty().Max();
            var version = options?.Version ?? 0;
            if (version == 0)
            {
                return new PolicyStatusResult(revisions.Count == 0 ? null : ToDomain(revisions[^1], includePolicy: true), active);
            }

            var found = revisions.FirstOrDefault(r => r.Version == version)
                ?? throw FakeGateway.NotFound("policy revision", version.ToString(CultureInfo.InvariantCulture));
            return new PolicyStatusResult(ToDomain(found, includePolicy: true), active);
        }));
    }

    public Pager<PolicyRevision> ListRevisions(string sandbox, PolicyListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        options ??= new PolicyListOptions();
        return FakePaging.Create(
            gateway,
            "Policy.ListRevisions",
            workspace,
            sandbox,
            options.PageSize,
            options.PageToken,
            () => Revisions(gateway.FindSandbox(workspace, sandbox), options.Global).OrderByDescending(r => r.Version).Select(r => ToDomain(r, includePolicy: false)).ToList());
    }

    private List<PolicyRevisionEntry> Revisions(SandboxEntry entry, bool global) => global ? gateway.GlobalRevisions : entry.Revisions;

    private static string ReviewToken(SandboxEntry entry, DraftChunkEntry chunk) =>
        string.Create(CultureInfo.InvariantCulture, $"rt-{chunk.Id}-{chunk.Revision}-{entry.PolicyVersion}");

    private static DraftChunkEntry Find(SandboxEntry entry, string chunkId) =>
        entry.Chunks.FirstOrDefault(c => c.Id == chunkId) ?? throw FakeGateway.NotFound("draft chunk", chunkId);

    private static DraftChunkEntry Pending(SandboxEntry entry, string chunkId)
    {
        var chunk = Find(entry, chunkId);
        return chunk.Status == "pending" ? chunk : throw FakeGateway.Conflict($"chunk '{chunkId}' is already {chunk.Status}");
    }

    private void Record(SandboxEntry entry, string type, string description, string chunkId)
    {
        entry.History.Add(new DraftHistoryEntry(gateway.Time.GetUtcNow(), type, description, chunkId));
        entry.DraftVersion++;
    }

    /// <summary>Approves chunks atomically: one new policy revision containing every approved rule.</summary>
    private PolicyChangeResult ApplyApproved(SandboxEntry entry, List<DraftChunkEntry> chunks)
    {
        var policy = entry.Policy.Clone();
        foreach (var chunk in chunks)
        {
            policy.NetworkPolicies[chunk.RuleName] = chunk.Rule.Clone();
        }

        var revision = gateway.AddRevision(entry, policy, new Dictionary<string, string> { ["approved"] = string.Join(',', chunks.Select(c => c.Id)) });
        foreach (var chunk in chunks)
        {
            chunk.Status = "approved";
            chunk.DecidedAt = gateway.Time.GetUtcNow();
            Record(entry, "approved", $"approved rule '{chunk.RuleName}'", chunk.Id);
        }

        gateway.Touch(entry);
        return new PolicyChangeResult(revision.Version, revision.Hash);
    }

    private static PolicyRevision ToDomain(PolicyRevisionEntry r, bool includePolicy) => new()
    {
        Version = r.Version,
        PolicyHash = r.Hash,
        Status = r.Status,
        LoadError = r.LoadError,
        CreatedAt = r.CreatedAt,
        LoadedAt = r.LoadedAt,
        Policy = includePolicy ? r.Policy.Clone() : null,
        Provenance = new Dictionary<string, string>(r.Provenance),
    };

    private static DraftChunk ToDomain(SandboxEntry entry, DraftChunkEntry c)
    {
        var candidate = entry.Policy.Clone();
        candidate.NetworkPolicies[c.RuleName] = c.Rule.Clone();
        return new DraftChunk
        {
            Id = c.Id,
            Status = c.Status,
            RuleName = c.RuleName,
            ProposedRule = c.Rule.Clone(),
            Rationale = c.Rationale,
            SecurityNotes = c.SecurityNotes,
            CreatedAt = c.CreatedAt,
            DecidedAt = c.DecidedAt,
            Stage = "initial",
            HitCount = 1,
            FirstSeenAt = c.CreatedAt,
            LastSeenAt = c.CreatedAt,
            Binary = c.Binary,
            ValidationResult = "ok",
            RejectionReason = c.RejectionReason,
            ReviewToken = ReviewToken(entry, c),
            CurrentEffectivePolicyHash = FakeGateway.HashPolicy(entry.Policy),
            CandidateEffectivePolicyHash = FakeGateway.HashPolicy(candidate),
            CurrentEffectivePolicy = entry.Policy.Clone(),
            CandidateEffectivePolicy = candidate,
        };
    }
}
