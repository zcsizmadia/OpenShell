// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>Load state of a policy revision.</summary>
public enum PolicyLoadStatus
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>The gateway received the update; the sandbox has not loaded it yet.</summary>
    Pending = 1,
    /// <summary>The sandbox applied this revision.</summary>
    Loaded = 2,
    /// <summary>The sandbox could not apply it; the last known good policy stays active.</summary>
    Failed = 3,
    /// <summary>A newer revision was stored before the sandbox loaded this one.</summary>
    Superseded = 4,
}

/// <summary>A versioned policy revision.</summary>
public sealed record PolicyRevision
{
    /// <summary>Version, increasing per sandbox.</summary>
    public uint Version { get; init; }

    /// <summary>SHA-256 hash of the canonical payload; empty for an invalid historical row.</summary>
    public string PolicyHash { get; init; } = "";

    /// <summary>Load state.</summary>
    public PolicyLoadStatus Status { get; init; }

    /// <summary>Load error or schema diagnostic.</summary>
    public string LoadError { get; init; } = "";

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Time the sandbox loaded it, when it did.</summary>
    public DateTimeOffset? LoadedAt { get; init; }

    /// <summary>The full policy; populated only when the gateway was asked for it.</summary>
    public WireSandbox.SandboxPolicy? Policy { get; init; }

    /// <summary>Immutable provenance supplied with the revision.</summary>
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = Sandbox.EmptyMap;
}

/// <summary>A policy revision plus the sandbox's active version.</summary>
/// <param name="Revision">The queried revision.</param>
/// <param name="ActiveVersion">The version currently loaded by the sandbox.</param>
public sealed record PolicyStatusResult(PolicyRevision? Revision, uint ActiveVersion);

/// <summary>Options for <see cref="IPolicyClient.GetStatusAsync"/>.</summary>
public sealed record PolicyStatusOptions
{
    /// <summary>The version to query; zero means the latest.</summary>
    public uint Version { get; init; }

    /// <summary>Query global policy revisions instead of the sandbox's own.</summary>
    public bool Global { get; init; }
}

/// <summary>Options for <see cref="IPolicyClient.ListRevisions"/>.</summary>
public sealed record PolicyListOptions
{
    /// <summary>Revisions per page; zero uses the gateway default.</summary>
    public int PageSize { get; init; }

    /// <summary>Resume token from an earlier page.</summary>
    public string? PageToken { get; init; }

    /// <summary>List global policy revisions instead of the sandbox's own.</summary>
    public bool Global { get; init; }
}

/// <summary>A proposed network policy rule awaiting review.</summary>
public sealed record DraftChunk
{
    /// <summary>Unique chunk id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Review state: <c>pending</c>, <c>approved</c> or <c>rejected</c>.</summary>
    public string Status { get; init; } = "";

    /// <summary>Proposed <c>network_policies</c> map key.</summary>
    public string RuleName { get; init; } = "";

    /// <summary>The proposed rule; this is the generated wire type.</summary>
    public WireSandbox.NetworkPolicyRule? ProposedRule { get; init; }

    /// <summary>Why the rule was proposed.</summary>
    public string Rationale { get; init; } = "";

    /// <summary>Security concerns flagged by analysis.</summary>
    public string SecurityNotes { get; init; } = "";

    /// <summary>Analysis confidence from 0 to 1; zero in mechanistic mode.</summary>
    public float Confidence { get; init; }

    /// <summary>Denial summaries that led to the chunk.</summary>
    public IReadOnlyList<string> DenialSummaryIds { get; init; } = [];

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Time of the approve or reject decision.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Recommendation stage: <c>initial</c> or <c>refined</c>.</summary>
    public string Stage { get; init; } = "";

    /// <summary>For a refined chunk, the initial chunk it replaces.</summary>
    public string SupersedesChunkId { get; init; } = "";

