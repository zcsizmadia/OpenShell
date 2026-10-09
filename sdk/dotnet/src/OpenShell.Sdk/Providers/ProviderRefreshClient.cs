// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>What an operator should do after a refresh failure.</summary>
public enum RefreshRecoveryAction
{
    /// <summary>None reported.</summary>
    Unspecified = 0,
    /// <summary>Retry.</summary>
    Retry = 1,
    /// <summary>Reauthorize the credential.</summary>
    Reauthorize = 2,
    /// <summary>Fix the refresh configuration.</summary>
    FixConfiguration = 3,
    /// <summary>Investigate.</summary>
    Investigate = 4,
}

/// <summary>Gateway-owned refresh state of one provider credential.</summary>
public sealed record RefreshStatus
{
    /// <summary>Provider name.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Provider id.</summary>
    public string ProviderId { get; init; } = "";

    /// <summary>Credential key.</summary>
    public string CredentialKey { get; init; } = "";

    /// <summary>Strategy.</summary>
    public RefreshStrategy Strategy { get; init; }

    /// <summary>Status text.</summary>
    public string Status { get; init; } = "";

    /// <summary>Credential expiration.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Next automatic refresh; null means none is scheduled. Use <see cref="RecoveryAction"/> to tell a parked refresh from an unset time.</summary>
    public DateTimeOffset? NextRefreshAt { get; init; }

    /// <summary>Last successful refresh.</summary>
    public DateTimeOffset? LastRefreshAt { get; init; }

    /// <summary>Last error text.</summary>
    public string LastError { get; init; } = "";

    /// <summary>Required recovery workflow.</summary>
    public RefreshRecoveryAction RecoveryAction { get; init; }

    /// <summary>Stable gateway-owned failure identifier, for example <c>oauth_invalid_grant</c>.</summary>
    public string FailureCode { get; init; } = "";

    /// <summary>Bounded provider subtype refining <see cref="FailureCode"/>.</summary>
    public string ProviderErrorSubtype { get; init; } = "";

    /// <summary>Time of the last error.</summary>
    public DateTimeOffset? LastErrorAt { get; init; }
}

/// <summary>Configures gateway-owned refresh for a credential.</summary>
public sealed record RefreshConfig
{
    /// <summary>Provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Credential key.</summary>
    public required string CredentialKey { get; init; }

    /// <summary>Strategy.</summary>
    public RefreshStrategy Strategy { get; init; }

    /// <summary>Strategy inputs. Treated as secret: never printed by <see cref="ToString"/>.</summary>
    public IReadOnlyDictionary<string, string> Material { get; init; } = Sandbox.EmptyMap;

    /// <summary>Additional material names to store as secrets; each must be present in <see cref="Material"/>.</summary>
    public IReadOnlyList<string>? SecretMaterialKeys { get; init; }

    /// <summary>Current credential expiration, when known.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Provider = ").Append(Provider)
            .Append(", CredentialKey = ").Append(CredentialKey)
            .Append(", Strategy = ").Append(Strategy)
            .Append(", MaterialKeys = [").AppendJoin(", ", Material.Keys).Append(']');
        return true;
    }
}

/// <summary>Gateway-owned credential refresh operations.</summary>
public interface IProviderRefreshClient
{
    /// <summary>Gets refresh status for one credential key, or for every credential when <paramref name="credentialKey"/> is null.</summary>
    ValueTask<IReadOnlyList<RefreshStatus>> GetStatusAsync(string provider, string? credentialKey = null, CancellationToken cancellationToken = default);

    /// <summary>Configures refresh for a credential.</summary>
    ValueTask<RefreshStatus> ConfigureAsync(RefreshConfig config, CancellationToken cancellationToken = default);

    /// <summary>Forces a credential rotation.</summary>
    ValueTask<RefreshStatus> RotateAsync(string provider, string credentialKey, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes refresh configuration.</summary>
    ValueTask<DeletionResult> DeleteAsync(string provider, string credentialKey, DeleteOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class ProviderRefreshClient(ClientCore core, string workspace) : IProviderRefreshClient
{
    public async ValueTask<IReadOnlyList<RefreshStatus>> GetStatusAsync(string provider, string? credentialKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var request = new Wire.GetProviderRefreshStatusRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = provider,
            CredentialKey = credentialKey ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.GetProviderRefreshStatusAsync(request, o), cancellationToken).ConfigureAwait(false);
        return response.Credentials.Select(ToDomain).ToArray();
    }

    public async ValueTask<RefreshStatus> ConfigureAsync(RefreshConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var request = new Wire.ConfigureProviderRefreshRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = config.Provider,
            CredentialKey = config.CredentialKey,
            Strategy = (Wire.ProviderCredentialRefreshStrategy)(int)config.Strategy,
            RequestId = config.RequestId ?? "",
        };
        SandboxMapping.AddAll(request.Material, config.Material);
        if (config.SecretMaterialKeys is { } secrets)
        {
            request.SecretMaterialKeys.AddRange(secrets);
        }

        if (config.ExpiresAt is { } expires)
        {
            request.ExpirationTime = ProviderMapping.ToWire(expires);
        }

        var response = await core.UnaryAsync(o => core.Grpc.ConfigureProviderRefreshAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Status);
    }

    public async ValueTask<RefreshStatus> RotateAsync(string provider, string credentialKey, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);
        var request = new Wire.RotateProviderCredentialRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = provider,
            CredentialKey = credentialKey,
            RequestId = requestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.RotateProviderCredentialAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Status);
    }

    public async ValueTask<DeletionResult> DeleteAsync(string provider, string credentialKey, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);
        var request = new Wire.DeleteProviderRefreshRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = provider,
            CredentialKey = credentialKey,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteProviderRefreshAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ProviderMapping.ToDeletion(response.Outcome);
    }

    private static RefreshStatus ToDomain(Wire.ProviderCredentialRefreshStatus s) => new()
    {
        Provider = s.Provider,
        ProviderId = s.ProviderId,
        CredentialKey = s.CredentialKey,
        Strategy = (RefreshStrategy)(int)s.Strategy,
        Status = s.Status,
        ExpiresAt = ProviderMapping.ToTime(s.ExpirationTime),
        NextRefreshAt = ProviderMapping.ToTime(s.NextRefreshTime),
        LastRefreshAt = ProviderMapping.ToTime(s.LastRefreshTime),
        LastError = s.LastError,
        RecoveryAction = (RefreshRecoveryAction)(int)s.RecoveryAction,
        FailureCode = s.FailureCode,
        ProviderErrorSubtype = s.ProviderErrorSubtype,
        LastErrorAt = ProviderMapping.ToTime(s.LastErrorTime),
    };
}
