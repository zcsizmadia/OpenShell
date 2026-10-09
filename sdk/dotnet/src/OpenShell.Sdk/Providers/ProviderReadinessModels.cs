// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>A timestamp that keeps nanosecond precision, which <see cref="DateTimeOffset"/> cannot.</summary>
public readonly record struct ExactTimestamp(long Seconds, int Nanos)
{
    /// <summary>Converts to a <see cref="DateTimeOffset"/>, truncating to 100 ns ticks.</summary>
    public DateTimeOffset ToDateTimeOffset() =>
        DateTimeOffset.UnixEpoch.AddSeconds(Seconds).AddTicks(Nanos / 100);
}

/// <summary>Operation whose installed authority a receipt tracks.</summary>
public enum ProviderMutationKind
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>A provider was attached.</summary>
    Attach = 1,
    /// <summary>A provider was detached.</summary>
    Detach = 2,
    /// <summary>A provider's credentials or config changed.</summary>
    Update = 3,
    /// <summary>Reconstructed status for existing desired state without a mutation receipt.</summary>
    Observe = 4,
}

/// <summary>Persisted intent is tracked separately from installed state.</summary>
public enum ProviderReadinessState
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Saved but not yet observed.</summary>
    Persisted = 1,
    /// <summary>Waiting for the sandbox to install it.</summary>
    Pending = 2,
    /// <summary>Installed in the sandbox.</summary>
    Ready = 3,
    /// <summary>Credentials are withheld from the sandbox.</summary>
    Withheld = 4,
    /// <summary>Removed from the sandbox.</summary>
    Revoked = 5,
    /// <summary>Installation failed.</summary>
    Failed = 6,
    /// <summary>A later mutation replaced this authority.</summary>
    Superseded = 7,
}

/// <summary>Closed reason categories, safe to display.</summary>
public enum ProviderReadinessReason
{
    /// <summary>No reason: complete.</summary>
    Unspecified = 0,
    /// <summary>Waiting for the supervisor to connect.</summary>
    WaitingForSupervisor = 1,
    /// <summary>Waiting for credential installation.</summary>
    WaitingForCredentials = 2,
    /// <summary>Waiting for policy activation.</summary>
    WaitingForPolicy = 3,
    /// <summary>Waiting for the process environment.</summary>
    WaitingForProcess = 4,
    /// <summary>The supervisor does not support the required protocol.</summary>
    UnsupportedSupervisor = 5,
    /// <summary>Credentials are withheld.</summary>
    CredentialsWithheld = 6,
    /// <summary>Credential installation failed.</summary>
    CredentialInstallFailed = 7,
    /// <summary>Policy activation failed.</summary>
    PolicyActivationFailed = 8,
    /// <summary>Process environment installation failed.</summary>
    ProcessInstallFailed = 9,
    /// <summary>The supervisor disconnected.</summary>
    SupervisorDisconnected = 10,
    /// <summary>The supervisor lease expired.</summary>
    SupervisorLeaseExpired = 11,
    /// <summary>The desired state changed.</summary>
    DesiredStateChanged = 12,
    /// <summary>A credential expired.</summary>
    CredentialExpired = 13,
    /// <summary>Local policy overrides it.</summary>
    LocalPolicy = 14,
    /// <summary>The installed snapshot differs from desired.</summary>
    SnapshotMismatch = 15,
}

/// <summary>Lifecycle of one desired-state update operation.</summary>
public enum ConfigUpdateOperationState
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>In progress.</summary>
    Pending = 1,
    /// <summary>Applied.</summary>
    Applied = 2,
    /// <summary>Inactive.</summary>
    Inactive = 3,
    /// <summary>Failed.</summary>
    Failed = 4,
    /// <summary>Replaced by a later operation.</summary>
    Superseded = 5,
    /// <summary>Cancelled.</summary>
    Cancelled = 6,
}

/// <summary>Result of applying a configuration revision at its owning runtime boundary.</summary>
public enum ConfigApplyOutcome
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Applied.</summary>
    Applied = 1,
    /// <summary>Ignored as a duplicate.</summary>
    IgnoredDuplicate = 2,
    /// <summary>Ignored as stale.</summary>
    IgnoredStale = 3,
    /// <summary>A local override was retained.</summary>
    RetainedLocalOverride = 4,
    /// <summary>Applied in a degraded form.</summary>
    Degraded = 5,
    /// <summary>Failed; the last known good configuration was retained.</summary>
    FailedRetainedLastKnownGood = 6,
    /// <summary>Failed closed.</summary>
    FailedClosed = 7,
    /// <summary>Unsupported.</summary>
    Unsupported = 8,
}

/// <summary>Exact desired authority. Revisions are opaque identities, never ordered.</summary>
public sealed record ProviderDesiredIdentity
{
    /// <summary>Sandbox id.</summary>
    public string SandboxId { get; init; } = "";

    /// <summary>Sandbox name.</summary>
    public string Sandbox { get; init; } = "";

    /// <summary>Attachment epoch; may be empty for an initial attachment.</summary>
    public string AttachmentEpoch { get; init; } = "";

    /// <summary>Provider id; empty for a detached provider.</summary>
    public string ProviderId { get; init; } = "";

    /// <summary>Provider resource version.</summary>
    public ulong ProviderResourceVersion { get; init; }

