// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>Stable provider profile categories used for grouping and filtering.</summary>
public enum ProviderProfileCategory
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Other.</summary>
    Other = 1,
    /// <summary>Inference.</summary>
    Inference = 2,
    /// <summary>Agent.</summary>
    Agent = 3,
    /// <summary>Source control.</summary>
    SourceControl = 4,
    /// <summary>Messaging.</summary>
    Messaging = 5,
    /// <summary>Data.</summary>
    Data = 6,
    /// <summary>Knowledge.</summary>
    Knowledge = 7,
}

/// <summary>How a credential is refreshed.</summary>
public enum RefreshStrategy
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Static; never refreshed.</summary>
    Static = 1,
    /// <summary>Refreshed outside the gateway.</summary>
    External = 2,
    /// <summary>OAuth2 refresh token grant.</summary>
    OAuth2RefreshToken = 3,
    /// <summary>OAuth2 client credentials grant.</summary>
    OAuth2ClientCredentials = 4,
    /// <summary>Google service account JWT.</summary>
    GoogleServiceAccountJwt = 5,
    /// <summary>AWS STS assume role.</summary>
    AwsStsAssumeRole = 6,
}

/// <summary>The OAuth2 grant used for a dynamically acquired credential.</summary>
public enum CredentialTokenGrantType
{
    /// <summary>Treated as client credentials for backwards compatibility.</summary>
    Unspecified = 0,
    /// <summary>Client credentials.</summary>
    ClientCredentials = 1,
    /// <summary>Token exchange.</summary>
    TokenExchange = 2,
}

/// <summary>One input a refresh strategy needs.</summary>
public sealed record ProfileCredentialRefreshMaterial(string Name, string Description = "", bool Required = false, bool Secret = false);

/// <summary>Maps a minted output to a sibling credential.</summary>
public sealed record ProfileCredentialRefreshOutput(string Output, string Credential);

/// <summary>How a profile credential is refreshed.</summary>
public sealed record ProfileCredentialRefresh
{
    /// <summary>Strategy.</summary>
    public RefreshStrategy Strategy { get; init; }

    /// <summary>Token endpoint URL.</summary>
    public string TokenUrl { get; init; } = "";

    /// <summary>OAuth2 scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Refresh this long before expiry; null when absent (truncated to 100 ns).</summary>
    public TimeSpan? RefreshBefore { get; init; }

    /// <summary>Maximum credential lifetime; null when absent (truncated to 100 ns).</summary>
    public TimeSpan? MaxLifetime { get; init; }

    /// <summary>Inputs required by the strategy.</summary>
    public IReadOnlyList<ProfileCredentialRefreshMaterial> Material { get; init; } = [];

    /// <summary>Additional outputs of one refresh.</summary>
    public IReadOnlyList<ProfileCredentialRefreshOutput> AdditionalOutputs { get; init; } = [];
}

/// <summary>Endpoint-specific audience override for a token grant.</summary>
public sealed record TokenGrantAudienceOverride(string Host, uint Port, string Path, string Audience, IReadOnlyList<string> Scopes);

/// <summary>Subject token for a token-exchange grant.</summary>
public sealed record TokenGrantSubjectToken(string Source, string Credential, string SubjectTokenType = "");

/// <summary>Dynamic credential acquisition through an OAuth2 grant.</summary>
public sealed record CredentialTokenGrant
{
    /// <summary>OAuth2 token endpoint URL.</summary>
    public string TokenEndpoint { get; init; } = "";

    /// <summary>Default resource audience.</summary>
    public string Audience { get; init; } = "";

    /// <summary>Audience for the SPIRE JWT-SVID.</summary>
    public string JwtSvidAudience { get; init; } = "";

    /// <summary>Scopes to request.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Token cache TTL override; null means use <c>expires_in</c>.</summary>
    public TimeSpan? CacheTtl { get; init; }

    /// <summary>Endpoint-specific audience overrides.</summary>
    public IReadOnlyList<TokenGrantAudienceOverride> AudienceOverrides { get; init; } = [];

    /// <summary>OAuth2 client assertion type.</summary>
    public string ClientAssertionType { get; init; } = "";

