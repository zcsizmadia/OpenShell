// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OpenShell;

/// <summary>TLS settings for an <c>https://</c> gateway.</summary>
public sealed record TlsOptions
{
    /// <summary>Private CA certificates to trust instead of the system roots.</summary>
    public X509Certificate2Collection? CaCertificates { get; init; }

    /// <summary>Client certificate for mTLS. The default local gateway requires one.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>Disables server certificate validation. Development only.</summary>
    public bool InsecureSkipVerify { get; init; }
}

/// <summary>Configures an <see cref="OpenShellClient"/>.</summary>
public sealed record OpenShellClientOptions
{
    /// <summary>Gateway address, for example <c>https://gateway.example.com</c>.</summary>
    public required Uri Address { get; init; }

    /// <summary>Authentication; defaults to <see cref="Auth.None"/>.</summary>
    public IAuthProvider Auth { get; init; } = OpenShell.Auth.None;

    /// <summary>TLS settings; ignored for <c>http://</c>.</summary>
    public TlsOptions? Tls { get; init; }

    /// <summary>Workspace used when none is given. Defaults to <c>default</c>.</summary>
    public string DefaultWorkspace { get; init; } = "default";

    /// <summary>Deadline applied to unary calls that do not set their own. Null means none.</summary>
    public TimeSpan? DefaultTimeout { get; init; }

    /// <summary>
    /// Permits sending credentials over plaintext <c>http://</c> to a non-loopback host.
    /// Off by default because it leaks the credential on the wire.
    /// </summary>
    public bool AllowInsecureAuth { get; init; }

    /// <summary>Retry behaviour for transient failures. Defaults to <see cref="RetryPolicy.Default"/>; use <see cref="RetryPolicy.None"/> to disable.</summary>
    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

    /// <summary>Optional logger factory.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Throws <see cref="OpenShellException"/> (<see cref="OpenShellErrorKind.InvalidConfig"/>) when the options are inconsistent or unsafe, without connecting.</summary>
    public void Validate() => Transport.ChannelFactory.Validate(this);

    /// <summary>Replaces the HTTP handler. Intended for tests and custom proxies; <see cref="Tls"/> is then ignored.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
}