    /// <summary>How many times the endpoint was proposed across denial flush cycles.</summary>
    public int HitCount { get; init; }

    /// <summary>First time the endpoint was proposed.</summary>
    public DateTimeOffset? FirstSeenAt { get; init; }

    /// <summary>Most recent time the endpoint was proposed.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Binary path that triggered the denial.</summary>
    public string Binary { get; init; } = "";

    /// <summary>Gateway-side static check verdict.</summary>
    public string ValidationResult { get; init; } = "";

    /// <summary>Reviewer text given when the chunk was rejected.</summary>
    public string RejectionReason { get; init; } = "";

    /// <summary>Gateway-side preflight failure explaining why the chunk cannot be applied now.</summary>
    public string ApplicationError { get; init; } = "";

    /// <summary>Digest binding a review to the exact live inputs; pass it to approve.</summary>
    public string ReviewToken { get; init; } = "";

    /// <summary>Hash of the current effective policy.</summary>
    public string CurrentEffectivePolicyHash { get; init; } = "";

    /// <summary>Hash of the candidate effective policy.</summary>
    public string CandidateEffectivePolicyHash { get; init; } = "";

    /// <summary>The current effective policy used for review.</summary>
    public WireSandbox.SandboxPolicy? CurrentEffectivePolicy { get; init; }

    /// <summary>The candidate effective policy used for review.</summary>
    public WireSandbox.SandboxPolicy? CandidateEffectivePolicy { get; init; }
}

/// <summary>The draft policy of a sandbox.</summary>
/// <param name="Chunks">Proposed rules.</param>
/// <param name="RollingSummary">Summary of all analysis; empty in mechanistic mode.</param>
/// <param name="DraftVersion">Current draft version.</param>
/// <param name="LastAnalyzedAt">When the last analysis finished.</param>
public sealed record DraftPolicy(IReadOnlyList<DraftChunk> Chunks, string RollingSummary, ulong DraftVersion, DateTimeOffset? LastAnalyzedAt);

/// <summary>The effect of approving or undoing a chunk.</summary>
/// <param name="PolicyVersion">The new policy version.</param>
/// <param name="PolicyHash">Hash of the new policy.</param>
public sealed record PolicyChangeResult(uint PolicyVersion, string PolicyHash);

/// <summary>The effect of approving every pending chunk.</summary>
/// <param name="PolicyVersion">The new policy version.</param>
/// <param name="PolicyHash">Hash of the new policy.</param>
/// <param name="ChunksApproved">Chunks approved.</param>
/// <param name="ChunksSkipped">Chunks skipped for security flags, staleness or batch conflicts.</param>
public sealed record ApproveAllResult(uint PolicyVersion, string PolicyHash, uint ChunksApproved, uint ChunksSkipped);

/// <summary>One reviewed chunk and its token, for <see cref="ApproveAllOptions.Approvals"/>.</summary>
/// <param name="ChunkId">The chunk id.</param>
/// <param name="ReviewToken">The token returned with the reviewed chunk.</param>
public sealed record ChunkApproval(string ChunkId, string ReviewToken);

/// <summary>Options for <see cref="IPolicyClient.ApproveAllAsync"/>.</summary>
public sealed record ApproveAllOptions
{
    /// <summary>Include chunks that carry security notes; they are skipped by default.</summary>
    public bool IncludeSecurityFlagged { get; init; }

    /// <summary>The exact reviewed chunks and tokens to approve in one atomic write.</summary>
    public IReadOnlyList<ChunkApproval>? Approvals { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}

/// <summary>One entry of the draft decision history.</summary>
/// <param name="EventTime">When it happened.</param>
/// <param name="EventType">For example <c>approved</c>, <c>rejected</c>, <c>edited</c>, <c>undone</c> or <c>cleared</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="ChunkId">The chunk concerned, if any.</param>
public sealed record DraftHistoryEntry(DateTimeOffset? EventTime, string EventType, string Description, string ChunkId);
