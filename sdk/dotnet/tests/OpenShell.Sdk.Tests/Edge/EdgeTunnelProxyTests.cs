// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenShell.Edge;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class EdgeTunnelUrlTests
{
    [Test]
    [Arguments("https://gw.example.com", "wss://gw.example.com/_ws_tunnel")]
    [Arguments("https://gw.example.com/", "wss://gw.example.com/_ws_tunnel")]
    [Arguments("https://gw.example.com:8443/base/", "wss://gw.example.com:8443/base/_ws_tunnel")]
    [Arguments("http://127.0.0.1:8080", "ws://127.0.0.1:8080/_ws_tunnel")]
    [Arguments("http://localhost:8080", "ws://localhost:8080/_ws_tunnel")]
    [Arguments("http://[::1]:8080", "ws://[::1]:8080/_ws_tunnel")]
    [Arguments("wss://gw.example.com/custom/path", "wss://gw.example.com/custom/path")]
    [Arguments("ws://127.0.0.1:9/custom", "ws://127.0.0.1:9/custom")]
    public async Task Maps_gateway_endpoints_to_tunnel_urls(string input, string expected)
    {
        await Assert.That(EdgeTunnelProxy.ToTunnelUrl(input).AbsoluteUri).IsEqualTo(new Uri(expected).AbsoluteUri);
    }

    [Test]
    [Arguments("http://gw.example.com")]
    [Arguments("ws://gw.example.com/x")]
    [Arguments("ftp://gw.example.com")]
    [Arguments("/relative")]
    [Arguments("wss://user:pw@gw.example.com/x")]
    public async Task Unsafe_or_invalid_endpoints_are_rejected(string input)
    {
        var ex = await Assert.That(() => EdgeTunnelProxy.ToTunnelUrl(input)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Start_validates_arguments_and_token_safety()
    {
        await Assert.That(() => EdgeTunnelProxy.Start("", "token")).Throws<ArgumentException>();
        await Assert.That(() => EdgeTunnelProxy.Start("https://gw.example.com", " ")).Throws<ArgumentException>();
        await Assert.That(() => EdgeTunnelProxy.Start("https://gw.example.com", "a;b")).Throws<OpenShellException>();
        await Assert.That(() => EdgeTunnelProxy.Start("http://remote.example.com", "token")).Throws<OpenShellException>();
    }
}

public class EdgeTunnelProxyTests
{
    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static async Task Echo(WebSocket server, bool asText = false)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await server.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await server.SendAsync(buffer.AsMemory(0, result.Count), asText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The test tore the connection down.
        }
    }

    private static EdgeTunnelProxy StartProxy(EdgeTunnelOptions options, string endpoint = "http://127.0.0.1:9") =>
        EdgeTunnelProxy.Start(endpoint, "edge.jwt-token", options);

    private static async Task<TcpClient> ConnectAsync(EdgeTunnelProxy proxy)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(proxy.LocalEndPoint);
        return client;
    }

    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static WebSocketConnector InMemoryEcho(List<(Uri Url, IReadOnlyDictionary<string, string> Headers)>? seen = null, bool asText = false) =>
        (url, headers, ct) =>
        {
            seen?.Add((url, headers));
            var (client, server) = InMemoryWebSocket.CreatePair();
            _ = Echo(server, asText);
            return ValueTask.FromResult<WebSocket>(client);
        };

    [Test]
    public async Task Exposes_a_loopback_endpoint_and_uri()
    {
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho() });

        await Assert.That(IPAddress.IsLoopback(proxy.LocalEndPoint.Address)).IsTrue();
        await Assert.That(proxy.LocalEndPoint.Port).IsGreaterThan(0);
        await Assert.That(proxy.LocalUri).IsEqualTo(new Uri($"http://127.0.0.1:{proxy.LocalEndPoint.Port}"));
    }

    [Test]
    public async Task Echoes_data_and_sends_edge_credentials_to_the_connector()
    {
        var seen = new List<(Uri Url, IReadOnlyDictionary<string, string> Headers)>();
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho(seen) }, "https://gw.example.com");
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();

        await stream.WriteAsync("hello tunnel"u8.ToArray());
        var echoed = await ReadExactly(stream, 12);

        await Assert.That(Encoding.UTF8.GetString(echoed)).IsEqualTo("hello tunnel");
        var (url, headers) = seen.Single();
        await Assert.That(url.AbsoluteUri).IsEqualTo("wss://gw.example.com/_ws_tunnel");
        await Assert.That(headers["cf-access-jwt-assertion"]).IsEqualTo("edge.jwt-token");
        await Assert.That(headers["Cookie"]).IsEqualTo("CF_Authorization=edge.jwt-token");
    }

    [Test]
    public async Task Large_payloads_are_chunked_and_reassembled_in_order()
    {
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho() });
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();
        var payload = new byte[1_500_000];
        Random.Shared.NextBytes(payload);

        var write = Task.Run(async () => await stream.WriteAsync(payload));
        var echoed = await ReadExactly(stream, payload.Length);
        await write;

        await Assert.That(echoed.AsSpan().SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Text_frames_from_the_server_are_delivered_as_bytes()
    {
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho(asText: true) });
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();

        await stream.WriteAsync("abc"u8.ToArray());

        await Assert.That(Encoding.UTF8.GetString(await ReadExactly(stream, 3))).IsEqualTo("abc");
    }

    [Test]
    public async Task Serves_several_connections_independently()
    {
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho() });
        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            using var client = await ConnectAsync(proxy);
            var stream = client.GetStream();
            var message = Encoding.UTF8.GetBytes($"conn-{i}");
            await stream.WriteAsync(message);
            return Encoding.UTF8.GetString(await ReadExactly(stream, message.Length));
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        await Assert.That(results).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => $"conn-{i}"));
    }

    [Test]
    public async Task Server_close_ends_the_local_connection()
    {
        var connector = new WebSocketConnector((_, _, ct) =>
        {
            var (client, server) = InMemoryWebSocket.CreatePair();
            _ = Task.Run(async () =>
            {
                var buffer = new byte[16];
                await server.ReceiveAsync(buffer, CancellationToken.None);
                await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }, ct);
            return ValueTask.FromResult<WebSocket>(client);
        });
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector });
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 1 });
        var read = await stream.ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task Client_half_close_still_lets_the_final_response_drain()
    {
        var connector = new WebSocketConnector((_, _, ct) =>
        {
            var (client, server) = InMemoryWebSocket.CreatePair();
            _ = Task.Run(async () =>
            {
                var buffer = new byte[16];
                while ((await server.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close)
                {
                }

                await server.SendAsync("final"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
                await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }, ct);
            return ValueTask.FromResult<WebSocket>(client);
        });
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector });
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();

        await stream.WriteAsync("request"u8.ToArray());
        client.Client.Shutdown(SocketShutdown.Send);

        await Assert.That(Encoding.UTF8.GetString(await ReadExactly(stream, 5))).IsEqualTo("final");
        await Assert.That(await stream.ReadAsync(new byte[4]).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);
    }

    [Test]
    public async Task Connector_failure_closes_the_local_connection_and_logs_without_credentials()
    {
        var logger = new ListLogger();
        var connector = new WebSocketConnector((_, _, ct) => throw new WebSocketException("handshake refused: edge.jwt-token"));
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector, Logger = logger });
        using var client = await ConnectAsync(proxy);

        var read = await client.GetStream().ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(read).IsEqualTo(0);
        await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue();
        await Assert.That(logger.Entries.Any(e => e.Message.Contains("edge.jwt-token", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Connect_timeout_cuts_a_stuck_dial()
    {
        var connector = new WebSocketConnector(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        await using var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector, ConnectTimeout = TimeSpan.FromMilliseconds(100) });
        using var client = await ConnectAsync(proxy);

        var read = await client.GetStream().ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_cuts_open_connections_after_the_close_timeout_and_is_idempotent()
    {
        var logger = new ListLogger();
        var connected = new TaskCompletionSource();
        var connector = new WebSocketConnector((_, _, ct) =>
        {
            var (client, _) = InMemoryWebSocket.CreatePair(); // The peer never answers.
            connected.TrySetResult();
            return ValueTask.FromResult<WebSocket>(client);
        });
        var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector, Logger = logger, CloseTimeout = TimeSpan.FromMilliseconds(200) });
        using var client = await ConnectAsync(proxy);
        await connected.Task;
        await Task.Delay(50);

        await proxy.DisposeAsync();
        await proxy.DisposeAsync();
        proxy.Dispose();

        var read = await client.GetStream().ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(read).IsEqualTo(0);
        await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("close timeout", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Dispose_waits_for_connections_that_finish_within_the_timeout()
    {
        var logger = new ListLogger();
        var connector = new WebSocketConnector((_, _, ct) =>
        {
            var (client, server) = InMemoryWebSocket.CreatePair();
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }, ct);
            return ValueTask.FromResult<WebSocket>(client);
        });
        var proxy = StartProxy(new EdgeTunnelOptions { Connector = connector, Logger = logger, CloseTimeout = TimeSpan.FromSeconds(10) });
        using var client = await ConnectAsync(proxy);
        await Task.Delay(30);

        await proxy.DisposeAsync();

        await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information)).IsFalse();
    }

    [Test]
    public async Task Synchronous_dispose_stops_the_listener()
    {
        var proxy = StartProxy(new EdgeTunnelOptions { Connector = InMemoryEcho() });
        var endpoint = proxy.LocalEndPoint;

        proxy.Dispose();

        // Rebinding the port succeeds only once the listener is closed. A refused connect would also show it, but on
        // Windows a connect to a closed loopback port retries for seconds and timed out under parallel test load.
        var rebound = new TcpListener(endpoint);
        await Assert.That(rebound.Start).ThrowsNothing();
        rebound.Stop();
    }

    // ---- default connector against a real WebSocket server ----

    private sealed class WsServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop = Task.CompletedTask;

        public WsServer()
        {
            Port = TestPorts.Free();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try
            {
                _listener.Start();
                Started = true;
                _loop = Task.Run(Loop);
            }
            catch (Exception e) when (e is HttpListenerException or PlatformNotSupportedException)
            {
                Started = false;
            }
        }

        public int Port { get; }

        public bool Started { get; }

        public ConcurrentQueue<(string Path, string? Jwt, string? Cookie)> Upgrades { get; } = new();

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    Upgrades.Enqueue((
                        context.Request.Url!.AbsolutePath,
                        context.Request.Headers["cf-access-jwt-assertion"],
                        context.Request.Headers["Cookie"]));
                    var accepted = await context.AcceptWebSocketAsync(null);
                    await Echo(accepted.WebSocket);
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Started)
            {
                _listener.Stop();
                await _loop;
            }

            _listener.Close();
        }
    }

    [Test]
    public async Task Default_connector_upgrades_with_edge_headers_and_echoes()
    {
        await using var server = new WsServer();
        if (!server.Started)
        {
            return; // HttpListener cannot bind here (for example a locked-down Windows account).
        }

        var configured = false;
        await using var proxy = EdgeTunnelProxy.Start(
            $"http://127.0.0.1:{server.Port}",
            "edge.jwt-token",
            new EdgeTunnelOptions { ConfigureWebSocket = _ => configured = true });
        using var client = await ConnectAsync(proxy);
        var stream = client.GetStream();

        await stream.WriteAsync("over the real wire"u8.ToArray());
        var echoed = await ReadExactly(stream, 18);

        await Assert.That(Encoding.UTF8.GetString(echoed)).IsEqualTo("over the real wire");
        await Assert.That(configured).IsTrue();
        var upgrade = server.Upgrades.Single();
        await Assert.That(upgrade.Path).IsEqualTo("/_ws_tunnel");
        await Assert.That(upgrade.Jwt).IsEqualTo("edge.jwt-token");
        await Assert.That(upgrade.Cookie).Contains("CF_Authorization=edge.jwt-token");
    }

    [Test]
    public async Task Default_connector_failure_closes_the_local_connection()
    {
        var deadPort = TestPorts.Free();
        await using var proxy = EdgeTunnelProxy.Start($"http://127.0.0.1:{deadPort}", "edge.jwt-token", new EdgeTunnelOptions { ConnectTimeout = TimeSpan.FromSeconds(5) });
        using var client = await ConnectAsync(proxy);

        var read = await client.GetStream().ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.That(read).IsEqualTo(0);
    }
}
