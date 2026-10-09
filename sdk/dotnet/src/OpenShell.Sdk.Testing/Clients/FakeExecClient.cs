// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    /// <summary>Finds a sandbox that can run commands: it must exist and be ready.</summary>
    internal SandboxEntry RequireRunning(string workspace, string name)
    {
        var entry = FindSandbox(workspace, name);
        return entry.Phase == SandboxPhase.Ready
            ? entry
            : throw Conflict($"sandbox '{name}' is not ready (phase {entry.Phase})");
    }
}

internal sealed class FakeExecClient(FakeGateway gateway, string workspace) : IExecClient
{
    public ValueTask<ExecResult> RunAsync(string sandbox, IReadOnlyList<string> command, ExecOptions? options = null, CancellationToken cancellationToken = default)
    {
        Validate(sandbox, command);
        var script = gateway.Run("Exec.Run", workspace, sandbox, cancellationToken, () =>
        {
            _ = gateway.RequireRunning(workspace, sandbox);
            return gateway.ResolveExec(new FakeExecInvocation(workspace, sandbox, [.. command], options ?? new ExecOptions()));
        });
        return ValueTask.FromResult(new ExecResult(script.ExitCode, script.Stdout, script.Stderr));
    }

#pragma warning disable CS1998 // The script is already in memory, so the iterator never awaits.
    public async IAsyncEnumerable<ExecEvent> StreamAsync(
        string sandbox,
        IReadOnlyList<string> command,
        ExecOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(sandbox, command);
        var script = gateway.Run("Exec.Stream", workspace, sandbox, cancellationToken, () =>
        {
            _ = gateway.RequireRunning(workspace, sandbox);
            return gateway.ResolveExec(new FakeExecInvocation(workspace, sandbox, [.. command], options ?? new ExecOptions()));
        });
        if (script.Stdout.Length > 0)
        {
            yield return new ExecOutput(false, script.Stdout);
        }

        if (script.Stderr.Length > 0)
        {
            yield return new ExecOutput(true, script.Stderr);
        }

        yield return new ExecExit(script.ExitCode);
    }
#pragma warning restore CS1998

    public async ValueTask<ExecInteractiveSession> InteractiveAsync(
        string sandbox,
        IReadOnlyList<string> command,
        uint columns,
        uint rows,
        ExecOptions? options = null,
        bool allocateTty = true,
        CancellationToken cancellationToken = default)
    {
        Validate(sandbox, command);
        gateway.Run("Exec.Interactive", workspace, sandbox, cancellationToken, () => gateway.RequireRunning(workspace, sandbox));
        var request = ExecClient.BuildRequest(workspace, sandbox, command, options);
        request.Tty = allocateTty;
        request.Cols = columns;
        request.Rows = rows;
        return await ExecInteractiveSession.StartAsync(gateway.DuplexCore, request, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(string sandbox, IReadOnlyList<string> command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("command must not be empty", nameof(command));
        }
    }
}
