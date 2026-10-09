// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Google.Protobuf;
using Grpc.Core;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>
/// A running interactive command. Read <see cref="ReadEventsAsync"/> (once) for output, write stdin and resize
/// the terminal concurrently, and await <see cref="WaitForExitAsync"/> for the exit code.
/// </summary>
public sealed class ExecInteractiveSession : IAsyncDisposable
{
    private const int MaxFrameBytes = 256 * 1024;

    private readonly AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> _call;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _eventsStarted;
    private int _inputCompleted;
    private int _disposed;

    private ExecInteractiveSession(AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> call, CancellationTokenSource cts)
    {
        _call = call;
        _cts = cts;
    }

    internal static async ValueTask<ExecInteractiveSession> StartAsync(ClientCore core, Wire.ExecSandboxRequest start, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(core.IsDisposed, core);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>? call = null;
        try
        {
            call = core.Grpc.ExecSandboxInteractive(new CallOptions(cancellationToken: cts.Token));
            await call.RequestStream.WriteAsync(new Wire.ExecSandboxInput { Start = start }, cts.Token).ConfigureAwait(false);
            return new ExecInteractiveSession(call, cts);
        }
        catch (RpcException e)
        {
            call?.Dispose();
            cts.Dispose();
            throw ClientCore.Map(e, cancellationToken);
        }
        catch
        {
            call?.Dispose();
            cts.Dispose();
            throw;
        }
    }

    /// <summary>Completes with the exit code once the command finished; faults if the stream failed first.</summary>
    public Task<int> ExitCode => _exit.Task;

    /// <summary>Sends bytes to the command's standard input.</summary>
    public async ValueTask WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        while (!data.IsEmpty)
        {
            var chunk = data.Length > MaxFrameBytes ? data[..MaxFrameBytes] : data;
            data = data[chunk.Length..];

            // The gRPC writer serializes the frame before the awaited write completes, so wrapping is safe.
            await SendAsync(new Wire.ExecSandboxInput { Stdin = UnsafeByteOperations.UnsafeWrap(chunk) }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the command's terminal about a new window size.</summary>
    public ValueTask ResizeAsync(uint columns, uint rows, CancellationToken cancellationToken = default) =>
        SendAsync(new Wire.ExecSandboxInput { Resize = new Wire.ExecSandboxWindowResize { Cols = columns, Rows = rows } }, cancellationToken);

    /// <summary>Closes standard input (EOF) while output keeps flowing.</summary>
    public async ValueTask CompleteStdinAsync()
    {
        if (Interlocked.Exchange(ref _inputCompleted, 1) != 0)
        {
            return;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // The stream is already torn down.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Streams output and finally the exit event. May be enumerated once. If the gateway closes the stream without
    /// an exit event the enumeration throws, so a failing command cannot be missed.
    /// </summary>
    public IAsyncEnumerable<ExecEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _eventsStarted, 1) != 0)
        {
            throw new InvalidOperationException("the event stream can only be enumerated once");
        }

        return ReadEventsCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ExecEvent> ReadEventsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = LinkToSession(cancellationToken);
        var token = linked.Token;
        var sawExit = false;
        while (true)
        {
            bool moved;
            try
            {
                moved = await _call.ResponseStream.MoveNext(token).ConfigureAwait(false);
            }
            catch (RpcException e)
            {
                var mapped = ClientCore.Map(e, token);
                if (mapped is OperationCanceledException canceled)
                {
                    _exit.TrySetCanceled(canceled.CancellationToken);
                }
                else
                {
                    _exit.TrySetException(mapped);
                }

                throw mapped;
            }
            catch (OperationCanceledException e)
            {
                _exit.TrySetCanceled(e.CancellationToken);
                throw;
            }
            catch (ObjectDisposedException)
            {
                _exit.TrySetCanceled(cancellationToken);
                throw new OperationCanceledException("the interactive session was closed");
            }

            if (!moved)
            {
                break;
            }

            var ev = _call.ResponseStream.Current;
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
                    _exit.TrySetResult(ev.Exit.ExitCode);
                    yield return new ExecExit(ev.Exit.ExitCode);
                    break;
            }
        }

        if (!sawExit)
        {
            var error = new OpenShellException(OpenShellErrorKind.Unavailable, "exec stream ended without an exit event");
            _exit.TrySetException(error);
            throw error;
        }
    }

    /// <summary>
    /// Waits for the exit code. When nobody is reading <see cref="ReadEventsAsync"/>, output is drained and discarded.
    /// </summary>
    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _eventsStarted, 1) == 0)
        {
            await foreach (var _ in ReadEventsCoreAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }

        return await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the command and releases the stream.</summary>
    public async ValueTask CancelAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CompleteStdinAsync().ConfigureAwait(false);
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _call.Dispose();
            _cts.Dispose();
            _exit.TrySetException(new OpenShellException(OpenShellErrorKind.Cancelled, "the interactive session was closed before the command exited"));
            _ = _exit.Task.Exception; // Observed so an unawaited ExitCode never surfaces as an unobserved exception.
        }
    }

    private CancellationTokenSource LinkToSession(CancellationToken cancellationToken)
    {
        try
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        }
        catch (ObjectDisposedException)
        {
            _exit.TrySetCanceled(cancellationToken);
            throw new OperationCanceledException("the interactive session was closed");
        }
    }

    private async ValueTask SendAsync(Wire.ExecSandboxInput message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _inputCompleted) != 0)
        {
            throw new InvalidOperationException("standard input is already closed");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _call.RequestStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            throw ClientCore.Map(e, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
