// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Grpc.Core;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>
/// A bidirectional byte stream over the gateway's TCP forward relay. Reads and writes are asynchronous only;
/// one reader and one writer may be active at the same time.
/// </summary>
public sealed class ForwardStream : Stream
{
    private const int MaxFrameBytes = 256 * 1024;

    private readonly AsyncDuplexStreamingCall<Wire.TcpForwardFrame, Wire.TcpForwardFrame> _call;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Func<ValueTask>? _onClosed;
    private ReadOnlyMemory<byte> _pending;
    private int _writeCompleted;
    private int _closed;

    internal ForwardStream(
        AsyncDuplexStreamingCall<Wire.TcpForwardFrame, Wire.TcpForwardFrame> call,
        CancellationTokenSource cts,
        Func<ValueTask>? onClosed = null)
    {
        _call = call;
        _cts = cts;
        _onClosed = onClosed;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal static async ValueTask<ForwardStream> OpenAsync(
        ClientCore core,
        Wire.TcpForwardInit init,
        CancellationToken cancellationToken,
        Func<ValueTask>? onClosed = null)
    {
        ObjectDisposedException.ThrowIf(core.IsDisposed, core);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AsyncDuplexStreamingCall<Wire.TcpForwardFrame, Wire.TcpForwardFrame>? call = null;
        try
        {
            call = core.Grpc.ForwardTcp(new CallOptions(cancellationToken: cts.Token));
            await call.RequestStream.WriteAsync(new Wire.TcpForwardFrame { Init = init }, cts.Token).ConfigureAwait(false);
            return new ForwardStream(call, cts, onClosed);
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

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_pending.IsEmpty)
        {
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token)
                : null;
            var token = linked?.Token ?? _cts.Token;
            while (_pending.IsEmpty)
            {
                bool moved;
                try
                {
                    moved = await _call.ResponseStream.MoveNext(token).ConfigureAwait(false);
                }
                catch (RpcException e)
                {
                    throw ClientCore.Map(e, token);
                }

                if (!moved)
                {
                    return 0;
                }

                if (_call.ResponseStream.Current.PayloadCase == Wire.TcpForwardFrame.PayloadOneofCase.Data)
                {
                    _pending = _call.ResponseStream.Current.Data.Memory;
                }
            }
        }

        var n = Math.Min(buffer.Length, _pending.Length);
        _pending[..n].CopyTo(buffer);
        _pending = _pending[n..];
        return n;
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (Volatile.Read(ref _writeCompleted) != 0)
        {
            throw new InvalidOperationException("the write side of the stream is completed");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                var chunk = buffer.Length > MaxFrameBytes ? buffer[..MaxFrameBytes] : buffer;
                buffer = buffer[chunk.Length..];

                // The gRPC writer serializes the frame before the awaited write completes, so wrapping is safe.
                var frame = new Wire.TcpForwardFrame { Data = UnsafeByteOperations.UnsafeWrap(chunk) };
                try
                {
                    await _call.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (RpcException e)
                {
                    throw ClientCore.Map(e, cancellationToken);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Half-closes the stream: no more data will be sent, but reads continue until the remote side finishes.</summary>
    public async ValueTask CompleteWriteAsync()
    {
        if (Interlocked.Exchange(ref _writeCompleted, 1) != 0)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // The stream is already torn down; nothing is left to complete.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("use ReadAsync");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("use WriteAsync");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            await CompleteWriteAsync().ConfigureAwait(false);
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _call.Dispose();
            _cts.Dispose();
            if (Interlocked.Exchange(ref _onClosed, null) is { } onClosed)
            {
                await onClosed().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
