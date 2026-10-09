// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace OpenShell;

/// <summary>On-disk <c>metadata.json</c> of a registered gateway.</summary>
internal sealed class GatewayMetadataDto
{
    [JsonPropertyName("gateway_endpoint")]
    public string? GatewayEndpoint { get; set; }

    [JsonPropertyName("auth_mode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("oidc_issuer")]
    public string? OidcIssuer { get; set; }

    [JsonPropertyName("oidc_client_id")]
    public string? OidcClientId { get; set; }

    [JsonPropertyName("oidc_audience")]
    public string? OidcAudience { get; set; }

    [JsonPropertyName("oidc_scopes")]
    public string? OidcScopes { get; set; }
}

/// <summary>On-disk <c>oidc_token.json</c> bundle shared with the CLI.</summary>
internal sealed class OidcTokenBundleDto
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }
}

/// <summary>The subset of the OpenID Connect discovery document the SDK uses.</summary>
internal sealed class OidcDiscoveryDto
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; set; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; set; }

    [JsonPropertyName("device_authorization_endpoint")]
    public string? DeviceAuthorizationEndpoint { get; set; }

    [JsonPropertyName("scopes_supported")]
    public string[]? ScopesSupported { get; set; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public string[]? CodeChallengeMethodsSupported { get; set; }
}

/// <summary>RFC 8628 device authorization response.</summary>
internal sealed class DeviceAuthorizationDto
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public long Interval { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GatewayMetadataDto))]
[JsonSerializable(typeof(OidcTokenBundleDto))]
[JsonSerializable(typeof(OidcDiscoveryDto))]
[JsonSerializable(typeof(DeviceAuthorizationDto))]
internal sealed partial class SdkJsonContext : JsonSerializerContext;
