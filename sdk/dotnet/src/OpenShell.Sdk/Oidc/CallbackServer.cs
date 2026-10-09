// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenShell.Oidc;

/// <summary>
/// A single-purpose loopback HTTP listener that receives the OAuth redirect. Requests whose
/// <c>state</c> does not match are answered with 400 and ignored, so a stray local request cannot abort a login.
/// </summary>
internal sealed class CallbackServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 8192;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly string _expectedState;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;

    private CallbackServer(TcpListener listener, string expectedState)
    {
        _listener = listener;
        _expectedState = expectedState;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Starts listening on 127.0.0.1, or returns null when the port is unavailable.</summary>
    public static CallbackServer? TryStart(int port, string expectedState)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return null;
        }

        return new CallbackServer(listener, expectedState);
    }

    /// <summary>Completes with the authorization code, or faults when the provider reports an error.</summary>
    public Task<string> WaitForCodeAsync() => _result.Task;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Expected while shutting down.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, timeout.Token).ConfigureAwait(false);
                var (status, body) = Process(requestLine);
                var payload = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n{body}");
                await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // A broken or slow local client must not take down the listener.
            }
        }
    }

    private (string Status, string Body) Process(string? requestLine)
    {
        var parts = requestLine?.Split(' ');
        if (parts is not { Length: >= 2 } || parts[0] != "GET")
        {
            return ("405 Method Not Allowed", "method not allowed");
        }

        var target = parts[1];
        var queryStart = target.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? target : target[..queryStart];
        if (path != "/callback")
        {
            return ("404 Not Found", "not found");
        }

        var query = ParseQuery(queryStart < 0 ? string.Empty : target[(queryStart + 1)..]);
        query.TryGetValue("state", out var state);
        if (!string.Equals(state, _expectedState, StringComparison.Ordinal))
        {
            return ("400 Bad Request", "state mismatch");
        }

        if (query.TryGetValue("error", out var error) && error.Length > 0)
        {
            _result.TrySetException(new OidcException(OidcFailure.AuthCode, "provider returned an authorization error"));
            return ("400 Bad Request", "authorization failed");
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            return ("400 Bad Request", "missing authorization code");
        }

        _result.TrySetResult(code);
        return ("200 OK", "<html><body><h1>Login successful</h1><p>You can close this window.</p></body></html>");
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : WebUtility.UrlDecode(pair[(eq + 1)..]);
            map.TryAdd(key, value);
        }

        return map;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (newline >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, newline).TrimEnd('\r');
            }
        }

        return null;
    }
}