    /// <summary>Grant type.</summary>
    public CredentialTokenGrantType GrantType { get; init; }

    /// <summary>Subject token for token exchange.</summary>
    public TokenGrantSubjectToken? SubjectToken { get; init; }

    /// <summary>OAuth2 requested token type.</summary>
    public string RequestedTokenType { get; init; } = "";

    /// <summary>Output-only: endpoint authorities allowed to obtain this grant.</summary>
    public IReadOnlyList<string> Owners { get; init; } = [];
}

/// <summary>One credential a provider profile declares.</summary>
public sealed record ProfileCredential
{
    /// <summary>Credential name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Environment variables that receive the value.</summary>
    public IReadOnlyList<string> EnvVars { get; init; } = [];

    /// <summary>Whether the credential is required.</summary>
    public bool Required { get; init; }

    /// <summary>Authentication style.</summary>
    public string AuthStyle { get; init; } = "";

    /// <summary>Header name for header auth.</summary>
    public string HeaderName { get; init; } = "";

    /// <summary>Query parameter name for query auth.</summary>
    public string QueryParam { get; init; } = "";

    /// <summary>Path template for path auth.</summary>
    public string PathTemplate { get; init; } = "";

    /// <summary>Refresh declaration.</summary>
    public ProfileCredentialRefresh? Refresh { get; init; }

    /// <summary>Token grant declaration.</summary>
    public CredentialTokenGrant? TokenGrant { get; init; }
}

/// <summary>EXPERIMENTAL: non-secret content served at a virtual sandbox path. May change or be removed.</summary>
public sealed record ProfileFile(string Path, string Content, string EnvVar = "");

/// <summary>A provider type profile.</summary>
public sealed record ProviderProfile
{
    /// <summary>Profile id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Category.</summary>
    public ProviderProfileCategory Category { get; init; }

    /// <summary>Credentials.</summary>
    public IReadOnlyList<ProfileCredential> Credentials { get; init; } = [];

    /// <summary>EXPERIMENTAL: files rendered from provider configuration.</summary>
    public IReadOnlyList<ProfileFile> Files { get; init; } = [];

    /// <summary>Network endpoints; these are the generated wire type so no policy fields are lost.</summary>
    public IReadOnlyList<NetworkEndpoint> Endpoints { get; init; } = [];

    /// <summary>Binaries allowed to use the credentials; generated wire type.</summary>
    public IReadOnlyList<NetworkBinary> Binaries { get; init; } = [];

    /// <summary>Whether the profile can serve inference.</summary>
    public bool InferenceCapable { get; init; }

    /// <summary>Credential names eligible for local discovery.</summary>
    public IReadOnlyList<string> DiscoveryCredentials { get; init; } = [];

    /// <summary>Storage resource version for custom profiles; update calls use it for optimistic concurrency.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Non-secret annotations.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = Sandbox.EmptyMap;

    /// <summary>Server-set provenance: <c>builtin</c>, <c>user</c>, or <c>interceptor/{name}</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>Server-set visibility: <c>platform</c>, <c>workspace</c>, or empty.</summary>
    public string Scope { get; init; } = "";
}

/// <summary>A profile submitted for import, update or lint, with optional source metadata for diagnostics.</summary>
public sealed record ProfileImportItem(ProviderProfile Profile, string Source = "");

/// <summary>A validation finding from import, update or lint.</summary>
public sealed record ProfileDiagnostic(string Source, string ProfileId, string Field, string Message, string Severity);

/// <summary>Result of importing profiles.</summary>
public sealed record ImportProfilesResult(IReadOnlyList<ProfileDiagnostic> Diagnostics, IReadOnlyList<ProviderProfile> Profiles, bool Imported);

/// <summary>Result of updating a profile.</summary>
public sealed record UpdateProfileResult(IReadOnlyList<ProfileDiagnostic> Diagnostics, ProviderProfile? Profile, bool Updated);

/// <summary>Result of linting profiles.</summary>
public sealed record LintProfilesResult(IReadOnlyList<ProfileDiagnostic> Diagnostics, bool Valid);

/// <summary>Options for profile mutations.</summary>
public sealed record ProfileMutationOptions
{
    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}
