// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Gateway;

namespace OpenShell.Oidc;

/// <summary>Tokens returned by an OIDC flow.</summary>
public sealed record OidcTokens(string AccessToken, string? RefreshToken = null, string? TokenType = null, DateTimeOffset? ExpiresAt = null)
{
    /// <summary>Converts to the SDK's <see cref="AccessToken"/> (dropping the refresh token).</summary>
    public AccessToken ToAccessToken() => new(AccessToken, ExpiresAt);

    /// <inheritdoc />
    public override string ToString() => "OidcTokens { ... }";
}

/// <summary>Settings shared by every OIDC flow. Secrets are held in memory only and never logged.</summary>
public sealed record OidcOptions
{
    /// <summary>Issuer URL. Filled from <see cref="Gateway"/> when omitted.</summary>
    public string? Issuer { get; init; }

    /// <summary>OAuth client id. Filled from <see cref="Gateway"/> when omitted.</summary>
    public string? ClientId { get; init; }

    /// <summary>Client secret for confidential clients.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Supplies the client secret on demand, so it need not live in configuration.</summary>
    public Func<CancellationToken, ValueTask<string>>? ClientSecretProvider { get; init; }

    /// <summary>Audience; null leaves it unset (or takes it from <see cref="Gateway"/>).</summary>
    public string? Audience { get; init; }

    /// <summary>
    /// Scopes. Interactive flows default to <c>openid profile email</c>; client credentials sends none unless set.
    /// </summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Loopback callback port; 0 tries 8000 then 18000 and falls back to the keyboard flow.</summary>
    public int CallbackPort { get; init; }

    /// <summary>Overall timeout for a flow. Defaults to two minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Use the paste-the-code flow instead of the browser callback.</summary>
    public bool UseKeyboardFlow { get; init; }

    /// <summary>Do not read or write the on-disk token cache.</summary>
    public bool InMemory { get; init; }

    /// <summary>Directory holding <c>oidc_token.json</c>; defaults to the gateway directory.</summary>
    public string? TokenCacheDirectory { get; init; }

    /// <summary>Receives the verification URL and user code during the device flow; defaults to the console.</summary>
    public Action<string, string>? DisplayDeviceCode { get; init; }

    /// <summary>Opens the authorization URL; defaults to the OS browser.</summary>
    public Func<Uri, CancellationToken, ValueTask>? BrowserLauncher { get; init; }

    /// <summary>Input for the keyboard flow; defaults to standard input.</summary>
    public TextReader? KeyboardInput { get; init; }

    /// <summary>Output for the keyboard flow; defaults to standard error.</summary>
    public TextWriter? KeyboardOutput { get; init; }

    /// <summary>
    /// HTTP client for discovery and token calls. It must not follow redirects. Defaults to a shared client that does not.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Clock for expiry and polling; defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>A registered gateway supplying issuer, client, audience, scopes and the token directory.</summary>
    public GatewayMetadata? Gateway { get; init; }

    /// <inheritdoc />
    public override string ToString() => "OidcOptions { ... }";
}
