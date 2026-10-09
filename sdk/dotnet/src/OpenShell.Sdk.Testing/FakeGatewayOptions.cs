// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace OpenShell.Testing;

/// <summary>Configures a <see cref="FakeGateway"/>.</summary>
public sealed record FakeGatewayOptions
{
    /// <summary>
    /// Clock for timestamps, phase transitions and waits. Pass a <c>FakeTimeProvider</c> to make time-driven
    /// behaviour deterministic. Defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Workspace used by clients created without an explicit one. Defaults to <c>default</c>.</summary>
    public string DefaultWorkspace { get; init; } = "default";

    /// <summary>
    /// Create workspaces on first use instead of failing with <see cref="OpenShellErrorKind.NotFound"/>.
    /// Convenient for tests; set to false to mimic a gateway that only knows workspaces created explicitly.
    /// </summary>
    public bool AutoCreateWorkspaces { get; init; } = true;

    /// <summary>
    /// How long a new sandbox stays in <see cref="SandboxPhase.Provisioning"/> before it becomes ready.
    /// The create call always reports <c>Provisioning</c>; later reads observe <c>Ready</c> once this time has passed.
    /// </summary>
    public TimeSpan ProvisioningDelay { get; init; } = TimeSpan.Zero;

    /// <summary>How long stop and start take to reach <c>Stopped</c> and <c>Ready</c>.</summary>
    public TimeSpan TransitionDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// How long a sandbox deletion takes. Zero deletes immediately and reports <see cref="DeletionOutcome.Completed"/>;
    /// a positive delay reports <see cref="DeletionOutcome.Accepted"/> and removes the sandbox after the delay.
    /// </summary>
    public TimeSpan DeletionDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Maximum rootfs tar size accepted by <see cref="IRootfsClient"/>. Defaults to 1 GiB.</summary>
    public ulong RootfsMaxBytes { get; init; } = 1UL << 30;

    /// <summary>Gateway version reported by the health checks.</summary>
    public string GatewayVersion { get; init; } = "fake-1.0.0";

    /// <summary>Items per page when a list call does not set a page size.</summary>
    public int DefaultPageSize { get; init; } = 100;

    /// <summary>Gateway-provided provider profiles that exist before any import.</summary>
    public IReadOnlyList<ProviderProfile>? BuiltinProfiles { get; init; }
}

/// <summary>One recorded call on a <see cref="FakeGateway"/>.</summary>
/// <param name="Sequence">Position in the call log, starting at 1.</param>
/// <param name="Operation">Operation name such as <c>Sandboxes.Create</c>.</param>
/// <param name="Workspace">The workspace the call ran in; empty for gateway-wide calls.</param>
/// <param name="Target">The primary resource name, when the call has one.</param>
public sealed record FakeCall(int Sequence, string Operation, string Workspace, string? Target);

/// <summary>A command run request presented to the handlers registered with <see cref="FakeGateway.OnExec"/>.</summary>
/// <param name="Workspace">The workspace of the sandbox.</param>
/// <param name="Sandbox">The sandbox name.</param>
/// <param name="Command">The command and arguments.</param>
/// <param name="Options">The exec options the caller passed.</param>
public sealed record FakeExecInvocation(string Workspace, string Sandbox, IReadOnlyList<string> Command, ExecOptions Options)
{
    /// <summary>The command joined with single spaces, convenient for matching.</summary>
    public string CommandLine => string.Join(' ', Command);
}

/// <summary>The scripted outcome of a command.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Stdout">Standard output bytes.</param>
/// <param name="Stderr">Standard error bytes.</param>
public sealed record FakeExecScript(int ExitCode, ReadOnlyMemory<byte> Stdout = default, ReadOnlyMemory<byte> Stderr = default)
{
    /// <summary>A zero exit code with the given standard output.</summary>
    public static FakeExecScript Success(string stdout = "") => new(0, Encoding.UTF8.GetBytes(stdout));

    /// <summary>A failing exit code with the given standard error.</summary>
    public static FakeExecScript Failure(int exitCode, string stderr = "") => new(exitCode, default, Encoding.UTF8.GetBytes(stderr));
}

/// <summary>A TCP or SSH relay request presented to the handler registered with <see cref="FakeGateway.OnForward"/>.</summary>
/// <param name="Workspace">The workspace of the sandbox.</param>
/// <param name="Sandbox">The sandbox name.</param>
/// <param name="Port">The target port; null for an SSH relay.</param>
/// <param name="ServiceId">Optional correlation id the caller passed.</param>
public sealed record FakeForwardRequest(string Workspace, string Sandbox, uint? Port, string? ServiceId)
{
    /// <summary>True when the request targets the sandbox SSH relay.</summary>
    public bool IsSsh => Port is null;
}

/// <summary>Kinds of frame a client can send on an interactive exec session.</summary>
public enum FakeInteractiveFrameKind
{
    /// <summary>The session started.</summary>
    Start,
    /// <summary>Bytes written to standard input.</summary>
    Stdin,
    /// <summary>The terminal was resized.</summary>
    Resize,
    /// <summary>Standard input was closed.</summary>
    StdinClosed,
}

/// <summary>A frame received from the client on an interactive exec session.</summary>
/// <param name="Sandbox">The sandbox name.</param>
/// <param name="Kind">What the frame carries.</param>
/// <param name="Data">Standard input bytes for <see cref="FakeInteractiveFrameKind.Stdin"/>.</param>
/// <param name="Columns">Terminal columns for start and resize frames.</param>
/// <param name="Rows">Terminal rows for start and resize frames.</param>
public sealed record FakeInteractiveFrame(string Sandbox, FakeInteractiveFrameKind Kind, ReadOnlyMemory<byte> Data = default, uint Columns = 0, uint Rows = 0);
