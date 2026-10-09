// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Options for running a command in a sandbox.</summary>
public sealed record ExecOptions
{
    /// <summary>Working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Execution timeout enforced by the gateway.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Bytes written to the command's standard input.</summary>
    public ReadOnlyMemory<byte> Stdin { get; init; }

    /// <summary>Allocate a TTY.</summary>
    public bool Tty { get; init; }

    /// <summary>Skip the login shell wrapper.</summary>
    public bool NoLoginShell { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}

/// <summary>An event from a streaming exec.</summary>
public abstract record ExecEvent
{
    private protected ExecEvent() { }
}

/// <summary>A chunk of standard output or error.</summary>
public sealed record ExecOutput(bool IsStdErr, ReadOnlyMemory<byte> Data) : ExecEvent
{
    /// <summary>Decodes the chunk as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Data.Span);
}

/// <summary>The terminal event carrying the command exit code.</summary>
public sealed record ExecExit(int ExitCode) : ExecEvent;

/// <summary>The buffered result of a command.</summary>
public sealed record ExecResult(int ExitCode, ReadOnlyMemory<byte> Stdout, ReadOnlyMemory<byte> Stderr)
{
    /// <summary>Standard output decoded as UTF-8.</summary>
    public string StdoutText => Encoding.UTF8.GetString(Stdout.Span);

    /// <summary>Standard error decoded as UTF-8.</summary>
    public string StderrText => Encoding.UTF8.GetString(Stderr.Span);

    /// <summary>True when the command exited with code 0.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Command execution inside sandboxes.</summary>
public interface IExecClient
{
    /// <summary>Runs a command and buffers its output.</summary>
    ValueTask<ExecResult> RunAsync(string sandbox, IReadOnlyList<string> command, ExecOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams output as it arrives. The final event is always an <see cref="ExecExit"/>; if the gateway closes
    /// the stream without one, the enumeration throws, so a failing command cannot be missed.
    /// </summary>
    IAsyncEnumerable<ExecEvent> StreamAsync(string sandbox, IReadOnlyList<string> command, ExecOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a command with a TTY and a duplex channel: write stdin, resize the terminal, read output events.
    /// Dispose the session to cancel the command.
    /// </summary>
    ValueTask<ExecInteractiveSession> InteractiveAsync(string sandbox, IReadOnlyList<string> command, uint columns, uint rows, ExecOptions? options = null, bool allocateTty = true, CancellationToken cancellationToken = default);
}

internal sealed class ExecClient(ClientCore core, string workspace) : IExecClient
{
    public async ValueTask<ExecResult> RunAsync(string sandbox, IReadOnlyList<string> command, ExecOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stdout = new ArrayBufferWriter();
        var stderr = new ArrayBufferWriter();
        int? exit = null;
        await foreach (var e in StreamAsync(sandbox, command, options, cancellationToken).ConfigureAwait(false))
        {
            switch (e)
            {
                case ExecOutput { IsStdErr: false } o:
                    stdout.Write(o.Data.Span);
                    break;
                case ExecOutput o:
                    stderr.Write(o.Data.Span);
                    break;
                case ExecExit x:
                    exit = x.ExitCode;
                    break;
            }
        }

        return new ExecResult(exit!.Value, stdout.ToMemory(), stderr.ToMemory());
    }

    public async IAsyncEnumerable<ExecEvent> StreamAsync(
        string sandbox,
        IReadOnlyList<string> command,
        ExecOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("command must not be empty", nameof(command));
        }

        var request = BuildRequest(workspace, sandbox, command, options);

        var sawExit = false;
        await foreach (var ev in core.StreamAsync(o => core.Grpc.ExecSandbox(request, o), cancellationToken).ConfigureAwait(false))
        {
            switch (ev.PayloadCase)
            {
                case Wire.ExecSandboxEvent.PayloadOneofCase.Stdout:
                    yield return new ExecOutput(false, ev.Stdout.Data.Memory);
                    break;
                case Wire.ExecSandboxEvent.PayloadOneofCase.Stderr:
                    yield return new ExecOutput(true, ev.Stderr.Data.Memory);
                    break;
                case Wire.ExecSandboxEvent.PayloadOneofCase.Exit:
                    sawExit = true;
                    yield return new ExecExit(ev.Exit.ExitCode);
                    break;
            }
        }

        if (!sawExit)
        {
            throw new OpenShellException(OpenShellErrorKind.Unavailable, "exec stream ended without an exit event");
        }
    }

    public async ValueTask<ExecInteractiveSession> InteractiveAsync(
        string sandbox,
        IReadOnlyList<string> command,
        uint columns,
        uint rows,
        ExecOptions? options = null,
        bool allocateTty = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("command must not be empty", nameof(command));
        }

        var request = BuildRequest(workspace, sandbox, command, options);
        request.Tty = allocateTty;
        request.Cols = columns;
        request.Rows = rows;
        return await ExecInteractiveSession.StartAsync(core, request, cancellationToken).ConfigureAwait(false);
    }

    internal static Wire.ExecSandboxRequest BuildRequest(string workspace, string sandbox, IReadOnlyList<string> command, ExecOptions? options)
    {
        var request = new Wire.ExecSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Workdir = options?.WorkingDirectory ?? "",
            Tty = options?.Tty ?? false,
            NoLoginShell = options?.NoLoginShell ?? false,
            RequestId = options?.RequestId ?? "",
        };
        request.Command.AddRange(command);
        SandboxMapping.AddAll(request.Environment, options?.Environment);
        if (options?.Timeout is { } timeout)
        {
            request.ExecutionTimeout = Duration.FromTimeSpan(timeout);
        }

        if (options is { Stdin.IsEmpty: false })
        {
            request.Stdin = UnsafeByteOperations.UnsafeWrap(options.Stdin);
        }

        return request;
    }

    private sealed class ArrayBufferWriter
    {
        private byte[] _buffer = [];
        private int _length;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public ReadOnlyMemory<byte> ToMemory() => _buffer.AsMemory(0, _length);
    }
}
