// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Options for <see cref="ITcpClient.ForwardAsync"/>.</summary>
public sealed record ForwardOptions
{
    /// <summary>Optional service identifier for audit and correlation.</summary>
    public string? ServiceId { get; init; }
}

/// <summary>Options for <see cref="ITcpClient.ListenAsync"/>.</summary>
public sealed record ListenOptions
{
    /// <summary>Local address to bind. Defaults to loopback.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Optional service identifier for audit and correlation.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Carry each connection over an SSH relay session instead of the plain TCP target.</summary>
    public bool UseSshTunnel { get; init; }
}

/// <summary>A local listener that bridges every accepted connection to a sandbox port.</summary>
public interface IForwardListener : IAsyncDisposable
{
    /// <summary>The bound local endpoint, with the resolved port when 0 was requested.</summary>
    IPEndPoint LocalEndPoint { get; }
}

/// <summary>TCP forwarding to sandbox ports.</summary>
public interface ITcpClient
{
    /// <summary>Opens a duplex byte stream to a loopback port inside the sandbox.</summary>
    ValueTask<ForwardStream> ForwardAsync(string sandbox, uint port, ForwardOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens locally (port 0 picks a free one) and forwards each connection to <paramref name="remotePort"/>.
    /// Dispose the result to stop accepting and tear down active connections.
    /// </summary>
    ValueTask<IForwardListener> ListenAsync(string sandbox, uint remotePort, int localPort = 0, ListenOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class TcpForwardClient(ClientCore core, string workspace, ISandboxClient sandboxes, ISshClient ssh) : ITcpClient
{
    public async ValueTask<ForwardStream> ForwardAsync(string sandbox, uint port, ForwardOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ValidatePort(port);
        _ = await sandboxes.GetAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var init = new Wire.TcpForwardInit
        {
            Sandbox = sandbox,
            Workspace = workspace,
            ServiceId = options?.ServiceId ?? "",
            Tcp = new Wire.TcpRelayTarget { Host = "127.0.0.1", Port = port },
        };
        return await ForwardStream.OpenAsync(core, init, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IForwardListener> ListenAsync(string sandbox, uint remotePort, int localPort = 0, ListenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ValidatePort(remotePort);
        ArgumentOutOfRangeException.ThrowIfNegative(localPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, ushort.MaxValue);
        options ??= new ListenOptions();

        var listener = new TcpListener(options.BindAddress, localPort);
        listener.Start();
        Func<CancellationToken, ValueTask<Stream>> open = options.UseSshTunnel
            ? async ct => await ssh.TunnelAsync(sandbox, new TunnelOptions { ServiceId = options.ServiceId }, ct).ConfigureAwait(false)
            : async ct => await ForwardAsync(sandbox, remotePort, new ForwardOptions { ServiceId = options.ServiceId }, ct).ConfigureAwait(false);
        return ValueTask.FromResult<IForwardListener>(new ForwardListener(listener, open));
    }

    internal static void ValidatePort(uint port)
    {
        if (port is 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be in range 1-65535");
        }
    }
}

internal sealed class ForwardListener : IForwardListener
{
    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly HashSet<Task> _bridges = [];
    private int _disposed;

    public ForwardListener(TcpListener listener, Func<CancellationToken, ValueTask<Stream>> open)
    {
        _listener = listener;
        _open = open;
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public IPEndPoint LocalEndPoint { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _bridges];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            client.NoDelay = true;
            Task bridge = BridgeAsync(client, token);
            lock (_gate)
            {
                _bridges.Add(bridge);
            }

            _ = bridge.ContinueWith(
                t =>
                {
                    lock (_gate)
                    {
                        _bridges.Remove(t);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task CopyUpAsync(Stream local, Stream tunnel, CancellationToken token)
    {
        await local.CopyToAsync(tunnel, token).ConfigureAwait(false);
        if (tunnel is ForwardStream forward)
        {
            await forward.CompleteWriteAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OpenShellException or IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Already reported through the bridge; only observed here so it is not left unobserved.
        }
    }

    private async Task BridgeAsync(System.Net.Sockets.TcpClient client, CancellationToken token)
    {
        using (client)
        {
            Stream? tunnel = null;
            Task up = Task.CompletedTask;
            Task down = Task.CompletedTask;
            try
            {
                tunnel = await _open(token).ConfigureAwait(false);
                var local = client.GetStream();
                var open = tunnel;
                up = CopyUpAsync(local, open, token);
                down = open.CopyToAsync(local, token);

                // A local half-close lets the response drain; the remote finishing ends the connection.
                var first = await Task.WhenAny(up, down).ConfigureAwait(false);
                await first.ConfigureAwait(false);
                if (first == up)
                {
                    await down.ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OpenShellException or IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The connection is dropped; the listener keeps serving new ones.
            }
            finally
            {
                if (tunnel is not null)
                {
                    await tunnel.DisposeAsync().ConfigureAwait(false);
                }

                client.Close();
                await ObserveAsync(up).ConfigureAwait(false);
                await ObserveAsync(down).ConfigureAwait(false);
            }
        }
    }
}
