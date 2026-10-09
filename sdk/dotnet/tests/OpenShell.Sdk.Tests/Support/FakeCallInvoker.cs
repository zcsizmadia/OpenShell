// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;

namespace OpenShell.Tests.Support;

/// <summary>The server side of a fake duplex call: read what the client wrote, write what the client reads.</summary>
internal sealed class DuplexChannel<TReq, TResp> : IDisposable
    where TReq : class, IMessage
    where TResp : class
{
    private readonly Channel<TReq> _requests = Channel.CreateUnbounded<TReq>();
    private readonly Channel<TResp> _responses = Channel.CreateUnbounded<TResp>();
    private readonly CancellationTokenSource _disposed = new();
    private readonly List<TReq> _received = [];

    public DuplexChannel(CancellationToken clientCancellation)
    {
        ClientCancellation = clientCancellation;
        RequestWriter = new Writer(this);
        ResponseReader = new Reader(this);
    }

    /// <summary>The cancellation token the client passed to the call.</summary>
    public CancellationToken ClientCancellation { get; set; }

    /// <summary>When set, writes beyond this many successful frames fail with <c>Unavailable</c>.</summary>
    public int? FailWritesAfter { get; set; }

    /// <summary>When true, completing the request stream fails with <c>Unavailable</c>.</summary>
    public bool FailComplete { get; set; }

    /// <summary>When set, the client's next read throws this exception instead of returning a frame.</summary>
    public Exception? MoveNextFault { get; set; }

    /// <summary>True once the client disposed the call.</summary>
    public bool ClientDisposed => _disposed.IsCancellationRequested;

    public IClientStreamWriter<TReq> RequestWriter { get; }

    public IAsyncStreamReader<TResp> ResponseReader { get; }

    /// <summary>Every request frame received so far (deep copies).</summary>
    public IReadOnlyList<TReq> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>Reads the next client frame; null once the client completed its side.</summary>
    public async ValueTask<TReq?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ClientCancellation, _disposed.Token);
        try
        {
            return await _requests.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // The client cancelled or disposed the call.
        }
    }

    /// <summary>Reads client frames until the client completes.</summary>
    public async IAsyncEnumerable<TReq> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            yield return frame;
        }
    }

    public void Send(TResp response) => _responses.Writer.TryWrite(response);

    internal void Finish(Exception? fault) => _responses.Writer.TryComplete(fault);

    public void Dispose()
    {
        _disposed.Cancel();
        _requests.Writer.TryComplete();
    }

    private sealed class Writer(DuplexChannel<TReq, TResp> owner) : IClientStreamWriter<TReq>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TReq message, CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : WriteAsync(message);
        }

        public Task WriteAsync(TReq message)
        {
            lock (owner._received)
            {
                if (owner.FailWritesAfter is { } limit && owner._received.Count >= limit)
                {
                    return Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "write failed")));
                }

                // Round-trip through the wire format so later buffer reuse by the client cannot alter what the server saw.
                var copy = (TReq)message.Descriptor.Parser.ParseFrom(message.ToByteArray());
                owner._received.Add(copy);
                owner._requests.Writer.TryWrite(copy);
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            owner._requests.Writer.TryComplete();
            return owner.FailComplete
                ? Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "complete failed")))
                : Task.CompletedTask;
        }
    }

    private sealed class Reader(DuplexChannel<TReq, TResp> owner) : IAsyncStreamReader<TResp>
    {
        public TResp Current { get; private set; } = null!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (owner.MoveNextFault is { } fault)
            {
                throw fault;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.ClientCancellation, owner._disposed.Token);
            try
            {
                if (!await owner._responses.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
            }

            owner._responses.Reader.TryRead(out var item);
            Current = item!;
            return true;
        }
    }
}

/// <summary>An in-memory <see cref="CallInvoker"/> that routes calls to handlers by RPC name, bypassing serialization.</summary>
internal sealed class FakeCallInvoker : CallInvoker
{
    private readonly Dictionary<string, Func<object, CallOptions, object>> _unary = [];
    private readonly Dictionary<string, Func<object, CallOptions, IEnumerable<object>>> _stream = [];

    public List<(string Method, object Request, CallOptions Options)> Calls { get; } = [];

    public FakeCallInvoker Unary<TReq, TResp>(string method, Func<TReq, TResp> handler)
        where TReq : class
        where TResp : class =>
        Unary<TReq, TResp>(method, (r, _) => handler(r));

    public FakeCallInvoker Unary<TReq, TResp>(string method, Func<TReq, CallOptions, TResp> handler)
        where TReq : class
        where TResp : class
    {
        _unary[method] = (r, o) => handler((TReq)r, o);
        return this;
    }

