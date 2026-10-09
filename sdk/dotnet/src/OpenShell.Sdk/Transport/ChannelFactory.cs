// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;

namespace OpenShell.Transport;

internal static class ChannelFactory
{
    public static void Validate(OpenShellClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var address = options.Address ?? throw Invalid("gateway address is required");
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
        {
            throw Invalid("gateway address must be an absolute http:// or https:// URL");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            throw Invalid("default workspace must not be empty");
        }

        if (options.Auth.SendsCredentials
            && address.Scheme == "http"
            && !options.AllowInsecureAuth
            && !IsLoopback(address))
        {
            throw Invalid(
                $"refusing to send credentials over plaintext http:// to non-loopback host '{address.Host}'; use https:// or set AllowInsecureAuth");
        }

        if (options.Tls is { ClientCertificate.HasPrivateKey: false })
        {
            throw Invalid("mTLS client certificate must include its private key");
        }
    }

    public static GrpcChannel Create(OpenShellClientOptions options)
    {
        Validate(options);
        var inner = options.HttpHandler ?? BuildSocketsHandler(options);
        var handler = new AuthHandler(options.Auth, inner);
        return GrpcChannel.ForAddress(options.Address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = null,
            LoggerFactory = options.LoggerFactory,
        });
    }

    internal static bool IsLoopback(Uri address) =>
        address.IsLoopback
        || (IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static SocketsHttpHandler BuildSocketsHandler(OpenShellClientOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
        };

        if (options.Address.Scheme != "https" || options.Tls is not { } tls)
        {
            return handler;
        }

        var ssl = new SslClientAuthenticationOptions();
        if (tls.ClientCertificate is { } cert)
        {
            ssl.ClientCertificates = [cert];
        }

        if (tls.InsecureSkipVerify)
        {
#pragma warning disable CA5359 // Explicit, documented development-only opt-in (TlsOptions.InsecureSkipVerify).
            ssl.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }
        else if (tls.CaCertificates is { Count: > 0 } roots)
        {
            ssl.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateAgainst(roots, certificate, errors);
        }

        handler.SslOptions = ssl;
        return handler;
    }

    internal static bool ValidateAgainst(X509Certificate2Collection roots, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        // Name mismatch is never excusable; chain errors are re-evaluated against the pinned roots.
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }

    private static OpenShellException Invalid(string message) => new(OpenShellErrorKind.InvalidConfig, message);
}

/// <summary>Applies the configured <see cref="IAuthProvider"/> to every outgoing request.</summary>
internal sealed class AuthHandler(IAuthProvider auth, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await auth.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
