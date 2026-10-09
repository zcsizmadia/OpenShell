// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OpenShell.Edge;

namespace OpenShell.Gateway;

/// <summary>How <see cref="GatewayClient"/> reaches a gateway fronted by Cloudflare Access.</summary>
public enum EdgeTunnelMode
{
    /// <summary>Start a local WebSocket tunnel for <c>cloudflare_jwt</c> gateways; connect directly for every other auth mode.</summary>
    Auto,

    /// <summary>Never tunnel; send the Access credentials as headers on direct gRPC calls.</summary>
    Never,
}

/// <summary>Settings for <see cref="GatewayClient"/>.</summary>
public sealed record GatewayConnectOptions
{
    /// <summary>Locates registrations; defaults to the OS layout.</summary>
    public GatewayLocator? Locator { get; init; }

    /// <summary>Replaces the authentication derived from the gateway's <c>auth_mode</c>.</summary>
    public IAuthProvider? Auth { get; init; }

    /// <summary>Replaces the TLS settings derived from the gateway's <c>mtls</c> directory.</summary>
    public TlsOptions? Tls { get; init; }

    /// <summary>Workspace used when none is given; defaults to <c>default</c>.</summary>
    public string? DefaultWorkspace { get; init; }

    /// <summary>Per-call deadline for unary calls.</summary>
    public TimeSpan? DefaultTimeout { get; init; }

    /// <summary>Renew an expired OIDC access token through the stored refresh token. Default true.</summary>
    public bool AutoRefresh { get; init; } = true;

    /// <summary>Persist renewed tokens back to <c>oidc_token.json</c> so the CLI sees rotation. Default true.</summary>
    public bool WriteBack { get; init; } = true;

    /// <summary>HTTP client for OIDC discovery and refresh (must not follow redirects).</summary>
    public HttpClient? OidcHttpClient { get; init; }

    /// <summary>Clock for token expiry.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// How to reach a Cloudflare Access gateway. <see cref="EdgeTunnelMode.Auto"/> (the default) routes gRPC through a
    /// local WebSocket tunnel, because the edge authenticates only GET requests.
    /// </summary>
    public EdgeTunnelMode EdgeTunnel { get; init; } = EdgeTunnelMode.Auto;

    /// <summary>Settings for the edge tunnel when one is started.</summary>
    public EdgeTunnelOptions? EdgeTunnelOptions { get; init; }

    /// <summary>Optional logger factory.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Replaces the HTTP handler (tests, custom proxies).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
}

/// <summary>Builds clients from gateways registered on disk by the OpenShell CLI.</summary>
public static class GatewayClient
{
    /// <summary>Connects to the named gateway, or the active one when <paramref name="name"/> is null.</summary>
    public static OpenShellClient Connect(string? name = null, GatewayConnectOptions? options = null)
    {
        options ??= new GatewayConnectOptions();
        var config = (options.Locator ?? new GatewayLocator()).Load(name);
        return ConnectCore(config, options).Client;
    }

    /// <summary>Builds the client for a loaded registration, returning the edge tunnel it owns when one was started.</summary>
    internal static (OpenShellClient Client, EdgeTunnelProxy? Tunnel) ConnectCore(GatewayMetadata config, GatewayConnectOptions options)
    {
        var clientOptions = CreateOptions(config, options);
        if (!UsesEdgeTunnel(config, options))
        {
            return (new OpenShellClient(clientOptions), null);
        }

        var proxy = EdgeTunnelProxy.Start(
            config.Endpoint.ToString(),
            LazyEdgeAuth.ReadToken(config.Directory),
            options.EdgeTunnelOptions ?? new EdgeTunnelOptions { Logger = options.LoggerFactory?.CreateLogger<EdgeTunnelProxy>() });
        try
        {
            // The tunnel carries plaintext gRPC to loopback; the edge terminates TLS on the far side.
            return (OpenShellClient.CreateOwning(clientOptions with { Address = proxy.LocalUri, Tls = null }, proxy), proxy);
        }
        catch
        {
            proxy.Dispose();
            throw;
        }
    }

    internal static bool UsesEdgeTunnel(GatewayMetadata config, GatewayConnectOptions options) =>
        options.EdgeTunnel == EdgeTunnelMode.Auto
        && config.AuthMode == GatewayAuthMode.CloudflareJwt
        && options.Auth is null;

    /// <summary>Derives client options (address, TLS, authentication) from a registration.</summary>
    public static OpenShellClientOptions CreateOptions(GatewayMetadata config, GatewayConnectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new GatewayConnectOptions();

        var auth = options.Auth ?? config.AuthMode switch
        {
            GatewayAuthMode.None or GatewayAuthMode.Plaintext or GatewayAuthMode.Mtls => Auth.None,
            GatewayAuthMode.CloudflareJwt => new LazyEdgeAuth(config.Directory),
            GatewayAuthMode.Oidc => new DiskOidcAuth(config, options),
            _ => throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "unsupported auth mode"),
        };

        var tls = options.Tls ?? (config.Endpoint.Scheme == Uri.UriSchemeHttps ? LoadTls(config) : null);
        return new OpenShellClientOptions
        {
            Address = config.Endpoint,
            Auth = auth,
            Tls = tls,
            DefaultWorkspace = options.DefaultWorkspace ?? "default",
            DefaultTimeout = options.DefaultTimeout,
            LoggerFactory = options.LoggerFactory,
            HttpHandler = options.HttpHandler,
        };
    }

    /// <summary>
    /// Loads the mTLS bundle (<c>mtls/ca.crt</c>, <c>tls.crt</c>, <c>tls.key</c>), using the strongest trust profile available:
    /// full mTLS, CA-only, or system roots.
    /// </summary>
    internal static TlsOptions LoadTls(GatewayMetadata config)
    {
        var dir = Path.Combine(config.Directory, "mtls");
        var caPath = Path.Combine(dir, "ca.crt");
        var certPath = Path.Combine(dir, "tls.crt");
        var keyPath = Path.Combine(dir, "tls.key");
        var hasCa = File.Exists(caPath);
        var hasPair = File.Exists(certPath) && File.Exists(keyPath);

        if (config.AuthMode == GatewayAuthMode.Mtls && !hasPair)
        {
            throw new OpenShellException(OpenShellErrorKind.InvalidConfig, $"gateway '{config.Name}' uses mTLS but its mtls directory lacks tls.crt or tls.key");
        }

        try
        {
            X509Certificate2Collection? roots = null;
            if (hasCa)
            {
                roots = [];
                roots.ImportFromPemFile(caPath);
            }

            return new TlsOptions
            {
                CaCertificates = roots,
                ClientCertificate = hasPair ? LoadClientCertificate(certPath, keyPath) : null,
            };
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or ArgumentException)
        {
            throw new OpenShellException(OpenShellErrorKind.Tls, $"cannot load the mTLS bundle for gateway '{config.Name}'", e);
        }
    }

    private static X509Certificate2 LoadClientCertificate(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        // Re-import through PKCS#12 so the private key is usable by SChannel on Windows.
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
#else
        return new X509Certificate2(pem.Export(X509ContentType.Pfx));
#endif
    }
}