    public FakeCallInvoker Throws<TReq, TResp>(string method, StatusCode code, string detail = "")
        where TReq : class
        where TResp : class =>
        Unary<TReq, TResp>(method, (_, _) => throw new RpcException(new Status(code, detail)));

    public FakeCallInvoker Stream<TReq, TResp>(string method, Func<TReq, IEnumerable<TResp>> handler)
        where TReq : class
        where TResp : class
    {
        _stream[method] = (r, _) => handler((TReq)r).Cast<object>();
        return this;
    }

    public int CountOf(string method) => Calls.Count(c => c.Method == method);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        Calls.Add((method.Name, request, options));
        if (!_unary.TryGetValue(method.Name, out var handler))
        {
            throw new InvalidOperationException($"no fake handler for {method.Name}");
        }

        try
        {
            var response = (TResponse)handler(request, options);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }
        catch (RpcException e)
        {
            return new AsyncUnaryCall<TResponse>(
                Task.FromException<TResponse>(e), Task.FromResult(new Metadata()), () => e.Status, () => [], () => { });
        }
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        Calls.Add((method.Name, request, options));
        if (!_stream.TryGetValue(method.Name, out var handler))
        {
            throw new InvalidOperationException($"no fake stream handler for {method.Name}");
        }

        var items = handler(request, options).Cast<TResponse>();
        return new AsyncServerStreamingCall<TResponse>(
            new Reader<TResponse>(items), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    /// <summary>Registers a duplex-streaming handler. The handler runs concurrently with the client and ends the call by returning.</summary>
    public FakeCallInvoker Duplex<TReq, TResp>(string method, Func<DuplexChannel<TReq, TResp>, Task> handler)
        where TReq : class, IMessage
        where TResp : class
    {
        _duplex[method] = failWritesAfter =>
        {
            var channel = new DuplexChannel<TReq, TResp>(CancellationToken.None) { FailWritesAfter = failWritesAfter };
            return new DuplexSession(
                channel,
                channel.RequestWriter,
                channel.ResponseReader,
                () => handler(channel),
                channel.Finish,
                channel.Dispose,
                token => channel.ClientCancellation = token);
        };
        return this;
    }

    private sealed record DuplexSession(
        object Channel,
        object Writer,
        object Reader,
        Func<Task> Run,
        Action<Exception?> Finish,
        Action Dispose,
        Action<CancellationToken> BindCancellation);

    /// <summary>Registers a client-streaming handler that receives every request once the client completes.</summary>
    public FakeCallInvoker ClientStream<TReq, TResp>(string method, Func<IReadOnlyList<TReq>, TResp> handler)
        where TReq : class, IMessage
        where TResp : class
    {
        _clientStream[method] = (reqs, _) => handler([.. reqs.Cast<TReq>()]);
        return this;
    }

    /// <summary>The duplex channels opened so far, in order, keyed by method name.</summary>
    public List<(string Method, object Channel)> DuplexCalls { get; } = [];

    /// <summary>Applied to every duplex call opened afterwards: writes beyond this many frames fail.</summary>
    public int? DuplexFailWritesAfter { get; set; }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        if (!_clientStream.TryGetValue(method.Name, out var handler))
        {
            throw new InvalidOperationException($"no fake client-stream handler for {method.Name}");
        }

        var requests = new List<object>();
        var completed = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RequestWriter<TRequest>(
            m => requests.Add(m!),
            () =>
            {
                try
                {
                    completed.TrySetResult((TResponse)handler(requests, options));
                }
                catch (RpcException e)
                {
                    completed.TrySetException(e);
                }
            });
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            writer, completed.Task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        if (!_duplex.TryGetValue(method.Name, out var handler))
        {
            throw new InvalidOperationException($"no fake duplex handler for {method.Name}");
        }

        var session = handler(DuplexFailWritesAfter);
        session.BindCancellation(options.CancellationToken);
        DuplexCalls.Add((method.Name, session.Channel));
        Calls.Add((method.Name, session.Channel, options));
        _ = Task.Run(async () =>
        {
            Exception? fault = null;
            try
            {
                await session.Run().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                fault = e;
            }
            finally
            {
                session.Finish(fault);
            }
        });
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            (IClientStreamWriter<TRequest>)session.Writer,
            (IAsyncStreamReader<TResponse>)session.Reader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            session.Dispose);
    }

    private readonly Dictionary<string, Func<int?, DuplexSession>> _duplex = [];
    private readonly Dictionary<string, Func<IReadOnlyList<object>, CallOptions, object>> _clientStream = [];

    private sealed class RequestWriter<T>(Action<T> onWrite, Action onComplete) : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            onWrite(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            onComplete();
            return Task.CompletedTask;
        }
    }

    private sealed class Reader<T>(IEnumerable<T> items) : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _e = items.GetEnumerator();

        public T Current => _e.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_e.MoveNext());
        }
    }
}
