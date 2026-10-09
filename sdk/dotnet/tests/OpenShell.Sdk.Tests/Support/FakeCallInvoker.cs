// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;

namespace OpenShell.Tests.Support;

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

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException();

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException();

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
