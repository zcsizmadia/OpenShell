// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace OpenShell.Oidc;

/// <summary>The trusted subset of an OpenID Connect discovery document.</summary>
public sealed record OidcProviderMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? DeviceAuthorizationEndpoint,
    IReadOnlyList<string> CodeChallengeMethodsSupported)
{
    /// <summary>True when the provider advertises PKCE with S256.</summary>
    public bool SupportsS256 => CodeChallengeMethodsSupported.Contains("S256", StringComparer.Ordinal);
}

/// <summary>Fetches and validates OpenID Connect discovery documents, caching them for ten minutes.</summary>
public sealed class OidcDiscovery
{
    internal const int MaxResponseBytes = 1 << 20;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (OidcProviderMetadata Metadata, DateTimeOffset FetchedAt)> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a discovery client. <paramref name="http"/> must not follow redirects.</summary>
    public OidcDiscovery(HttpClient? http = null, TimeProvider? timeProvider = null)
    {
        _http = http ?? OidcHttp.Shared;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A process-wide instance using the shared no-redirect HTTP client.</summary>
    public static OidcDiscovery Shared { get; } = new();

    /// <summary>Discovers the provider for <paramref name="issuer"/>.</summary>
    public async ValueTask<OidcProviderMetadata> DiscoverAsync(string issuer, CancellationToken cancellationToken = default)
    {
        var key = Normalize(issuer);
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && now < hit.FetchedAt + CacheTtl)
        {
            return hit.Metadata;
        }

        var metadata = await FetchAsync(key, cancellationToken).ConfigureAwait(false);
        _cache[key] = (metadata, now);
        return metadata;
    }

    internal static string Normalize(string issuer) => issuer.TrimEnd('/');

    private async Task<OidcProviderMetadata> FetchAsync(string issuer, CancellationToken cancellationToken)
    {
        OidcUrl.Validate("issuer", issuer);
        using var request = new HttpRequestMessage(HttpMethod.Get, issuer + "/.well-known/openid-configuration");
        var (status, body) = await OidcHttp.SendAsync(_http, request, OidcFailure.Discovery, "discovery", cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw new OidcException(OidcFailure.Discovery, $"discovery endpoint returned HTTP {(int)status}");
        }

        OidcDiscoveryDto? doc;
        try
        {
            doc = JsonSerializer.Deserialize(body, SdkJsonContext.Default.OidcDiscoveryDto);
        }
        catch (JsonException)
        {
            throw new OidcException(OidcFailure.Discovery, "invalid discovery JSON");
        }

        if (doc is null || Normalize(doc.Issuer ?? "") != issuer)
        {
            throw new OidcException(OidcFailure.Discovery, "discovery issuer does not match configured issuer");
        }

        if (string.IsNullOrEmpty(doc.TokenEndpoint))
        {
            throw new OidcException(OidcFailure.Discovery, "discovery document missing token_endpoint");
        }

        if (string.IsNullOrEmpty(doc.AuthorizationEndpoint))
        {
            throw new OidcException(OidcFailure.Discovery, "discovery document missing authorization_endpoint");
        }

        OidcUrl.Validate("issuer", doc.Issuer!);
        OidcUrl.Validate("authorization_endpoint", doc.AuthorizationEndpoint);
        OidcUrl.Validate("token_endpoint", doc.TokenEndpoint);
        if (!string.IsNullOrEmpty(doc.DeviceAuthorizationEndpoint))
        {
            OidcUrl.Validate("device_authorization_endpoint", doc.DeviceAuthorizationEndpoint);
        }

        return new OidcProviderMetadata(
            doc.Issuer!,
            doc.AuthorizationEndpoint,
            doc.TokenEndpoint,
            string.IsNullOrEmpty(doc.DeviceAuthorizationEndpoint) ? null : doc.DeviceAuthorizationEndpoint,
            doc.CodeChallengeMethodsSupported ?? []);
    }
}

/// <summary>URL policy for issuers and provider endpoints.</summary>
internal static class OidcUrl
{
    /// <summary>Requires HTTPS (HTTP only for loopback), no userinfo, no fragment.</summary>
    public static void Validate(string name, string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new OidcException(OidcFailure.Discovery, $"invalid {name} URL");
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || raw.Contains('#', StringComparison.Ordinal))
        {
            throw new OidcException(OidcFailure.Discovery, $"{name} URL must not contain userinfo or a fragment");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri))
        {
            return;
        }

        throw new OidcException(OidcFailure.Discovery, $"{name} URL must use HTTPS (HTTP is allowed only for loopback hosts)");
    }

    private static bool IsLoopbackHost(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
