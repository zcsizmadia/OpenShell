// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Wire = Openshell.V1;

namespace OpenShell.Testing;

/// <summary>A local listener created through <see cref="ITcpClient.ListenAsync"/> on the fake gateway.</summary>
/// <param name="Sandbox">The sandbox the listener forwards to.</param>
/// <param name="RemotePort">The sandbox port.</param>
/// <param name="LocalEndPoint">The simulated local endpoint.</param>
/// <param name="UseSshTunnel">Whether connections would travel over the SSH relay.</param>
public sealed record FakeListenerInfo(string Sandbox, uint RemotePort, IPEndPoint LocalEndPoint, bool UseSshTunnel);

internal sealed class FakeListener(FakeGateway gateway, FakeListenerInfo info) : IForwardListener
{
    public FakeListenerInfo Info { get; } = info;

    public IPEndPoint LocalEndPoint => Info.LocalEndPoint;

    public ValueTask DisposeAsync()
    {
        lock (gateway.Sync)
        {
            gateway.Listeners.Remove(this);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed partial class FakeGateway
{
    private int _nextPort = 40000;

    /// <summary>The listeners created with <see cref="ITcpClient.ListenAsync"/> that have not been disposed. No socket is opened.</summary>
    public IReadOnlyList<FakeListenerInfo> ActiveListeners
    {
        get
        {
            lock (Sync)
            {
                return [.. Listeners.Select(l => l.Info)];
            }
        }
    }

    internal int AllocatePort() => _nextPort++;
}

internal sealed class FakeSshClient(FakeGateway gateway, string workspace) : ISshClient
{
    public ValueTask<SshSession> CreateSessionAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Ssh.CreateSession", workspace, sandbox, cancellationToken, () =>
            NewSession(gateway.RequireRunning(workspace, sandbox))));
    }

    public ValueTask<DeletionResult> RevokeSessionAsync(string token, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return ValueTask.FromResult(gateway.Run("Ssh.RevokeSession", workspace, null, cancellationToken, () =>
            FakeGateway.DeleteResult(gateway.SshSessions.ContainsKey(token), options, "ssh session", "<token>", () => gateway.SshSessions.Remove(token))));
    }

    public async ValueTask<ForwardStream> TunnelAsync(string sandbox, TunnelOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var session = gateway.Run("Ssh.Tunnel", workspace, sandbox, cancellationToken, () =>
            NewSession(gateway.RequireRunning(workspace, sandbox)));
        var init = new Wire.TcpForwardInit
        {
            Sandbox = sandbox,
            Workspace = workspace,
            ServiceId = options?.ServiceId ?? "",
            AuthorizationToken = session.Token,
            Ssh = new Wire.SshRelayTarget(),
        };
        return await ForwardStream.OpenAsync(gateway.DuplexCore, init, cancellationToken, () => Revoke(session.Token)).ConfigureAwait(false);
    }

    private SshSession NewSession(SandboxEntry sandbox)
    {
        var token = gateway.NextId("fake-ssh-token");
        gateway.SshSessions[token] = new SshSessionEntry { Workspace = workspace, Sandbox = sandbox.Name, Token = token };
        return new SshSession
        {
            SandboxId = sandbox.Id,
            Token = token,
            GatewayHost = "fake.gateway.local",
            GatewayPort = 443,
            GatewayScheme = "https",
            HostKeyFingerprint = "SHA256:fakehostkeyfingerprint=",
            ExpiresAt = gateway.Time.GetUtcNow() + TimeSpan.FromHours(1),
        };
    }

    private ValueTask Revoke(string token)
    {
        lock (gateway.Sync)
        {
            gateway.SshSessions.Remove(token);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeTcpClient(FakeGateway gateway, string workspace) : ITcpClient
{
    public async ValueTask<ForwardStream> ForwardAsync(string sandbox, uint port, ForwardOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ValidatePort(port);
        gateway.Run("Tcp.Forward", workspace, sandbox, cancellationToken, () => gateway.RequireRunning(workspace, sandbox));
        var init = new Wire.TcpForwardInit
        {
            Sandbox = sandbox,
            Workspace = workspace,
            ServiceId = options?.ServiceId ?? "",
            Tcp = new Wire.TcpRelayTarget { Host = "127.0.0.1", Port = port },
        };
        return await ForwardStream.OpenAsync(gateway.DuplexCore, init, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IForwardListener> ListenAsync(string sandbox, uint remotePort, int localPort = 0, ListenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ValidatePort(remotePort);
        ArgumentOutOfRangeException.ThrowIfNegative(localPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, ushort.MaxValue);
        options ??= new ListenOptions();
        var listener = gateway.Run("Tcp.Listen", workspace, sandbox, cancellationToken, () =>
        {
            _ = gateway.RequireRunning(workspace, sandbox);
            var endpoint = new IPEndPoint(options.BindAddress, localPort == 0 ? gateway.AllocatePort() : localPort);
            var created = new FakeListener(gateway, new FakeListenerInfo(sandbox, remotePort, endpoint, options.UseSshTunnel));
            gateway.Listeners.Add(created);
            return created;
        });
        return ValueTask.FromResult<IForwardListener>(listener);
    }

    private static void ValidatePort(uint port)
    {
        if (port is 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be in range 1-65535");
        }
    }
}
