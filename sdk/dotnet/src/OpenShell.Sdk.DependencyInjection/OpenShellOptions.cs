// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using Grpc.Core;

namespace OpenShell.DependencyInjection;

/// <summary>
/// Mutable, configuration-bindable settings for one gateway. <see cref="OpenShellClientOptions"/> is immutable,
/// so the options pattern binds to this class and the factory converts it.
/// </summary>
public sealed class OpenShellOptions
{
    /// <summary>Gateway address, for example <c>https://gateway.example.com</c>. Required.</summary>
    public Uri? Address { get; set; }

    /// <summary>Workspace used when none is given. Defaults to <c>default</c>.</summary>
    public string Workspace { get; set; } = "default";

    /// <summary>Static bearer token. Prefer a token provider set through <see cref="Auth"/> for anything that expires.</summary>
    public string? Token { get; set; }

    /// <summary>Path to a PEM file with the CA certificate(s) to trust instead of the system roots.</summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>Path to a PEM client certificate for mTLS.</summary>
    public string? ClientCertificatePath { get; set; }

    /// <summary>Path to the PEM private key for <see cref="ClientCertificatePath"/>; omit when the key is in the certificate file.</summary>
    public string? ClientKeyPath { get; set; }

    /// <summary>Disables server certificate validation. Development only.</summary>
    public bool InsecureSkipVerify { get; set; }

    /// <summary>Permits credentials over plaintext <c>http://</c> to a non-loopback host.</summary>
    public bool AllowInsecureAuth { get; set; }

    /// <summary>Deadline applied to unary calls that do not set their own.</summary>
    public TimeSpan? DefaultTimeout { get; set; }

    /// <summary>Retry settings. Null keeps <see cref="RetryPolicy.Default"/>.</summary>
    public RetryOptions? Retry { get; set; }

    /// <summary>An authentication provider built in code. Takes precedence over <see cref="Token"/>; not bindable from configuration.</summary>
    public IAuthProvider? Auth { get; set; }

    /// <summary>TLS settings built in code. Takes precedence over the certificate path settings; not bindable from configuration.</summary>
    public TlsOptions? Tls { get; set; }

    internal OpenShellClientOptions ToClientOptions(Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory)
    {
        return new OpenShellClientOptions
        {
            Address = Address ?? throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "OpenShell:Address is required"),
            DefaultWorkspace = Workspace,
            Auth = Auth ?? (string.IsNullOrWhiteSpace(Token) ? OpenShell.Auth.None : OpenShell.Auth.StaticToken(Token)),
            Tls = Tls ?? BuildTls(),
            AllowInsecureAuth = AllowInsecureAuth,
            DefaultTimeout = DefaultTimeout,
            Retry = Retry?.ToPolicy() ?? RetryPolicy.Default,
            LoggerFactory = loggerFactory,
        };
    }

    private TlsOptions? BuildTls()
    {
        if (string.IsNullOrEmpty(CaCertificatePath) && string.IsNullOrEmpty(ClientCertificatePath) && !InsecureSkipVerify)
        {
            return null;
        }

        X509Certificate2Collection? roots = null;
        if (!string.IsNullOrEmpty(CaCertificatePath))
        {
            roots = [];
            roots.ImportFromPemFile(CaCertificatePath);
        }

        X509Certificate2? client = null;
        if (!string.IsNullOrEmpty(ClientCertificatePath))
        {
            using var pem = X509Certificate2.CreateFromPemFile(ClientCertificatePath, string.IsNullOrEmpty(ClientKeyPath) ? null : ClientKeyPath);

            // Round-trip through PKCS#12 so Windows SChannel can use the ephemeral key for client authentication.
#pragma warning disable SYSLIB0057 // The byte-array constructor is the only form available on both net8.0 and net10.0.
            client = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
#pragma warning restore SYSLIB0057
        }

        return new TlsOptions { CaCertificates = roots, ClientCertificate = client, InsecureSkipVerify = InsecureSkipVerify };
    }
}

/// <summary>Bindable retry settings; unset values keep the <see cref="RetryPolicy.Default"/> values.</summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first. 1 disables retries.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan? InitialBackoff { get; set; }

    /// <summary>Upper bound for any single delay.</summary>
    public TimeSpan? MaxBackoff { get; set; }

    /// <summary>Growth factor between delays.</summary>
    public double? BackoffMultiplier { get; set; }

    /// <summary>Fraction (0 to 1) of each delay that is randomized.</summary>
    public double? Jitter { get; set; }

    /// <summary>Status codes worth retrying.</summary>
    public StatusCode[]? RetryableStatusCodes { get; set; }

    internal RetryPolicy ToPolicy()
    {
        var policy = RetryPolicy.Default;
        return policy with
        {
            MaxAttempts = MaxAttempts ?? policy.MaxAttempts,
            InitialBackoff = InitialBackoff ?? policy.InitialBackoff,
            MaxBackoff = MaxBackoff ?? policy.MaxBackoff,
            BackoffMultiplier = BackoffMultiplier ?? policy.BackoffMultiplier,
            Jitter = Jitter ?? policy.Jitter,
            RetryableStatusCodes = RetryableStatusCodes is { } codes ? new HashSet<StatusCode>(codes) : policy.RetryableStatusCodes,
        };
    }
}
