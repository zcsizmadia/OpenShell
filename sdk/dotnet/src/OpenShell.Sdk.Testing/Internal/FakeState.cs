// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Testing;

/// <summary>Identifies a workspace-scoped resource.</summary>
internal readonly record struct Key(string Workspace, string Name);

internal sealed class WorkspaceEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public WorkspacePhase Phase { get; set; } = WorkspacePhase.Active;

    public Dictionary<string, string> Labels { get; init; } = [with(StringComparer.Ordinal)];

    public ulong ResourceVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, WorkspaceMember> Members { get; } = [with(StringComparer.Ordinal)];
}

internal sealed class SandboxEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Workspace { get; init; }

    public SandboxPhase Phase { get; set; }

    public SandboxPhase? NextPhase { get; set; }

    public DateTimeOffset? NextAt { get; set; }

    public bool RemoveOnTransition { get; set; }

    public Dictionary<string, string> Labels { get; init; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, string> Annotations { get; init; } = [with(StringComparer.Ordinal)];

    public ulong ResourceVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; set; }

    public SandboxSpec Spec { get; set; } = new();

    public List<string> Providers { get; } = [];

    public int? ExitCode { get; set; }

    public int RestartCount { get; set; }

    public string AttachmentEpoch { get; set; } = "";

    // Configuration and policy.
    public WireSandbox.SandboxPolicy? CreatePolicy { get; set; }

    public WireSandbox.SandboxPolicy Policy { get; set; } = new();

    public uint PolicyVersion { get; set; }

    public Dictionary<string, SettingValue> Settings { get; } = [with(StringComparer.Ordinal)];

    public List<PolicyRevisionEntry> Revisions { get; } = [];

    public ulong ConfigRevision { get; set; } = 1;

    public ulong ProviderEnvRevision { get; set; } = 1;

    // Draft policy review.
    public List<DraftChunkEntry> Chunks { get; } = [];

    public List<DraftHistoryEntry> History { get; } = [];

    public ulong DraftVersion { get; set; }

    // Watch stream, logs and events.
    public List<WatchRecord> Records { get; } = [];

    public long Sequence { get; set; }
}

internal sealed class PolicyRevisionEntry
{
    public required uint Version { get; init; }

    public required string Hash { get; init; }

    public required WireSandbox.SandboxPolicy Policy { get; init; }

    public PolicyLoadStatus Status { get; set; } = PolicyLoadStatus.Loaded;

    public string LoadError { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LoadedAt { get; set; }

    public Dictionary<string, string> Provenance { get; init; } = [with(StringComparer.Ordinal)];
}

internal sealed class DraftChunkEntry
{
    public required string Id { get; init; }

    public required string RuleName { get; init; }

    public required WireSandbox.NetworkPolicyRule Rule { get; set; }

    public string Status { get; set; } = "pending";

    public string Rationale { get; init; } = "";

    public string SecurityNotes { get; init; } = "";

    public string Binary { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string RejectionReason { get; set; } = "";

    public int Revision { get; set; }
}

internal enum WatchKind
{
    Status,
    Log,
    Event,
    Draft,
}

internal sealed class WatchRecord
{
    public required long Sequence { get; init; }

    public required WatchKind Kind { get; init; }

    public Sandbox? Sandbox { get; init; }

    public LogLine? Log { get; init; }

    public PlatformEvent? Event { get; init; }

    public DraftPolicyUpdate? Draft { get; init; }

    public DateTimeOffset Time { get; init; }

    public string Cursor => Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class TemplateEntry
{
    public required SandboxWorkloadTemplate Template { get; set; }
}

internal sealed class ProviderEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Workspace { get; init; }

    public required string Type { get; set; }

    public Dictionary<string, string> Credentials { get; set; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, string> Config { get; set; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, string> Labels { get; set; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, string> Annotations { get; set; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, DateTimeOffset> Expirations { get; set; } = [with(StringComparer.Ordinal)];

    public string ProfileWorkspace { get; set; } = "";

    public ulong ResourceVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class ReceiptEntry
{
    public required ProviderMutationReceipt Receipt { get; init; }

    public required string SandboxName { get; init; }

    public ProviderReadinessState? StateOverride { get; set; }

    public ProviderReadinessReason ReasonOverride { get; set; }
}

internal sealed class ServiceEntry
{
    public required string Workspace { get; init; }

    public required string Sandbox { get; init; }

    public required string SandboxId { get; init; }

    public required string Name { get; init; }

    public uint TargetPort { get; init; }

    public bool Domain { get; init; }

    public ServiceAuthorizationMode AuthorizationMode { get; init; }

    public ulong ResourceVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class SshSessionEntry
{
    public required string Workspace { get; init; }

    public required string Sandbox { get; init; }

    public required string Token { get; init; }
}

internal sealed class RefreshEntry
{
    public required string Provider { get; init; }

    public required string ProviderId { get; init; }

    public required string CredentialKey { get; init; }

    public RefreshStrategy Strategy { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastRefreshAt { get; set; }

    public int Rotations { get; set; }
}

internal sealed class StagedRootfs
{
    public required string Token { get; init; }

    public required string FileName { get; init; }

    public byte[]? Content { get; set; }

    public bool Consumed { get; set; }
}
