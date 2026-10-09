// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using OpenShell.Edge;
using OpenShell.Gateway;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class GatewayEdgeTunnelTests
{
    private static GatewayMetadata Config(string directory, GatewayAuthMode mode = GatewayAuthMode.CloudflareJwt) => new()
    {
        Name = "edge",
        Endpoint = new Uri("https://gw.example.com"),
        Directory = directory,
        AuthMode = mode,
    };

    private static (WebSocketConnector Connector, List<(Uri Url, IReadOnlyDictionary<string, string> Headers)> Seen) FailingConnector()
    {
        var seen = new List<(Uri, IReadOnlyDictionary<string, string>)>();
        return ((url, headers, _) =>
        {
            seen.Add((url, headers));
            throw new InvalidOperationException("edge unreachable in tests");
        }, seen);
    }

    private static GatewayConnectOptions Options(WebSocketConnector? connector = null, EdgeTunnelMode mode = EdgeTunnelMode.Auto, IAuthProvider? auth = null) => new()
    {
        EdgeTunnel = mode,
        Auth = auth,
        EdgeTunnelOptions = connector is null ? null : new EdgeTunnelOptions { Connector = connector },
    };

    private static async Task<bool> PortIsClosedAsync(int port)
    {
        using var probe = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    [Test]
    public async Task Cloudflare_gateway_is_reached_through_a_local_tunnel_with_the_edge_token()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "edge_token"), "edge.jwt-1");
        var (connector, seen) = FailingConnector();

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path), Options(connector));
        using (client)
        {
            await Assert.That(tunnel).IsNotNull();
            await Assert.That(tunnel!.LocalEndPoint.Port).IsGreaterThan(0);

            // A call travels to the local proxy, which dials the edge with the token in the upgrade headers.
            await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();
        }

        await Assert.That(seen.Count).IsGreaterThan(0);
        await Assert.That(seen[0].Url).IsEqualTo(new Uri("wss://gw.example.com/_ws_tunnel"));
        await Assert.That(seen[0].Headers["cf-access-jwt-assertion"]).IsEqualTo("edge.jwt-1");
        await Assert.That(seen[0].Headers["cookie"]).IsEqualTo("CF_Authorization=edge.jwt-1");
    }

    [Test]
    public async Task Disposing_the_client_stops_the_tunnel_it_owns()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "edge_token"), "tok");
        var (connector, _) = FailingConnector();

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path), Options(connector));
        var port = tunnel!.LocalEndPoint.Port;
        await Assert.That(await PortIsClosedAsync(port)).IsFalse();

        client.Dispose();

        await Assert.That(await PortIsClosedAsync(port)).IsTrue();
    }

    [Test]
    public async Task Never_mode_connects_directly_without_a_tunnel()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "edge_token"), "tok");

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path), Options(mode: EdgeTunnelMode.Never));
        using (client)
        {
            await Assert.That(tunnel).IsNull();
        }
    }

    [Test]
    [Arguments(GatewayAuthMode.None)]
    [Arguments(GatewayAuthMode.Plaintext)]
    public async Task Other_auth_modes_never_start_a_tunnel(GatewayAuthMode mode)
    {
        using var dir = new TempDirectory();

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path, mode), Options());
        using (client)
        {
            await Assert.That(tunnel).IsNull();
        }
    }

    [Test]
    public async Task A_caller_supplied_auth_provider_opts_out_of_the_tunnel()
    {
        using var dir = new TempDirectory();

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path), Options(auth: Auth.StaticToken("mine")));
        using (client)
        {
            await Assert.That(tunnel).IsNull();
        }
    }

    [Test]
    public async Task Missing_edge_token_fails_before_anything_listens()
    {
        using var dir = new TempDirectory();

        var ex = await Assert.That(() => GatewayClient.ConnectCore(Config(dir.Path), Options())).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Auth);
    }

    [Test]
    public async Task Connect_by_name_uses_the_tunnel_for_a_registered_cloudflare_gateway()
    {
        using var root = new TempDirectory();
        var gatewayDir = GatewayLocatorTests.Register(root, "user", "edge", """{"gateway_endpoint":"https://gw.example.com","auth_mode":"cloudflare_jwt"}""");
        await File.WriteAllTextAsync(Path.Combine(gatewayDir, "edge_token"), "tok");
        var (connector, seen) = FailingConnector();
        var locator = new GatewayLocator(new GatewayLocatorOptions { UserConfigDirectory = root.Combine("user"), SystemConfigDirectory = root.Combine("sys") });

        using var client = GatewayClient.Connect("edge", new GatewayConnectOptions { Locator = locator, EdgeTunnelOptions = new EdgeTunnelOptions { Connector = connector } });
        await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(seen.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Default_tunnel_options_are_built_when_none_are_given()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "edge_token"), "tok");

        var (client, tunnel) = GatewayClient.ConnectCore(Config(dir.Path), new GatewayConnectOptions());
        using (client)
        {
            await Assert.That(tunnel).IsNotNull();
        }
    }
}
