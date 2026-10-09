// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Openshell.Datamodel.V1;
using GrpcService = Openshell.V1.OpenShell;

namespace OpenShell;

/// <summary>State shared by an <see cref="OpenShellClient"/> and every workspace-scoped view of it.</summary>
internal sealed class ClientCore : IDisposable
{
    private readonly GrpcChannel? _channel;
    private int _disposed;

    public ClientCore(OpenShellClientOptions options, GrpcChannel? channel, GrpcService.OpenShellClient grpc)
    {
        Options = options;
        _channel = channel;
        Grpc = grpc;
    }

    public OpenShellClientOptions Options { get; }

    public GrpcService.OpenShellClient Grpc { get; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Logger for SDK-level events such as retries and watch reconnects; discards everything when no factory is configured.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>A resource whose lifetime follows the client, such as an edge tunnel proxy.</summary>
    public IDisposable? OwnedResource { get; init; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public CallOptions CallOptions(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var effective = timeout ?? Options.DefaultTimeout;
        return new CallOptions(
            deadline: effective is { } t ? DateTime.UtcNow + t : null,
            cancellationToken: cancellationToken);
    }

    /// <summary>Runs a unary call, retrying once when the auth provider can recover from <c>Unauthenticated</c>.</summary>
    public async ValueTask<TResponse> UnaryAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var active = call(CallOptions(cancellationToken, timeout));
                return await active.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated && attempt == 0)
            {
                if (!await TryRecoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw Map(e, cancellationToken);
                }
            }
            catch (RpcException e)
            {
                throw Map(e, cancellationToken);
            }
        }
    }

    /// <summary>Runs a server-streaming call, mapping gRPC failures to <see cref="OpenShellException"/>.</summary>
    public async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var active = call(CallOptions(cancellationToken));
        var stream = active.ResponseStream;
        while (true)
        {
            bool moved;
            try
            {
                moved = await stream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException e)
            {
                throw Map(e, cancellationToken);
            }

            if (!moved)
            {
                yield break;
            }

            yield return stream.Current;
        }
    }

    public static Exception Map(RpcException e, CancellationToken cancellationToken) =>
        e.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : OpenShellException.FromRpc(e);

    public static WorkspaceSelector Scope(string workspace) => new() { Workspace = workspace };

    public static WorkspaceSelector ListScope(string workspace, bool all) =>
        all ? new WorkspaceSelector { AllWorkspaces = new AllWorkspaces() } : Scope(workspace);

    private async ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Options.Auth.TryRecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OpenShellException)
        {
            throw new OpenShellException(OpenShellErrorKind.Auth, "credential recovery failed", e);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel?.Dispose();
            OwnedResource?.Dispose();
        }
    }
}