    /// <summary>Provider environment revision fingerprint.</summary>
    public ulong ProviderEnvRevision { get; init; }

    /// <summary>Sandbox configuration revision fingerprint.</summary>
    public ulong ConfigRevision { get; init; }

    /// <summary>Policy hash.</summary>
    public string PolicyHash { get; init; } = "";
}

/// <summary>Immutable, secret-free record of one sandbox's intended provider mutation.</summary>
public sealed record ProviderMutationReceipt
{
    /// <summary>Receipt id.</summary>
    public string ReceiptId { get; init; } = "";

    /// <summary>Shared by all sandbox receipts from one provider update.</summary>
    public string MutationId { get; init; } = "";

    /// <summary>Provider name.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Workspace.</summary>
    public string Workspace { get; init; } = "";

    /// <summary>Mutation kind.</summary>
    public ProviderMutationKind Kind { get; init; }

    /// <summary>Desired authority.</summary>
    public ProviderDesiredIdentity? Desired { get; init; }

    /// <summary>Persistence time with nanosecond precision.</summary>
    public ExactTimestamp? PersistedTime { get; init; }
}

/// <summary>Installed state reported by the current supervisor.</summary>
public sealed record ProviderReadinessObservation
{
    /// <summary>Supervisor session id.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>Sequence, monotonic only within one connection.</summary>
    public ulong Sequence { get; init; }

    /// <summary>Attachment epoch.</summary>
    public string AttachmentEpoch { get; init; } = "";

    /// <summary>Provider environment revision.</summary>
    public ulong ProviderEnvRevision { get; init; }

    /// <summary>Configuration revision.</summary>
    public ulong ConfigRevision { get; init; }

    /// <summary>Policy hash.</summary>
    public string PolicyHash { get; init; } = "";

    /// <summary>Whether credentials are installed.</summary>
    public bool CredentialsInstalled { get; init; }

    /// <summary>Whether the policy is active.</summary>
    public bool PolicyActive { get; init; }

    /// <summary>Whether the launch environment is installed.</summary>
    public bool LaunchEnvironmentInstalled { get; init; }

    /// <summary>Process instance id.</summary>
    public string ProcessInstanceId { get; init; } = "";

    /// <summary>Reason the supervisor reported, if not complete.</summary>
    public ProviderReadinessReason Reason { get; init; }
}

/// <summary>Durable progress of the operation behind a receipt.</summary>
public sealed record ProviderOperation(
    string OperationId,
    ConfigUpdateOperationState State,
    ConfigApplyOutcome Outcome,
    string SanitizedError);

/// <summary>Operator view of desired and observed state; contains no credential material.</summary>
public sealed record ProviderReadinessStatus
{
    /// <summary>The receipt this status belongs to.</summary>
    public ProviderMutationReceipt? Receipt { get; init; }

    /// <summary>Overall state.</summary>
    public ProviderReadinessState State { get; init; }

    /// <summary>Reason the state is not yet <see cref="ProviderReadinessState.Ready"/> or <see cref="ProviderReadinessState.Revoked"/>.</summary>
    public ProviderReadinessReason Reason { get; init; }

    /// <summary>Latest supervisor observation.</summary>
    public ProviderReadinessObservation? Observed { get; init; }

    /// <summary>Network instance id.</summary>
    public string NetworkInstanceId { get; init; } = "";

    /// <summary>When the supervisor evidence was accepted; null until a report arrives.</summary>
    public DateTimeOffset? ObservedTime { get; init; }

    /// <summary>When the gateway evaluated this status.</summary>
    public DateTimeOffset? EvaluatedTime { get; init; }

    /// <summary>The durable operation for the receipt.</summary>
    public ProviderOperation? Operation { get; init; }
}

/// <summary>Why a bounded provider wait returned.</summary>
public enum ProviderWaitOutcome
{
    /// <summary>The original receipt's authority was installed or revoked as requested.</summary>
    Complete,
    /// <summary>The deadline elapsed; the last observed state remains authoritative.</summary>
    TimedOut,
    /// <summary>Installation failed, was withheld, or the desired authority was superseded.</summary>
    Terminal,
}

/// <summary>Last safe status and the reason a provider wait returned.</summary>
public sealed record ProviderWaitResult(ProviderReadinessStatus Status, ProviderWaitOutcome Outcome);

/// <summary>The result of attaching a provider to a sandbox.</summary>
/// <param name="Sandbox">The sandbox after the mutation.</param>
/// <param name="Attached">True when newly attached; false when already attached.</param>
/// <param name="Receipt">Persisted intent; readiness requires current supervisor observations.</param>
public sealed record AttachProviderResult(Sandbox Sandbox, bool Attached, ProviderMutationReceipt? Receipt);

/// <summary>The result of detaching a provider from a sandbox.</summary>
/// <param name="Sandbox">The sandbox after the mutation.</param>
/// <param name="Detached">True when removed; false when it was not attached.</param>
/// <param name="Receipt">Revocation is complete only when the receipt reaches <see cref="ProviderReadinessState.Revoked"/>.</param>
public sealed record DetachProviderResult(Sandbox Sandbox, bool Detached, ProviderMutationReceipt? Receipt);

/// <summary>Options for attach and detach.</summary>
public sealed record SandboxProviderMutationOptions
{
    /// <summary>Optimistic concurrency guard; 0 uses the current version.</summary>
    public ulong ExpectedResourceVersion { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}
