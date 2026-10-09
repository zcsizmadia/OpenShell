// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Grpc.Core;
using Wire = Openshell.V1;

namespace OpenShell.Testing;

/// <summary>Writes client messages into a channel that the in-memory "server" reads.</summary>
internal sealed class ChannelClientWriter<T>(ChannelWriter<T> writer) : IClientStreamWriter<T>
{
    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message) => writer.WriteAsync(message).AsTask();

    public Task WriteAsync(T message, CancellationToken cancellationToken) => writer.WriteAsync(message, cancellationToken).AsTask();

    public Task CompleteAsync()
    {
        writer.TryComplete();
        return Task.CompletedTask;
    }
}

/// <summary>Reads server messages from a channel; a completion error surfaces from <see cref="MoveNext"/>.</summary>
internal sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
{
    public T Current { get; private set; } = default!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>The sandbox side of a forwarded connection. Reads yield what the client sent; writes reach the client.</summary>
internal sealed class RemoteEndStream(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) : Stream
{
    private ReadOnlyMemory<byte> _pending;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (!await input.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _pending = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var n = Math.Min(buffer.Length, _pending.Length);
        _pending[..n].CopyTo(buffer);
        _pending = _pending[n..];
        return n;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        output.WriteAsync(buffer.ToArray(), cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("use ReadAsync");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("use WriteAsync");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            output.TryComplete();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Serves the two duplex RPCs from memory so the fake can hand out the SDK's real <see cref="ForwardStream"/> and
/// <see cref="ExecInteractiveSession"/> types.
/// </summary>
internal sealed class DuplexInvoker(FakeGateway gateway) : CallInvoker
{
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        return typeof(TRequest) == typeof(Wire.TcpForwardFrame)
            ? (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)ForwardServer.Start(gateway, options.CancellationToken)
            : (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)InteractiveServer.Start(gateway, options.CancellationToken);
    }

    // The SDK only issues the two duplex calls above through this invoker.
    [ExcludeFromCodeCoverage(Justification = "Unreachable: the fake never issues these call shapes.")]
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    [ExcludeFromCodeCoverage(Justification = "Unreachable: the fake never issues these call shapes.")]
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    [ExcludeFromCodeCoverage(Justification = "Unreachable: the fake never issues these call shapes.")]
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    [ExcludeFromCodeCoverage(Justification = "Unreachable: the fake never issues these call shapes.")]
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException();
}

internal static class ForwardServer
{
    public static AsyncDuplexStreamingCall<Wire.TcpForwardFrame, Wire.TcpForwardFrame> Start(FakeGateway gateway, CancellationToken callToken)
    {
        var requests = Channel.CreateUnbounded<Wire.TcpForwardFrame>(new UnboundedChannelOptions { SingleReader = true });
        var responses = Channel.CreateUnbounded<Wire.TcpForwardFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
        _ = Task.Run(() => RunAsync(gateway, requests.Reader, responses.Writer, cts.Token), CancellationToken.None);
        return new AsyncDuplexStreamingCall<Wire.TcpForwardFrame, Wire.TcpForwardFrame>(
            new ChannelClientWriter<Wire.TcpForwardFrame>(requests.Writer),
            new ChannelStreamReader<Wire.TcpForwardFrame>(responses.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () =>
            {
                cts.Cancel();
                requests.Writer.TryComplete();
                responses.Writer.TryComplete();
            });
    }

    private static async Task RunAsync(
        FakeGateway gateway, ChannelReader<Wire.TcpForwardFrame> requests, ChannelWriter<Wire.TcpForwardFrame> responses, CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            var first = await requests.ReadAsync(cancellationToken).ConfigureAwait(false);
            var init = first.Init;
            var request = new FakeForwardRequest(
                init.Workspace,
                init.Sandbox,
                init.TargetCase == Wire.TcpForwardInit.TargetOneofCase.Tcp ? init.Tcp.Port : null,
                init.ServiceId.Length > 0 ? init.ServiceId : null);

            var toRemote = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var fromRemote = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            using var inboundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inbound = PumpInAsync(requests, toRemote.Writer, inboundCts.Token);
            var outbound = PumpOutAsync(fromRemote.Reader, responses, cancellationToken);
            await using var remote = new RemoteEndStream(toRemote.Reader, fromRemote.Writer);
            try
            {
                await gateway.ForwardHandler(request, remote, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The handler is done: flush what it wrote, then stop reading from a client that may still be open.
                fromRemote.Writer.TryComplete();
                await outbound.ConfigureAwait(false);
                await inboundCts.CancelAsync().ConfigureAwait(false);
                await inbound.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client closed the stream.
        }
        catch (Exception e)
        {
            error = new RpcException(new Status(StatusCode.Internal, e.Message));
        }

        responses.TryComplete(error);
    }

    private static async Task PumpInAsync(ChannelReader<Wire.TcpForwardFrame> requests, ChannelWriter<byte[]> toRemote, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (frame.PayloadCase == Wire.TcpForwardFrame.PayloadOneofCase.Data)
                {
                    await toRemote.WriteAsync(frame.Data.ToByteArray(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The call was cancelled.
        }
        finally
        {
            toRemote.TryComplete();
        }
    }

    private static async Task PumpOutAsync(ChannelReader<byte[]> fromRemote, ChannelWriter<Wire.TcpForwardFrame> responses, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in fromRemote.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await responses.WriteAsync(new Wire.TcpForwardFrame { Data = Google.Protobuf.ByteString.CopyFrom(chunk) }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The call was cancelled.
        }
    }
}

internal static class InteractiveServer
{
    public static AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> Start(FakeGateway gateway, CancellationToken callToken)
    {
        var requests = Channel.CreateUnbounded<Wire.ExecSandboxInput>(new UnboundedChannelOptions { SingleReader = true });
        var responses = Channel.CreateUnbounded<Wire.ExecSandboxEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
        _ = Task.Run(() => RunAsync(gateway, requests.Reader, responses.Writer, cts.Token), CancellationToken.None);
        return new AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>(
            new ChannelClientWriter<Wire.ExecSandboxInput>(requests.Writer),
            new ChannelStreamReader<Wire.ExecSandboxEvent>(responses.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () =>
            {
                cts.Cancel();
                requests.Writer.TryComplete();
                responses.Writer.TryComplete();
            });
    }

    private static async Task RunAsync(
        FakeGateway gateway, ChannelReader<Wire.ExecSandboxInput> requests, ChannelWriter<Wire.ExecSandboxEvent> responses, CancellationToken cancellationToken)
    {
        try
        {
            var first = await requests.ReadAsync(cancellationToken).ConfigureAwait(false);
            var start = first.Start;
            var sandbox = start.Sandbox;
            gateway.RecordInteractive(new FakeInteractiveFrame(sandbox, FakeInteractiveFrameKind.Start, default, start.Cols, start.Rows));

            var script = gateway.ResolveExec(start);
            if (script.Stdout.Length > 0)
            {
                await responses.WriteAsync(new Wire.ExecSandboxEvent { Stdout = new Wire.ExecSandboxStdout { Data = Google.Protobuf.ByteString.CopyFrom(script.Stdout.Span) } }, cancellationToken).ConfigureAwait(false);
            }

            if (script.Stderr.Length > 0)
            {
                await responses.WriteAsync(new Wire.ExecSandboxEvent { Stderr = new Wire.ExecSandboxStderr { Data = Google.Protobuf.ByteString.CopyFrom(script.Stderr.Span) } }, cancellationToken).ConfigureAwait(false);
            }

            await responses.WriteAsync(new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit { ExitCode = script.ExitCode } }, cancellationToken).ConfigureAwait(false);
            responses.TryComplete();

            await foreach (var frame in requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (frame.PayloadCase == Wire.ExecSandboxInput.PayloadOneofCase.Stdin)
                {
                    gateway.RecordInteractive(new FakeInteractiveFrame(sandbox, FakeInteractiveFrameKind.Stdin, frame.Stdin.ToByteArray()));
                }
                else if (frame.PayloadCase == Wire.ExecSandboxInput.PayloadOneofCase.Resize)
                {
                    gateway.RecordInteractive(new FakeInteractiveFrame(sandbox, FakeInteractiveFrameKind.Resize, default, frame.Resize.Cols, frame.Resize.Rows));
                }
            }

            gateway.RecordInteractive(new FakeInteractiveFrame(sandbox, FakeInteractiveFrameKind.StdinClosed));
        }
        catch (OperationCanceledException)
        {
            // The client closed the session.
        }
        catch (ChannelClosedException)
        {
            // The client closed the session before sending the start frame.
        }
        finally
        {
            responses.TryComplete();
        }
    }
}
