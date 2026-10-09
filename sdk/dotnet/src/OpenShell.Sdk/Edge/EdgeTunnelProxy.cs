// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace OpenShell.Edge;

/// <summary>Opens the WebSocket leg of an <see cref="EdgeTunnelProxy"/> connection.</summary>
/// <param name="url">The <c>ws://</c> or <c>wss://</c> tunnel URL.</param>
/// <param name="headers">Edge authentication headers to send on the upgrade request.</param>
/// <param name="cancellationToken">Cancels the connect.</param>
public delegate ValueTask<WebSocket> WebSocketConnector(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

/// <summary>Settings for <see cref="EdgeTunnelProxy"/>.</summary>
public sealed record EdgeTunnelOptions
{
    /// <summary>Logger for connection lifecycle events. Credentials are never logged.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>How long <see cref="EdgeTunnelProxy.DisposeAsync"/> waits for open connections before cutting them. Default 5 s.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout for establishing each WebSocket. Default 10 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Customizes the default <see cref="ClientWebSocket"/> (TLS, proxy, certificates).</summary>
    public Action<ClientWebSocketOptions>? ConfigureWebSocket { get; init; }

    /// <summary>Replaces the WebSocket dialer entirely.</summary>
    public WebSocketConnector? Connector { get; init; }
}

/// <summary>
/// A local TCP proxy that tunnels each connection over an edge-authenticated WebSocket. Use it when the gateway sits behind
/// an edge (such as Cloudflare Access) that authenticates GET upgrades but not gRPC POSTs: point an <c>http://</c> client at
/// <see cref="LocalUri"/>.
/// </summary>
public sealed partial class EdgeTunnelProxy : IAsyncDisposable, IDisposable
{
    private const int BufferSize = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly Uri _tunnelUrl;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly EdgeTunnelOptions _options;
    private readonly WebSocketConnector _connector;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _force = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    private EdgeTunnelProxy(TcpListener listener, Uri tunnelUrl, string edgeToken, EdgeTunnelOptions options)
    {
        _listener = listener;
        _tunnelUrl = tunnelUrl;
        _options = options;
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cf-access-jwt-assertion"] = edgeToken,
            ["cookie"] = $"CF_Authorization={edgeToken}",
        };
        _connector = options.Connector ?? DefaultConnectorAsync;
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The local loopback endpoint the proxy listens on.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>The <c>http://127.0.0.1:port</c> address to give to <see cref="OpenShellClientOptions.Address"/>.</summary>
    public Uri LocalUri => new($"http://127.0.0.1:{LocalEndPoint.Port}");

    /// <summary>
    /// Starts a proxy. <c>https://</c> and <c>http://</c> gateway endpoints map to <c>wss://</c>/<c>ws://</c> with the
    /// <c>/_ws_tunnel</c> path the gateway serves; a <c>ws://</c> or <c>wss://</c> URL is used exactly as given.
    /// <c>ws://</c> is accepted only for loopback hosts because the token would otherwise cross the network in clear text.
    /// </summary>
    public static EdgeTunnelProxy Start(string gatewayEndpoint, string edgeToken, EdgeTunnelOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeToken);
        _ = Auth.CloudflareAccess(edgeToken); // Validates the token is cookie/header safe.
        options ??= new EdgeTunnelOptions();

        var url = ToTunnelUrl(gatewayEndpoint);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new EdgeTunnelProxy(listener, url, edgeToken, options);
    }

    internal static Uri ToTunnelUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw Invalid("gateway URL must be an absolute URL with a host");
        }

        Uri result;
        switch (uri.Scheme)
        {
            case "https":
            case "http":
                var scheme = uri.Scheme == "https" ? "wss" : "ws";
                result = new Uri($"{scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}/_ws_tunnel{uri.Query}");
                break;
            case "wss":
            case "ws":
                result = uri;
                break;
            default:
                throw Invalid("gateway URL must use http, https, ws or wss");
        }

        if (result.Scheme == "ws" && !IsLoopback(result))
        {
            throw Invalid("an edge token requires wss:// (ws:// is allowed only for loopback hosts)");
        }

        if (result.UserInfo.Length > 0)
        {
            throw Invalid("gateway URL must not contain credentials");
        }

        return result;
    }

    /// <summary>Stops accepting, lets open connections finish for <see cref="EdgeTunnelOptions.CloseTimeout"/>, then cuts them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);

        var pending = Task.WhenAll(_connections.Keys);
        var finished = await Task.WhenAny(pending, Task.Delay(_options.CloseTimeout)).ConfigureAwait(false);
        if (finished != pending)
        {
            if (_options.Logger is { } log)
            {
                LogCloseTimeout(log);
            }

            await _force.CancelAsync().ConfigureAwait(false);
            await pending.ConfigureAwait(false);
        }

        _lifetime.Dispose();
        _force.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [LoggerMessage(Level = LogLevel.Information, Message = "Edge tunnel close timeout reached; cutting open connections")]
    private static partial void LogCloseTimeout(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Edge tunnel connection established")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Edge tunnel connection failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    private static bool IsLoopback(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static OpenShellException Invalid(string message) => new(OpenShellErrorKind.InvalidConfig, message);

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var task = BridgeAsync(client);
            _connections[task] = 0;
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task BridgeAsync(TcpClient local)
    {
        using (local)
        {
            local.NoDelay = true;
            WebSocket? socket = null;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_force.Token))
                {
                    connectCts.CancelAfter(_options.ConnectTimeout);
                    socket = await _connector(_tunnelUrl, _headers, connectCts.Token).ConfigureAwait(false);
                }

                if (_options.Logger is { } log)
                {
                    LogConnected(log);
                }

                await PumpAsync(local, socket, _force.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (_options.Logger is { } log)
                {
                    LogFailed(log, e.GetType().Name);
                }
            }
            finally
            {
                socket?.Dispose();
            }
        }
    }

    private static async Task PumpAsync(TcpClient local, WebSocket socket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stream = local.GetStream();
        var upload = UploadAsync(stream, socket, stop.Token);
        var download = DownloadAsync(local, stream, socket, stop.Token);

        // The server closing ends the tunnel; the client finishing only half-closes it so the response can drain.
        var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
        if (first == upload && upload.IsCompletedSuccessfully)
        {
            await download.ConfigureAwait(false);
        }
        else
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(Observe(upload), Observe(download)).ConfigureAwait(false);
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Teardown races are expected once the other direction finished.
        }
    }

    private static async Task UploadAsync(NetworkStream stream, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                }

                return;
            }

            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    private static async Task DownloadAsync(TcpClient local, NetworkStream stream, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                local.Client.Shutdown(SocketShutdown.Send);
                return;
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<WebSocket> DefaultConnectorAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in headers)
            {
                socket.Options.SetRequestHeader(name, value);
            }

            _options.ConfigureWebSocket?.Invoke(socket.Options);
            await socket.ConnectAsync(url, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
