// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>Lifecycle phase of a sandbox. Values match the wire enum.</summary>
public enum SandboxPhase
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Resources are being created.</summary>
    Provisioning = 1,
    /// <summary>Running and usable.</summary>
    Ready = 2,
    /// <summary>Failed.</summary>
    Error = 3,
    /// <summary>Being deleted.</summary>
    Deleting = 4,
    /// <summary>The gateway cannot determine the phase.</summary>
    Unknown = 5,
    /// <summary>Being stopped.</summary>
    Stopping = 6,
    /// <summary>Stopped; can be started again.</summary>
    Stopped = 7,
    /// <summary>Being started.</summary>
    Starting = 8,
    /// <summary>The main process finished and will not restart.</summary>
    Completed = 9,
}

/// <summary>Restart behaviour for the sandbox main process.</summary>
public enum SandboxRestartPolicy
{
    /// <summary>Gateway default.</summary>
    Unspecified = 0,
    /// <summary>Never restart.</summary>
    Never = 1,
    /// <summary>Restart after a failure.</summary>
    OnFailure = 2,
    /// <summary>Always restart.</summary>
    Always = 3,
}

/// <summary>How the gateway treats the Authorization header toward an exposed service.</summary>
public enum ServiceAuthorizationMode
{
    /// <summary>Gateway default (strip).</summary>
    Unspecified = 0,
    /// <summary>Remove Authorization before forwarding.</summary>
    Strip = 1,
    /// <summary>Forward one valid bearer Authorization header unchanged.</summary>
    BearerPassthrough = 2,
}

/// <summary>Result of a delete request.</summary>
public enum DeletionOutcome
{
    /// <summary>Not reported; does not establish completion.</summary>
    Unspecified = 0,
    /// <summary>Deleted.</summary>
    Completed = 1,
    /// <summary>Cleanup is pending.</summary>
    Accepted = 2,
    /// <summary>The target did not exist and <c>AllowMissing</c> was set.</summary>
    AlreadyAbsent = 3,
}

/// <summary>Typed result of a delete. A boolean would hide the pending-cleanup case.</summary>
public sealed record DeletionResult(DeletionOutcome Outcome, string? ResourceId = null)
{
    /// <summary>True when the gateway confirmed the resource is gone.</summary>
    public bool IsComplete => Outcome is DeletionOutcome.Completed or DeletionOutcome.AlreadyAbsent;
}

/// <summary>Registers a loopback HTTP service during sandbox creation.</summary>
public sealed record ServiceExposure(string Name, uint TargetPort, ServiceAuthorizationMode AuthorizationMode = ServiceAuthorizationMode.Unspecified);

/// <summary>One status condition of a sandbox.</summary>
public sealed record SandboxCondition(string Type, string Status, string Reason, string Message, DateTimeOffset? TransitionTime);

/// <summary>Per-sandbox workload template fields.</summary>
public sealed record SandboxTemplateSpec
{
    /// <summary>Container image.</summary>
    public string? Image { get; init; }

    /// <summary>Runtime class (for example <c>gvisor</c>).</summary>
    public string? RuntimeClassName { get; init; }

    /// <summary>Labels applied to the workload.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Annotations applied to the workload.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Environment for the workload.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Enable user namespace isolation.</summary>
    public bool? UserNamespaces { get; init; }

    /// <summary>Resource requests and limits as JSON, merged into the workload.</summary>
    public string? ResourcesJson { get; init; }

    /// <summary>Compute-driver-specific configuration as JSON.</summary>
    public string? DriverConfigJson { get; init; }
}

/// <summary>Desired state of a sandbox.</summary>
public sealed record SandboxSpec
{
    /// <summary>Shorthand for <see cref="Template"/>.<see cref="SandboxTemplateSpec.Image"/>.</summary>
    public string? Image { get; init; }

    /// <summary>Full workload template.</summary>
    public SandboxTemplateSpec? Template { get; init; }

    /// <summary>Main-process command; empty uses the image default.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>Sandbox-wide environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Attached provider names.</summary>
    public IReadOnlyList<string>? Providers { get; init; }

    /// <summary>Log level for the sandbox supervisor.</summary>
    public string? LogLevel { get; init; }

    /// <summary>Allocate a TTY for the main process.</summary>
    public bool Tty { get; init; }

    /// <summary>Restart behaviour.</summary>
    public SandboxRestartPolicy RestartPolicy { get; init; }

    /// <summary>
    /// Create-time safety boundary (filesystem, landlock, process and initial network policy).
    /// This is the generated wire type; build it directly or through <c>PolicyBuilder</c>.
    /// </summary>
    public SandboxPolicy? Policy { get; init; }

    /// <summary>Services to expose at creation.</summary>
    public IReadOnlyList<ServiceExposure>? ServiceExposures { get; init; }

    /// <summary>Adjusts the assembled wire spec for fields the curated shape does not surface.</summary>
    public Action<Openshell.V1.SandboxSpec>? RawSpec { get; init; }
}

/// <summary>A sandbox as reported by the gateway.</summary>
public sealed record Sandbox
{
    /// <summary>Immutable identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical name within the workspace.</summary>
    public required string Name { get; init; }

    /// <summary>Owning workspace.</summary>
    public required string Workspace { get; init; }

    /// <summary>Current phase.</summary>
    public SandboxPhase Phase { get; init; }

    /// <summary>User labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = EmptyMap;

    /// <summary>User annotations.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = EmptyMap;

    /// <summary>Optimistic-concurrency version.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Deletion time, when deletion is in progress.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Exposed service URLs by service name; the unnamed service uses the empty key.</summary>
    public IReadOnlyDictionary<string, string> ServiceUrls { get; init; } = EmptyMap;

    /// <summary>Status conditions.</summary>
    public IReadOnlyList<SandboxCondition> Conditions { get; init; } = [];

    /// <summary>Exit code of the main process once it finished.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Currently active policy version.</summary>
    public uint CurrentPolicyVersion { get; init; }

    /// <summary>Number of main-process restarts.</summary>
    public uint RestartCount { get; init; }

    /// <summary>SSH host key fingerprint, when known.</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>Image from the desired spec, when set.</summary>
    public string? Image { get; init; }

    internal static IReadOnlyDictionary<string, string> EmptyMap { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

/// <summary>Options for <see cref="ISandboxClient.CreateAsync"/>.</summary>
public sealed record CreateSandboxOptions
{
    /// <summary>Labels to attach.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Annotations to attach.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Wait for the main process to attach before returning.</summary>
    public bool AwaitMainProcessAttachment { get; init; }

    /// <summary>Idempotency key; a retry with the same key returns the original result.</summary>
    public string? RequestId { get; init; }
}

/// <summary>Options for deleting a resource.</summary>
public sealed record DeleteOptions
{
    /// <summary>Succeed with <see cref="DeletionOutcome.AlreadyAbsent"/> when the target is missing.</summary>
    public bool AllowMissing { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}

/// <summary>Options for the polling waits.</summary>
public sealed record WaitOptions
{
    /// <summary>Overall timeout; defaults to two minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>First poll delay; doubles up to <see cref="MaxPollInterval"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound for the poll delay.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>For delete waits: finish once the name resolves to a different sandbox id.</summary>
    public string? ExpectedSandboxId { get; init; }
}
