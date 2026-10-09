// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Provider type profile operations.</summary>
public interface IProviderProfileClient
{
    /// <summary>A view of this client that targets platform (global) profiles instead of one workspace.</summary>
    IProviderProfileClient Platform { get; }

    /// <summary>Lists profiles lazily.</summary>
    Pager<ProviderProfile> List(ListOptions? options = null);

    /// <summary>Gets a profile by id.</summary>
    ValueTask<ProviderProfile> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Imports custom profiles. Check <see cref="ImportProfilesResult.Imported"/> and diagnostics.</summary>
    ValueTask<ImportProfilesResult> ImportAsync(IReadOnlyList<ProfileImportItem> items, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Updates one custom profile with optimistic concurrency.</summary>
    ValueTask<UpdateProfileResult> UpdateAsync(string id, ulong expectedResourceVersion, ProfileImportItem item, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Validates profiles without storing them.</summary>
    ValueTask<LintProfilesResult> LintAsync(IReadOnlyList<ProfileImportItem> items, CancellationToken cancellationToken = default);

    /// <summary>Deletes a custom profile.</summary>
    ValueTask<DeletionResult> DeleteAsync(string id, DeleteOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class ProviderProfileClient(ClientCore core, string? workspace) : IProviderProfileClient
{
    public IProviderProfileClient Platform => workspace is null ? this : new ProviderProfileClient(core, null);

    private WorkspaceSelector? Scope => workspace is null ? null : ClientCore.Scope(workspace);

    public Pager<ProviderProfile> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<ProviderProfile>(
            async (token, ct) =>
            {
                var request = new Wire.ListProviderProfilesRequest { WorkspaceScope = Scope, PageSize = options.PageSize, PageToken = token ?? "" };
                var response = await core.UnaryAsync(o => core.Grpc.ListProviderProfilesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<ProviderProfile>(
                    response.Profiles.Select(ProfileMapping.ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<ProviderProfile> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var request = new Wire.GetProviderProfileRequest { WorkspaceScope = Scope, Id = id };
        var response = await core.UnaryAsync(o => core.Grpc.GetProviderProfileAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProfileMapping.ToDomain(response.Profile);
    }

    public async ValueTask<ImportProfilesResult> ImportAsync(IReadOnlyList<ProfileImportItem> items, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var request = new Wire.ImportProviderProfilesRequest { WorkspaceScope = Scope, RequestId = options?.RequestId ?? "" };
        request.Profiles.AddRange(items.Select(ProfileMapping.ToWire));
        var response = await core.UnaryAsync(o => core.Grpc.ImportProviderProfilesAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new ImportProfilesResult(
            response.Diagnostics.Select(ProfileMapping.ToDomain).ToArray(),
            response.Profiles.Select(ProfileMapping.ToDomain).ToArray(),
            response.Imported);
    }

    public async ValueTask<UpdateProfileResult> UpdateAsync(string id, ulong expectedResourceVersion, ProfileImportItem item, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(item);
        var request = new Wire.UpdateProviderProfilesRequest
        {
            WorkspaceScope = Scope,
            Id = id,
            ExpectedResourceVersion = expectedResourceVersion,
            Profile = ProfileMapping.ToWire(item),
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.UpdateProviderProfilesAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new UpdateProfileResult(
            response.Diagnostics.Select(ProfileMapping.ToDomain).ToArray(),
            response.Profile is null ? null : ProfileMapping.ToDomain(response.Profile),
            response.Updated);
    }

    public async ValueTask<LintProfilesResult> LintAsync(IReadOnlyList<ProfileImportItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var request = new Wire.LintProviderProfilesRequest { WorkspaceScope = Scope };
        request.Profiles.AddRange(items.Select(ProfileMapping.ToWire));
        var response = await core.UnaryAsync(o => core.Grpc.LintProviderProfilesAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new LintProfilesResult(response.Diagnostics.Select(ProfileMapping.ToDomain).ToArray(), response.Valid);
    }

    public async ValueTask<DeletionResult> DeleteAsync(string id, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var request = new Wire.DeleteProviderProfileRequest
        {
            WorkspaceScope = Scope,
            Id = id,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteProviderProfileAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProviderMapping.ToDeletion(response.Outcome);
    }
}

internal static class ProfileMapping
{
    public static ProviderProfile ToDomain(Wire.ProviderProfile p) => new()
    {
        Id = p.Id,
        DisplayName = p.DisplayName,
        Description = p.Description,
        Category = (ProviderProfileCategory)(int)p.Category,
        Credentials = p.Credentials.Select(ToDomain).ToArray(),
        Files = p.Files.Select(f => new ProfileFile(f.Path, f.Content, f.EnvVar)).ToArray(),
        Endpoints = p.Endpoints.ToArray(),
        Binaries = p.Binaries.ToArray(),
        InferenceCapable = p.InferenceCapable,
        DiscoveryCredentials = p.Discovery?.Credentials.ToArray() ?? [],
        ResourceVersion = p.ResourceVersion,
        Annotations = SandboxMapping.Copy(p.Annotations),
        Source = p.Source,
        Scope = p.Scope,
    };

    public static Wire.ProviderProfile ToWire(ProviderProfile p)
    {
        var wire = new Wire.ProviderProfile
        {
            Id = p.Id,
            DisplayName = p.DisplayName,
            Description = p.Description,
            Category = (Wire.ProviderProfileCategory)(int)p.Category,
            InferenceCapable = p.InferenceCapable,
            ResourceVersion = p.ResourceVersion,
            Source = p.Source,
            Scope = p.Scope,
        };
        wire.Credentials.AddRange(p.Credentials.Select(ToWire));
        wire.Files.AddRange(p.Files.Select(f => new Wire.ProviderProfileFile { Path = f.Path, Content = f.Content, EnvVar = f.EnvVar }));
        wire.Endpoints.AddRange(p.Endpoints);
        wire.Binaries.AddRange(p.Binaries);
        if (p.DiscoveryCredentials.Count > 0)
        {
            wire.Discovery = new Wire.ProviderProfileDiscovery();
            wire.Discovery.Credentials.AddRange(p.DiscoveryCredentials);
        }

        SandboxMapping.AddAll(wire.Annotations, p.Annotations);
        return wire;
    }

    public static Wire.ProviderProfileImportItem ToWire(ProfileImportItem item) =>
        new() { Profile = ToWire(item.Profile), Source = item.Source };

    public static ProfileDiagnostic ToDomain(Wire.ProviderProfileDiagnostic d) =>
        new(d.Source, d.ProfileId, d.Field, d.Message, d.Severity);

    private static ProfileCredential ToDomain(Wire.ProviderProfileCredential c) => new()
    {
        Name = c.Name,
        Description = c.Description,
        EnvVars = c.EnvVars.ToArray(),
        Required = c.Required,
        AuthStyle = c.AuthStyle,
        HeaderName = c.HeaderName,
        QueryParam = c.QueryParam,
        PathTemplate = c.PathTemplate,
        Refresh = c.Refresh is null ? null : ToDomain(c.Refresh),
        TokenGrant = c.TokenGrant is null ? null : ToDomain(c.TokenGrant, c.TokenGrantOwners),
    };

    private static Wire.ProviderProfileCredential ToWire(ProfileCredential c)
    {
        var wire = new Wire.ProviderProfileCredential
        {
            Name = c.Name,
            Description = c.Description,
            Required = c.Required,
            AuthStyle = c.AuthStyle,
            HeaderName = c.HeaderName,
            QueryParam = c.QueryParam,
            PathTemplate = c.PathTemplate,
        };
        wire.EnvVars.AddRange(c.EnvVars);
        if (c.Refresh is { } r)
        {
            wire.Refresh = ToWire(r);
        }

        // token_grant_owners is output-only and ignored on input, so it is not sent.
        if (c.TokenGrant is { } g)
        {
            wire.TokenGrant = ToWire(g);
        }

        return wire;
    }

    private static ProfileCredentialRefresh ToDomain(Wire.ProviderCredentialRefresh r) => new()
    {
        Strategy = (RefreshStrategy)(int)r.Strategy,
        TokenUrl = r.TokenUrl,
        Scopes = r.Scopes.ToArray(),
        RefreshBefore = r.RefreshBefore?.ToTimeSpan(),
        MaxLifetime = r.MaxLifetime?.ToTimeSpan(),
        Material = r.Material.Select(m => new ProfileCredentialRefreshMaterial(m.Name, m.Description, m.Required, m.Secret)).ToArray(),
        AdditionalOutputs = r.AdditionalOutputs.Select(o => new ProfileCredentialRefreshOutput(o.Output, o.Credential)).ToArray(),
    };

    private static Wire.ProviderCredentialRefresh ToWire(ProfileCredentialRefresh r)
    {
        var wire = new Wire.ProviderCredentialRefresh { Strategy = (Wire.ProviderCredentialRefreshStrategy)(int)r.Strategy, TokenUrl = r.TokenUrl };
        wire.Scopes.AddRange(r.Scopes);
        if (r.RefreshBefore is { } before)
        {
            wire.RefreshBefore = Duration.FromTimeSpan(before);
        }

        if (r.MaxLifetime is { } max)
        {
            wire.MaxLifetime = Duration.FromTimeSpan(max);
        }

        wire.Material.AddRange(r.Material.Select(m => new Wire.ProviderCredentialRefreshMaterial
        {
            Name = m.Name,
            Description = m.Description,
            Required = m.Required,
            Secret = m.Secret,
        }));
        wire.AdditionalOutputs.AddRange(r.AdditionalOutputs.Select(o => new Wire.ProviderCredentialRefreshOutput { Output = o.Output, Credential = o.Credential }));
        return wire;
    }

    private static CredentialTokenGrant ToDomain(Wire.ProviderCredentialTokenGrant g, IEnumerable<string> owners) => new()
    {
        TokenEndpoint = g.TokenEndpoint,
        Audience = g.Audience,
        JwtSvidAudience = g.JwtSvidAudience,
        Scopes = g.Scopes.ToArray(),
        CacheTtl = g.CacheTtl?.ToTimeSpan(),
        AudienceOverrides = g.AudienceOverrides.Select(o => new TokenGrantAudienceOverride(o.Host, o.Port, o.Path, o.Audience, o.Scopes.ToArray())).ToArray(),
        ClientAssertionType = g.ClientAssertionType,
        GrantType = (CredentialTokenGrantType)(int)g.GrantType,
        SubjectToken = g.SubjectToken is null ? null : new TokenGrantSubjectToken(g.SubjectToken.Source, g.SubjectToken.Credential, g.SubjectToken.SubjectTokenType),
        RequestedTokenType = g.RequestedTokenType,
        Owners = owners.ToArray(),
    };

    private static Wire.ProviderCredentialTokenGrant ToWire(CredentialTokenGrant g)
    {
        var wire = new Wire.ProviderCredentialTokenGrant
        {
            TokenEndpoint = g.TokenEndpoint,
            Audience = g.Audience,
            JwtSvidAudience = g.JwtSvidAudience,
            ClientAssertionType = g.ClientAssertionType,
            GrantType = (Wire.ProviderCredentialTokenGrantType)(int)g.GrantType,
            RequestedTokenType = g.RequestedTokenType,
        };
        wire.Scopes.AddRange(g.Scopes);
        if (g.CacheTtl is { } ttl)
        {
            wire.CacheTtl = Duration.FromTimeSpan(ttl);
        }

        wire.AudienceOverrides.AddRange(g.AudienceOverrides.Select(o =>
        {
            var w = new Wire.ProviderCredentialTokenGrantAudienceOverride { Host = o.Host, Port = o.Port, Path = o.Path, Audience = o.Audience };
            w.Scopes.AddRange(o.Scopes);
            return w;
        }));
        if (g.SubjectToken is { } s)
        {
            wire.SubjectToken = new Wire.ProviderCredentialTokenGrantSubjectToken { Source = s.Source, Credential = s.Credential, SubjectTokenType = s.SubjectTokenType };
        }

        return wire;
    }
}
