// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>A short-lived SSH relay session. The token is a credential and is redacted from <see cref="object.ToString"/>.</summary>
public sealed record SshSession
{
    /// <summary>Id of the sandbox the session targets.</summary>
    public required string SandboxId { get; init; }

    /// <summary>Session token; treat as a secret.</summary>
    public required string Token { get; init; }

    /// <summary>Gateway host to connect to.</summary>
    public required string GatewayHost { get; init; }

    /// <summary>Gateway port to connect to.</summary>
    public required int GatewayPort { get; init; }

    /// <summary>Gateway scheme, <c>http</c> or <c>https</c>.</summary>
    public required string GatewayScheme { get; init; }

    /// <summary>Host key fingerprint to pin, when the gateway provides one.</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>Absolute expiry; null means none.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("SandboxId = ").Append(SandboxId)
            .Append(", Token = <redacted>, GatewayHost = ").Append(GatewayHost)
            .Append(", GatewayPort = ").Append(GatewayPort)
            .Append(", GatewayScheme = ").Append(GatewayScheme);
        return true;
    }
}

/// <summary>Options for <see cref="ISshClient.TunnelAsync"/>.</summary>
public sealed record TunnelOptions
{
    /// <summary>Optional service identifier for audit and correlation.</summary>
    public string? ServiceId { get; init; }
}

/// <summary>SSH relay sessions and tunnels.</summary>
public interface ISshClient
{
    /// <summary>
    /// Creates an SSH session. The gateway response is validated against the proto contract before it is returned,
    /// because its fields end up in a <c>ProxyCommand</c> run by a local shell.
    /// </summary>
    ValueTask<SshSession> CreateSessionAsync(string sandbox, CancellationToken cancellationToken = default);

    /// <summary>Revokes a session token.</summary>
    ValueTask<DeletionResult> RevokeSessionAsync(string token, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a raw SSH relay stream to the sandbox. The session is created for the tunnel and revoked
    /// when the returned stream is disposed.
    /// </summary>
    ValueTask<ForwardStream> TunnelAsync(string sandbox, TunnelOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed partial class SshClient(ClientCore core, string workspace, ISandboxClient sandboxes) : ISshClient
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<SshSession> CreateSessionAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var request = new Wire.CreateSshSessionRequest { WorkspaceScope = ClientCore.Scope(workspace), Sandbox = sandbox };
        var response = await core.UnaryAsync(o => core.Grpc.CreateSshSessionAsync(request, o), cancellationToken).ConfigureAwait(false);
        Validate(response, expectedSandboxId: null);
        return new SshSession
        {
            SandboxId = response.SandboxId,
            Token = response.Token,
            GatewayHost = response.GatewayHost,
            GatewayPort = (int)response.GatewayPort,
            GatewayScheme = response.GatewayScheme,
            HostKeyFingerprint = response.HostKeyFingerprint is { Length: > 0 } fp ? fp : null,
            ExpiresAt = response.ExpirationTime?.ToDateTimeOffset(),
        };
    }

    public async ValueTask<DeletionResult> RevokeSessionAsync(string token, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var request = new Wire.RevokeSshSessionRequest { Token = token, AllowMissing = options?.AllowMissing ?? false };
        var response = await core.UnaryAsync(o => core.Grpc.RevokeSshSessionAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DeletionResult((DeletionOutcome)(int)response.Outcome);
    }

    public async ValueTask<ForwardStream> TunnelAsync(string sandbox, TunnelOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var existing = await sandboxes.GetAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var session = await CreateSessionAsync(sandbox, cancellationToken).ConfigureAwait(false);
        if (session.SandboxId != existing.Id && existing.Id.Length > 0)
        {
            await RevokeQuietlyAsync(session.Token).ConfigureAwait(false);
            throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "CreateSshSession response sandbox_id does not match the requested sandbox");
        }

        var init = new Wire.TcpForwardInit
        {
            Sandbox = sandbox,
            Workspace = workspace,
            ServiceId = options?.ServiceId ?? "",
            AuthorizationToken = session.Token,
            Ssh = new Wire.SshRelayTarget(),
        };
        try
        {
            return await ForwardStream.OpenAsync(core, init, cancellationToken, () => RevokeQuietlyAsync(session.Token)).ConfigureAwait(false);
        }
        catch
        {
            await RevokeQuietlyAsync(session.Token).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Throws <see cref="OpenShellErrorKind.InvalidConfig"/> when a field violates the CreateSshSession contract.</summary>
    internal static void Validate(Wire.CreateSshSessionResponse r, string? expectedSandboxId)
    {
        if (!SandboxIdPattern().IsMatch(r.SandboxId))
        {
            throw Reject("sandbox_id", "must match [A-Za-z0-9._-]{1,128}");
        }

        if (expectedSandboxId is not null && r.SandboxId != expectedSandboxId)
        {
            throw Reject("sandbox_id", $"must match requested sandbox '{expectedSandboxId}'");
        }

        var tokenBytes = Encoding.UTF8.GetByteCount(r.Token);
        if (tokenBytes is < 1 or > 4096 || !TokenPattern().IsMatch(r.Token))
        {
            throw Reject("token", "must be 1..4096 bytes of [A-Za-z0-9._~+/=-]");
        }

        var hostBytes = Encoding.UTF8.GetByteCount(r.GatewayHost);
        if (hostBytes is < 1 or > 253 || !IsValidGatewayHost(r.GatewayHost))
        {
            throw Reject("gateway_host", "must be a valid DNS name, IPv4 address, or bracketed IPv6 address");
        }

        if (r.GatewayPort is < 1 or > ushort.MaxValue)
        {
            throw Reject("gateway_port", "must be an integer in 1..65535");
        }

        if (r.GatewayScheme is not ("http" or "https"))
        {
            throw Reject("gateway_scheme", "must be exactly 'http' or 'https'");
        }

        if (r.HostKeyFingerprint.Length > 0
            && (Encoding.UTF8.GetByteCount(r.HostKeyFingerprint) > 256 || !FingerprintPattern().IsMatch(r.HostKeyFingerprint)))
        {
            throw Reject("host_key_fingerprint", "must be at most 256 bytes of [A-Za-z0-9:+/=-] when non-empty");
        }
    }

    private static bool IsValidGatewayHost(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
        }

        // A strict dotted quad is an IPv4 address; anything else is judged as a DNS name.
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3)
        {
            return true;
        }

        var dns = host.EndsWith('.') ? host[..^1] : host;
        if (dns.Length == 0)
        {
            return false;
        }

        foreach (var label in dns.Split('.'))
        {
            if (label.Length is < 1 or > 63 || !DnsLabelPattern().IsMatch(label))
            {
                return false;
            }
        }

        return true;
    }

    private static OpenShellException Reject(string field, string detail) =>
        new(OpenShellErrorKind.InvalidConfig, $"CreateSshSession response {field} {detail}");

    private async ValueTask RevokeQuietlyAsync(string token)
    {
        using var cts = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await RevokeSessionAsync(token, new DeleteOptions { AllowMissing = true }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OpenShellException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: an unrevoked session expires on its own.
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}\z")]
    private static partial Regex SandboxIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._~+/=-]+\z")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[A-Za-z0-9:+/=-]+\z")]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z")]
    private static partial Regex DnsLabelPattern();
}
