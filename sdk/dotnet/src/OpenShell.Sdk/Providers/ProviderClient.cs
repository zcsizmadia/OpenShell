// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Provider operations, scoped to one workspace.</summary>
public interface IProviderClient
{
    /// <summary>Provider type profiles, in this workspace.</summary>
    IProviderProfileClient Profiles { get; }

    /// <summary>Gateway-owned credential refresh configuration.</summary>
    IProviderRefreshClient Refresh { get; }

    /// <summary>Creates a provider.</summary>
    ValueTask<Provider> CreateAsync(Provider provider, CreateProviderOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a provider by name.</summary>
    ValueTask<Provider> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists providers lazily; set <see cref="ListOptions.AllWorkspaces"/> to span workspaces.</summary>
    Pager<Provider> List(ListOptions? options = null);

    /// <summary>Updates a provider and returns the per-sandbox receipts for sandboxes it is attached to.</summary>
    ValueTask<ProviderUpdateResult> UpdateAsync(Provider provider, UpdateProviderOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a provider.</summary>
    ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Creates the provider, or updates it in place when one with the same name already exists.</summary>
    ValueTask<Provider> EnsureAsync(Provider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports exactly the selected runtime credential keys, or fails without a result. Requires direct operator mTLS
    /// (a trusted <c>OU=operator</c> client certificate) and no bearer or edge token. The gateway refreshes eligible missing
    /// or short-lived outputs and may commit refresh effects even if delivery fails. This helper does not retry or cache,
    /// and never logs values; treat the result as secret.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, ProviderCredentialValue>> GetCredentialsAsync(
        string name,
        IReadOnlyCollection<string> credentialKeys,
        ProviderCredentialsOptions options,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ProviderClient(ClientCore core, string workspace) : IProviderClient
{
    public IProviderProfileClient Profiles { get; } = new ProviderProfileClient(core, workspace);

    public IProviderRefreshClient Refresh { get; } = new ProviderRefreshClient(core, workspace);

    public async ValueTask<Provider> CreateAsync(Provider provider, CreateProviderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var request = new Wire.CreateProviderRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = ProviderMapping.ToWire(provider),
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.CreateProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProviderMapping.ToDomain(response.Provider);
    }

    public async ValueTask<Provider> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.GetProviderRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProviderMapping.ToDomain(response.Provider);
    }

    public Pager<Provider> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<Provider>(
            async (token, ct) =>
            {
                var request = new Wire.ListProvidersRequest
                {
                    WorkspaceScope = ClientCore.ListScope(workspace, options.AllWorkspaces),
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListProvidersAsync(request, o), ct).ConfigureAwait(false);
                return new Page<Provider>(
                    response.Providers.Select(ProviderMapping.ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<ProviderUpdateResult> UpdateAsync(Provider provider, UpdateProviderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var request = ProviderMapping.ToUpdateRequest(provider, options, workspace);
        var response = await core.UnaryAsync(o => core.Grpc.UpdateProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new ProviderUpdateResult(
            ProviderMapping.ToDomain(response.Provider),
            response.MutationId,
            response.TargetReceipts.Select(r => ProviderReadinessLogic.ToDomain(r)!).ToArray());
    }

    public async ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.DeleteProviderRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProviderMapping.ToDeletion(response.Outcome);
    }

    public async ValueTask<Provider> EnsureAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider existing;
        try
        {
            existing = await GetAsync(provider.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenShellException e) when (e.IsNotFound)
        {
            return await CreateAsync(provider, null, cancellationToken).ConfigureAwait(false);
        }

        var updated = provider with { Id = existing.Id, ResourceVersion = existing.ResourceVersion };
        return (await UpdateAsync(updated, null, cancellationToken).ConfigureAwait(false)).Provider;
    }

    public async ValueTask<IReadOnlyDictionary<string, ProviderCredentialValue>> GetCredentialsAsync(
        string name,
        IReadOnlyCollection<string> credentialKeys,
        ProviderCredentialsOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialKeys);
        if (string.IsNullOrEmpty(options.Workspace) || options.Workspace.Trim() != options.Workspace)
        {
            throw Invalid("an explicit canonical workspace is required");
        }

        if (string.IsNullOrEmpty(name) || name.Trim() != name || Encoding.UTF8.GetByteCount(name) > 253)
        {
            throw Invalid("a canonical provider name is required");
        }

        // Copy before awaiting, so caller mutation cannot affect response validation.
        var keys = credentialKeys.ToArray();
        if (keys.Length is < 1 or > 32
            || keys.Any(k => k is null || k.Length > 256 || !KeyPattern().IsMatch(k))
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw Invalid("select between 1 and 32 unique runtime environment keys");
        }

        if (options.MinimumRemainingLifetime is { } lifetime && (lifetime < TimeSpan.Zero || lifetime > TimeSpan.FromHours(24)))
        {
            throw Invalid("MinimumRemainingLifetime must be between zero and 24 hours");
        }

        var request = new Wire.GetProviderCredentialsRequest
        {
            WorkspaceScope = ClientCore.Scope(options.Workspace),
            Name = name,
        };
        request.CredentialKeys.AddRange(keys);
        if (options.MinimumRemainingLifetime is { } margin)
        {
            request.MinimumRemainingLifetime = Duration.FromTimeSpan(margin);
        }

        var response = await core.UnaryAsync(o => core.Grpc.GetProviderCredentialsAsync(request, o), cancellationToken).ConfigureAwait(false);
        var returned = response.Credentials;
        if (returned.Count != keys.Length || keys.Any(k => !returned.ContainsKey(k)))
        {
            throw new OpenShellException(OpenShellErrorKind.Internal, "gateway returned an unexpected credential selection");
        }

        var result = new Dictionary<string, ProviderCredentialValue>(keys.Length, StringComparer.Ordinal);
        foreach (var (key, credential) in returned)
        {
            result[key] = new ProviderCredentialValue(credential.Value, ProviderMapping.ToTime(credential.ExpirationTime));
        }

        return result;
    }

    private static OpenShellException Invalid(string message) => new(OpenShellErrorKind.InvalidConfig, message);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
