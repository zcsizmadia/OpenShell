// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Oidc;

/// <summary><see cref="OidcOptions"/> with gateway fallbacks and defaults applied.</summary>
internal sealed class OidcResolved
{
    private static readonly string[] DefaultScopes = ["openid", "profile", "email"];

    private OidcResolved()
    {
    }

    public required string Issuer { get; init; }

    public required string ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public Func<CancellationToken, ValueTask<string>>? SecretProvider { get; init; }

    public string? Audience { get; init; }

    /// <summary>Scopes explicitly requested by the caller or the gateway; empty when none.</summary>
    public required IReadOnlyList<string> ExplicitScopes { get; init; }

    public required HttpClient Http { get; init; }

    public required TimeProvider Time { get; init; }

    public required OidcDiscovery Discovery { get; init; }

    public required OidcOptions Options { get; init; }

    public string? TokenDirectory { get; init; }

    /// <summary>Scopes for interactive flows: explicit scopes, else <c>openid profile email</c>.</summary>
    public IReadOnlyList<string> InteractiveScopes => ExplicitScopes.Count > 0 ? ExplicitScopes : DefaultScopes;

    public static OidcResolved Create(OidcOptions options, bool requireSecret)
    {
        ArgumentNullException.ThrowIfNull(options);
        var gateway = options.Gateway;
        var issuer = options.Issuer is { Length: > 0 } i ? i : gateway?.OidcIssuer;
        var clientId = options.ClientId is { Length: > 0 } c ? c : gateway?.OidcClientId;
        var audience = options.Audience ?? gateway?.OidcAudience;

        IReadOnlyList<string> scopes = options.Scopes is { } given
            ? [.. given]
            : gateway?.OidcScopes is { Length: > 0 } text
                ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(clientId))
        {
            throw new OidcException(OidcFailure.Config, "issuer and client ID are required (set Issuer and ClientId, or supply a Gateway)");
        }

        if (requireSecret && string.IsNullOrEmpty(options.ClientSecret) && options.ClientSecretProvider is null)
        {
            throw new OidcException(OidcFailure.ClientCredentials, "client secret is required (set ClientSecret or ClientSecretProvider)");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new OidcException(OidcFailure.Config, "timeout must be positive");
        }

        var http = options.HttpClient;
        var time = options.TimeProvider ?? TimeProvider.System;
        return new OidcResolved
        {
            Issuer = issuer,
            ClientId = clientId,
            ClientSecret = options.ClientSecret,
            SecretProvider = options.ClientSecretProvider,
            Audience = string.IsNullOrEmpty(audience) ? null : audience,
            ExplicitScopes = scopes,
            Http = http ?? OidcHttp.Shared,
            Time = time,
            Discovery = http is null ? OidcDiscovery.Shared : new OidcDiscovery(http, time),
            Options = options,
            TokenDirectory = options.TokenCacheDirectory ?? gateway?.Directory,
        };
    }
}
