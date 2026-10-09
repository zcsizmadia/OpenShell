// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Threading.Channels;

namespace OpenShell.Tests.Support;

/// <summary>One half of an in-memory WebSocket pair; sends on one channel and receives on the other.</summary>
internal sealed class InMemoryWebSocket : WebSocket
{
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming;
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _outgoing;
    private WebSocketState _state = WebSocketState.Open;
    private byte[] _leftover = [];

    private InMemoryWebSocket(Channel<(byte[], WebSocketMessageType)> incoming, Channel<(byte[], WebSocketMessageType)> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryWebSocket Client, InMemoryWebSocket Server) CreatePair()
    {
        var a = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        var b = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        return (new InMemoryWebSocket(a, b), new InMemoryWebSocket(b, a));
    }

    public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _outgoing.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        _outgoing.Writer.TryWrite(([], WebSocketMessageType.Close));
        _outgoing.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        _outgoing.Writer.TryWrite(([], WebSocketMessageType.Close));
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _outgoing.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_leftover.Length == 0)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var (data, type) = await _incoming.Reader.ReadAsync(cancellationToken);
            if (type == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            _leftover = data;
        }

        var count = Math.Min(buffer.Count, _leftover.Length);
        Array.Copy(_leftover, 0, buffer.Array!, buffer.Offset, count);
        _leftover = _leftover[count..];
        return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, _leftover.Length == 0);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        _outgoing.Writer.TryWrite((buffer.ToArray(), messageType));
        return Task.CompletedTask;
    }
}
