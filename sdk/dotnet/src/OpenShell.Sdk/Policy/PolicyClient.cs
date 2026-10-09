// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>Draft policy review and policy revision history for sandboxes.</summary>
public interface IPolicyClient
{
    /// <summary>Gets the draft policy, optionally filtered by chunk status (<c>pending</c>, <c>approved</c>, <c>rejected</c>).</summary>
    ValueTask<DraftPolicy> GetDraftAsync(string sandbox, string? statusFilter = null, CancellationToken cancellationToken = default);

    /// <summary>Approves one chunk. Fails with a conflict when live inputs no longer match <paramref name="reviewToken"/>.</summary>
    ValueTask<PolicyChangeResult> ApproveChunkAsync(string sandbox, string chunkId, string reviewToken, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Rejects one chunk with an optional reason that is surfaced back to the in-sandbox agent.</summary>
    ValueTask RejectChunkAsync(string sandbox, string chunkId, string? reason = null, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Approves pending chunks in one write.</summary>
    ValueTask<ApproveAllResult> ApproveAllAsync(string sandbox, ApproveAllOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces the proposed rule of a pending chunk.</summary>
    ValueTask EditChunkAsync(string sandbox, string chunkId, WireSandbox.NetworkPolicyRule proposedRule, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Reverses an approval, removing the merged rule from the active policy.</summary>
    ValueTask<PolicyChangeResult> UndoChunkAsync(string sandbox, string chunkId, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Clears every pending chunk and returns how many were cleared.</summary>
    ValueTask<uint> ClearChunksAsync(string sandbox, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Gets the chronological draft decision history.</summary>
    ValueTask<IReadOnlyList<DraftHistoryEntry>> GetDraftHistoryAsync(string sandbox, CancellationToken cancellationToken = default);

    /// <summary>Gets the status of a policy revision and the sandbox's active version.</summary>
    ValueTask<PolicyStatusResult> GetStatusAsync(string sandbox, PolicyStatusOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Lists policy revisions lazily, newest first as returned by the gateway.</summary>
    Pager<PolicyRevision> ListRevisions(string sandbox, PolicyListOptions? options = null);
}

internal sealed class PolicyClient(ClientCore core, string workspace) : IPolicyClient
{
    public async ValueTask<DraftPolicy> GetDraftAsync(string sandbox, string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        var request = new Wire.GetDraftPolicyRequest { WorkspaceScope = ClientCore.Scope(workspace), Sandbox = sandbox, StatusFilter = statusFilter ?? "" };
        var r = await core.UnaryAsync(o => core.Grpc.GetDraftPolicyAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DraftPolicy(
            r.Chunks.Select(ToDomain).ToArray(),
            r.RollingSummary,
            r.DraftVersion,
            r.LastAnalyzedTime?.ToDateTimeOffset());
    }

    public async ValueTask<PolicyChangeResult> ApproveChunkAsync(string sandbox, string chunkId, string reviewToken, string? requestId = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewToken);
        var request = new Wire.ApproveDraftChunkRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            ChunkId = chunkId,
            ReviewToken = reviewToken,
            RequestId = requestId ?? "",
        };
        var r = await core.UnaryAsync(o => core.Grpc.ApproveDraftChunkAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new PolicyChangeResult(r.PolicyVersion, r.PolicyHash);
    }

    public async ValueTask RejectChunkAsync(string sandbox, string chunkId, string? reason = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        var request = new Wire.RejectDraftChunkRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            ChunkId = chunkId,
            Reason = reason ?? "",
            RequestId = requestId ?? "",
        };
        await core.UnaryAsync(o => core.Grpc.RejectDraftChunkAsync(request, o), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApproveAllResult> ApproveAllAsync(string sandbox, ApproveAllOptions? options = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        var request = new Wire.ApproveAllDraftChunksRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            IncludeSecurityFlagged = options?.IncludeSecurityFlagged ?? false,
            RequestId = options?.RequestId ?? "",
        };
        if (options?.Approvals is { } approvals)
        {
            foreach (var a in approvals)
            {
                request.Approvals.Add(new Wire.DraftChunkApproval { ChunkId = a.ChunkId, ReviewToken = a.ReviewToken });
            }
        }

        var r = await core.UnaryAsync(o => core.Grpc.ApproveAllDraftChunksAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new ApproveAllResult(r.PolicyVersion, r.PolicyHash, r.ChunksApproved, r.ChunksSkipped);
    }

    public async ValueTask EditChunkAsync(string sandbox, string chunkId, WireSandbox.NetworkPolicyRule proposedRule, string? requestId = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        ArgumentNullException.ThrowIfNull(proposedRule);
        var request = new Wire.EditDraftChunkRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            ChunkId = chunkId,
            ProposedRule = proposedRule,
            RequestId = requestId ?? "",
        };
        await core.UnaryAsync(o => core.Grpc.EditDraftChunkAsync(request, o), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PolicyChangeResult> UndoChunkAsync(string sandbox, string chunkId, string? requestId = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        var request = new Wire.UndoDraftChunkRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            ChunkId = chunkId,
            RequestId = requestId ?? "",
        };
        var r = await core.UnaryAsync(o => core.Grpc.UndoDraftChunkAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new PolicyChangeResult(r.PolicyVersion, r.PolicyHash);
    }

    public async ValueTask<uint> ClearChunksAsync(string sandbox, string? requestId = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        var request = new Wire.ClearDraftChunksRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            RequestId = requestId ?? "",
        };
        var r = await core.UnaryAsync(o => core.Grpc.ClearDraftChunksAsync(request, o), cancellationToken).ConfigureAwait(false);
        return r.ChunksCleared;
    }

    public async ValueTask<IReadOnlyList<DraftHistoryEntry>> GetDraftHistoryAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        var request = new Wire.GetDraftHistoryRequest { WorkspaceScope = ClientCore.Scope(workspace), Sandbox = sandbox };
        var r = await core.UnaryAsync(o => core.Grpc.GetDraftHistoryAsync(request, o), cancellationToken).ConfigureAwait(false);
        return r.Entries.Select(e => new DraftHistoryEntry(e.EventTime?.ToDateTimeOffset(), e.EventType, e.Description, e.ChunkId)).ToArray();
    }

    public async ValueTask<PolicyStatusResult> GetStatusAsync(string sandbox, PolicyStatusOptions? options = null, CancellationToken cancellationToken = default)
    {
        Require(sandbox);
        var request = new Wire.GetSandboxPolicyStatusRequest
        {
            Sandbox = sandbox,
            Version = options?.Version ?? 0,
            Global = options?.Global ?? false,
        };
        if (!request.Global)
        {
            request.WorkspaceScope = ClientCore.Scope(workspace);
        }

        var r = await core.UnaryAsync(o => core.Grpc.GetSandboxPolicyStatusAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new PolicyStatusResult(r.Revision is null ? null : ToDomain(r.Revision), r.ActiveVersion);
    }

    public Pager<PolicyRevision> ListRevisions(string sandbox, PolicyListOptions? options = null)
    {
        Require(sandbox);
        options ??= new PolicyListOptions();
        return new Pager<PolicyRevision>(
            async (token, ct) =>
            {
                var request = new Wire.ListSandboxPoliciesRequest
                {
                    Sandbox = sandbox,
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                    Global = options.Global,
                };
                if (!options.Global)
                {
                    request.WorkspaceScope = ClientCore.Scope(workspace);
                }

                var r = await core.UnaryAsync(o => core.Grpc.ListSandboxPoliciesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<PolicyRevision>(
                    r.Revisions.Select(ToDomain).ToArray(),
                    r.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    private static void Require(string sandbox) => ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);

    private static PolicyRevision ToDomain(Wire.SandboxPolicyRevision r) => new()
    {
        Version = r.Version,
        PolicyHash = r.PolicyHash,
        Status = (PolicyLoadStatus)(int)r.Status,
        LoadError = r.LoadError,
        CreatedAt = r.CreatedTime?.ToDateTimeOffset(),
        LoadedAt = r.LoadedTime?.ToDateTimeOffset(),
        Policy = r.Policy,
        Provenance = SandboxMapping.Copy(r.Provenance),
    };

    private static DraftChunk ToDomain(Wire.PolicyChunk c) => new()
    {
        Id = c.Id,
        Status = c.Status,
        RuleName = c.RuleName,
        ProposedRule = c.ProposedRule,
        Rationale = c.Rationale,
        SecurityNotes = c.SecurityNotes,
        Confidence = c.Confidence,
        DenialSummaryIds = c.DenialSummaryIds.ToArray(),
        CreatedAt = c.CreatedTime?.ToDateTimeOffset(),
        DecidedAt = c.DecidedTime?.ToDateTimeOffset(),
        Stage = c.Stage,
        SupersedesChunkId = c.SupersedesChunkId,
        HitCount = c.HitCount,
        FirstSeenAt = c.FirstSeenTime?.ToDateTimeOffset(),
        LastSeenAt = c.LastSeenTime?.ToDateTimeOffset(),
        Binary = c.Binary,
        ValidationResult = c.ValidationResult,
        RejectionReason = c.RejectionReason,
        ApplicationError = c.ApplicationError,
        ReviewToken = c.ReviewToken,
        CurrentEffectivePolicyHash = c.CurrentEffectivePolicyHash,
        CandidateEffectivePolicyHash = c.CandidateEffectivePolicyHash,
        CurrentEffectivePolicy = c.CurrentEffectivePolicy,
        CandidateEffectivePolicy = c.CandidateEffectivePolicy,
    };
}
