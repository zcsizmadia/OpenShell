// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Gateway;

/// <summary>How a registered gateway authenticates callers.</summary>
public enum GatewayAuthMode
{
    /// <summary>No <c>auth_mode</c> recorded.</summary>
    None = 0,

    /// <summary>Direct plaintext HTTP, typically loopback development.</summary>
    Plaintext = 1,

    /// <summary>Cloudflare Access JWT stored in <c>edge_token</c> (or legacy <c>cf_token</c>).</summary>
    CloudflareJwt = 2,

    /// <summary>OIDC bearer tokens stored in <c>oidc_token.json</c>.</summary>
    Oidc = 3,

    /// <summary>Mutual TLS using the bundle in the gateway <c>mtls</c> directory.</summary>
    Mtls = 4,
}

/// <summary>Where a gateway registration was found.</summary>
public enum GatewayConfigSource
{
    /// <summary>Per-user configuration directory.</summary>
    User = 0,

    /// <summary>System-wide configuration directory.</summary>
    System = 1,
}

/// <summary>A registered gateway read from <c>metadata.json</c>.</summary>
public sealed record GatewayMetadata
{
    /// <summary>Gateway name (the registration directory name).</summary>
    public required string Name { get; init; }

    /// <summary>Gateway endpoint URL.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Authentication mode.</summary>
    public GatewayAuthMode AuthMode { get; init; }

    /// <summary>Where the registration was found.</summary>
    public GatewayConfigSource Source { get; init; }

    /// <summary>Registration directory holding <c>metadata.json</c>, tokens and the mTLS bundle.</summary>
    public required string Directory { get; init; }

    /// <summary>OIDC issuer URL, when the gateway uses OIDC.</summary>
    public string? OidcIssuer { get; init; }

    /// <summary>OIDC client id.</summary>
    public string? OidcClientId { get; init; }

    /// <summary>OIDC audience.</summary>
    public string? OidcAudience { get; init; }

    /// <summary>Space-separated OIDC scopes.</summary>
    public string? OidcScopes { get; init; }
}

/// <summary>Summary of a registered gateway returned by <see cref="GatewayLocator.List"/>.</summary>
public sealed record GatewayEntry(string Name, bool Active, GatewayConfigSource Source);
