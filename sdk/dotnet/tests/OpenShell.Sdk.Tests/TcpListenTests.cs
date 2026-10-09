// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class TcpListenTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<System.Net.Sockets.TcpClient> ConnectAsync(IForwardListener listener)
    {
        var client = new System.Net.Sockets.TcpClient();
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync(listener.LocalEndPoint, cts.Token);
        return client;
    }

    private static async Task<string> ReadAsync(NetworkStream stream, int count)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer);
    }

    [Test]
    public async Task Listen_bridges_local_connections_to_the_sandbox()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 8080, options: new ListenOptions { ServiceId = "svc" });

        await Assert.That(listener.LocalEndPoint.Port).IsGreaterThan(0);
        await Assert.That(IPAddress.IsLoopback(listener.LocalEndPoint.Address)).IsTrue();

        using var conn = await ConnectAsync(listener);
        var stream = conn.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping"));

        await Assert.That(await ReadAsync(stream, 4)).IsEqualTo("ping");
        var init = Relay.Channel(invoker).Received[0].Init;
        await Assert.That(init.Tcp.Port).IsEqualTo(8080u);
        await Assert.That(init.ServiceId).IsEqualTo("svc");
    }

    [Test]
    public async Task Listen_serves_several_connections_independently()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 80);

        using var a = await ConnectAsync(listener);
        using var b = await ConnectAsync(listener);
        await a.GetStream().WriteAsync(Encoding.UTF8.GetBytes("aaa"));
        await b.GetStream().WriteAsync(Encoding.UTF8.GetBytes("bbb"));

        await Assert.That(await ReadAsync(b.GetStream(), 3)).IsEqualTo("bbb");
        await Assert.That(await ReadAsync(a.GetStream(), 3)).IsEqualTo("aaa");
        await Assert.That(invoker.CountOf("ForwardTcp")).IsEqualTo(2);
    }

    [Test]
    public async Task A_local_half_close_still_delivers_the_response()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", async ch =>
        {
            await foreach (var _ in ch.ReadAllAsync())
            {
            }

            ch.Send(Relay.Data("done"));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 80);

        using var conn = await ConnectAsync(listener);
        var stream = conn.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("req"));
        conn.Client.Shutdown(SocketShutdown.Send);

        await Assert.That(await ReadAsync(stream, 4)).IsEqualTo("done");
    }

    [Test]
    public async Task The_local_connection_closes_when_the_remote_side_finishes()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", ch =>
        {
            ch.Send(Relay.Data("hi"));
            return Task.CompletedTask;
        });
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 80);

        using var conn = await ConnectAsync(listener);
        var stream = conn.GetStream();
        await Assert.That(await ReadAsync(stream, 2)).IsEqualTo("hi");

        using var cts = new CancellationTokenSource(Timeout);
        await Assert.That(await stream.ReadAsync(new byte[1], cts.Token)).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_open_drops_that_connection_but_the_listener_keeps_serving()
    {
        var gets = 0;
        var invoker = new FakeCallInvoker()
            .Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ =>
                Interlocked.Increment(ref gets) == 1
                    ? throw new RpcException(new Status(StatusCode.Unavailable, "flaky"))
                    : ClientTests.SandboxReply("sb"))
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 80);

        using (var first = await ConnectAsync(listener))
        {
            using var cts = new CancellationTokenSource(Timeout);
            var n = 0;
            try
            {
                n = await first.GetStream().ReadAsync(new byte[1], cts.Token);
            }
            catch (IOException)
            {
                // A reset also means the connection was dropped.
            }

            await Assert.That(n).IsEqualTo(0);
        }

        using var second = await ConnectAsync(listener);
        await second.GetStream().WriteAsync(Encoding.UTF8.GetBytes("ok"));
        await Assert.That(await ReadAsync(second.GetStream(), 2)).IsEqualTo("ok");
    }

    [Test]
    public async Task Listen_over_ssh_uses_a_relay_session_per_connection()
    {
        var invoker = Relay.WithSandbox()
            .Unary<Wire.CreateSshSessionRequest, Wire.CreateSshSessionResponse>("CreateSshSession", _ => new Wire.CreateSshSessionResponse
            {
                SandboxId = "id-1",
                Token = "tok",
                GatewayHost = "gw.example.com",
                GatewayPort = 443,
                GatewayScheme = "https",
            })
            .Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", _ => new Wire.RevokeSshSessionResponse())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = ClientTests.NewClient(invoker);
        await using var listener = await client.Tcp.ListenAsync("sb", 22, options: new ListenOptions { UseSshTunnel = true, ServiceId = "svc" });

        using var conn = await ConnectAsync(listener);
        await conn.GetStream().WriteAsync(Encoding.UTF8.GetBytes("SSH"));
        await Assert.That(await ReadAsync(conn.GetStream(), 3)).IsEqualTo("SSH");

        var init = Relay.Channel(invoker).Received[0].Init;
        await Assert.That(init.TargetCase).IsEqualTo(Wire.TcpForwardInit.TargetOneofCase.Ssh);
        await Assert.That(init.AuthorizationToken).IsEqualTo("tok");
        await Assert.That(init.ServiceId).IsEqualTo("svc");
    }

    [Test]
    public async Task Disposing_the_listener_tears_down_active_connections_and_refuses_new_ones()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        var listener = await client.Tcp.ListenAsync("sb", 80);
        var endpoint = listener.LocalEndPoint;
        var conn = await ConnectAsync(listener);
        await conn.GetStream().WriteAsync(Encoding.UTF8.GetBytes("x"));
        await ReadAsync(conn.GetStream(), 1);

        await listener.DisposeAsync();
        await listener.DisposeAsync();

        await Assert.That(Relay.Channel(invoker).ClientDisposed).IsTrue();
        using var cts = new CancellationTokenSource(Timeout);
        var n = 0;
        try
        {
            n = await conn.GetStream().ReadAsync(new byte[1], cts.Token);
        }
        catch (IOException)
        {
            // A reset also means the connection was torn down.
        }

        await Assert.That(n).IsEqualTo(0);
        conn.Dispose();
        using var late = new System.Net.Sockets.TcpClient();
        await Assert.That(async () => await late.ConnectAsync(endpoint, cts.Token)).Throws<SocketException>();
    }

    [Test]
    public async Task Listen_validates_arguments()
    {
        using var client = ClientTests.NewClient(Relay.EchoInvoker());

        await Assert.That(async () => await client.Tcp.ListenAsync("", 80)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("sb", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("sb", 80, localPort: -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("sb", 80, localPort: 65536)).Throws<ArgumentOutOfRangeException>();
    }
}
