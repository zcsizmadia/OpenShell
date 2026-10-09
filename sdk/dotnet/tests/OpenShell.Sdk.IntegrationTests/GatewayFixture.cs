// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace OpenShell.IntegrationTests;

/// <summary>
/// Connects to a real gateway described by environment variables. Every test skips when
/// <c>OPENSHELL_GATEWAY_ADDRESS</c> is unset, so a plain <c>dotnet test</c> stays green without one.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>OPENSHELL_GATEWAY_ADDRESS</c>: gateway URL, for example <c>https://127.0.0.1:8080</c>.</item>
/// <item><c>OPENSHELL_TOKEN</c>: optional OIDC bearer token.</item>
/// <item><c>OPENSHELL_CA_CERT</c>, <c>OPENSHELL_CLIENT_CERT</c>, <c>OPENSHELL_CLIENT_KEY</c>: optional PEM paths for a pinned CA and mTLS.</item>
/// <item><c>OPENSHELL_TLS_INSECURE=1</c>: skip server certificate validation (development only).</item>
/// <item><c>OPENSHELL_DOTNET_SDK_TEST_IMAGE</c>: sandbox image; defaults to the one the Go SDK tests use.</item>
/// <item><c>OPENSHELL_WORKSPACE</c>: workspace to use; defaults to <c>default</c>.</item>
/// </list>
/// </remarks>
internal static class GatewayFixture
{
    public const string DefaultImage = "nvcr.io/nvidia/base/ubuntu:24.04";

    public static string Image => Environment.GetEnvironmentVariable("OPENSHELL_DOTNET_SDK_TEST_IMAGE") is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>Creates a client, or skips the current test when no gateway is configured.</summary>
    public static OpenShellClient Connect()
    {
        if (Environment.GetEnvironmentVariable("OPENSHELL_GATEWAY_ADDRESS") is not { Length: > 0 } address)
        {
            Skip.Test("OPENSHELL_GATEWAY_ADDRESS is not set");
            throw new InvalidOperationException("unreachable");
        }

        var token = Environment.GetEnvironmentVariable("OPENSHELL_TOKEN");
        return new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri(address),
            Auth = string.IsNullOrEmpty(token) ? Auth.None : Auth.StaticToken(token),
            Tls = BuildTls(),
            DefaultWorkspace = Environment.GetEnvironmentVariable("OPENSHELL_WORKSPACE") is { Length: > 0 } ws ? ws : "default",
        });
    }

    /// <summary>A unique, DNS-safe sandbox name for one test run.</summary>
    public static string UniqueName(string prefix) => $"{prefix}-{Random.Shared.Next(100_000_000, 999_999_999)}";

    private static TlsOptions? BuildTls()
    {
        var ca = Environment.GetEnvironmentVariable("OPENSHELL_CA_CERT");
        var cert = Environment.GetEnvironmentVariable("OPENSHELL_CLIENT_CERT");
        var key = Environment.GetEnvironmentVariable("OPENSHELL_CLIENT_KEY");
        var insecure = Environment.GetEnvironmentVariable("OPENSHELL_TLS_INSECURE") == "1";
        if (string.IsNullOrEmpty(ca) && string.IsNullOrEmpty(cert) && !insecure)
        {
            return null;
        }

        X509Certificate2? clientCert = null;
        if (!string.IsNullOrEmpty(cert))
        {
            // Round-trip through PFX so SChannel can use the ephemeral key for client authentication on Windows.
            using var pem = X509Certificate2.CreateFromPemFile(cert, string.IsNullOrEmpty(key) ? null : key);
#pragma warning disable SYSLIB0057 // Loading from a byte array is the only portable way across net8.0 and net10.0 here.
            clientCert = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
#pragma warning restore SYSLIB0057
        }

        X509Certificate2Collection? roots = null;
        if (!string.IsNullOrEmpty(ca))
        {
            roots = [];
            roots.ImportFromPemFile(ca);
        }

        return new TlsOptions { CaCertificates = roots, ClientCertificate = clientCert, InsecureSkipVerify = insecure };
    }
}
