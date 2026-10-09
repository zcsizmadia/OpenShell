// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace OpenShell;

/// <summary>An opaque handle for a credential stored by gateway credential storage. Never authored by callers.</summary>
public sealed record CredentialHandle(string Driver, string Handle, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A provider: a named set of credentials and configuration for one provider type.</summary>
public sealed record Provider
{
    /// <summary>Canonical name within the workspace.</summary>
    public required string Name { get; init; }

    /// <summary>Provider profile id this provider is created from (for example <c>github</c>).</summary>
    public required string Type { get; init; }

    /// <summary>Immutable identifier; empty before creation.</summary>
    public string Id { get; init; } = "";

    /// <summary>Owning workspace; filled by the gateway.</summary>
    public string Workspace { get; init; } = "";

    /// <summary>Secret values used for authentication. Gateways normally return these redacted.</summary>
    public IReadOnlyDictionary<string, string> Credentials { get; init; } = Sandbox.EmptyMap;

    /// <summary>Non-secret provider configuration.</summary>
    public IReadOnlyDictionary<string, string> Config { get; init; } = Sandbox.EmptyMap;

    /// <summary>User labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = Sandbox.EmptyMap;

    /// <summary>User annotations.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = Sandbox.EmptyMap;

    /// <summary>Expiration times by credential key; a missing key means the credential does not expire.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> CredentialExpirationTimes { get; init; } = EmptyTimes;

    /// <summary>Workspace storing this provider's type profile; empty means platform scope.</summary>
    public string ProfileWorkspace { get; init; } = "";

    /// <summary>Handles for secrets held by gateway credential storage (read-only).</summary>
    public IReadOnlyDictionary<string, CredentialHandle> CredentialHandles { get; init; } = EmptyHandles;

    /// <summary>Optimistic-concurrency version.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Deletion time, when deletion is in progress.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    internal static IReadOnlyDictionary<string, DateTimeOffset> EmptyTimes { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, DateTimeOffset>(new Dictionary<string, DateTimeOffset>());

    internal static IReadOnlyDictionary<string, CredentialHandle> EmptyHandles { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, CredentialHandle>(new Dictionary<string, CredentialHandle>());

    // The generated ToString must never print credential values.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name)
            .Append(", Type = ").Append(Type)
            .Append(", Workspace = ").Append(Workspace)
            .Append(", ResourceVersion = ").Append(ResourceVersion)
            .Append(", CredentialKeys = [").AppendJoin(", ", Credentials.Keys).Append(']');
        return true;
    }
}

/// <summary>Options for <see cref="IProviderClient.CreateAsync"/>.</summary>
public sealed record CreateProviderOptions
{
    /// <summary>Idempotency key; a retry with the same key returns the original result.</summary>
    public string? RequestId { get; init; }
}

/// <summary>Options for <see cref="IProviderClient.UpdateAsync"/>.</summary>
public sealed record UpdateProviderOptions
{
    /// <summary>Expiry timestamps to merge; omitted keys are unchanged. Defaults to <see cref="Provider.CredentialExpirationTimes"/>.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset>? CredentialExpirationTimes { get; init; }

    /// <summary>Credential keys whose existing expiry should be removed.</summary>
    public IReadOnlyList<string>? ClearCredentialExpirationKeys { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}

/// <summary>The result of a provider update.</summary>
/// <param name="Provider">The updated provider.</param>
/// <param name="MutationId">Identifies the update even when its target set is empty.</param>
/// <param name="TargetReceipts">One receipt per sandbox attached when the update was selected; pass to <see cref="ISandboxProviderClient.WaitAsync(ProviderMutationReceipt, TimeSpan, CancellationToken)"/>.</param>
public sealed record ProviderUpdateResult(Provider Provider, string MutationId, IReadOnlyList<ProviderMutationReceipt> TargetReceipts);

/// <summary>Options for <see cref="IProviderClient.GetCredentialsAsync"/>.</summary>
public sealed record ProviderCredentialsOptions
{
    /// <summary>Required explicit workspace. There is no implicit default and no all-workspaces export.</summary>
    public required string Workspace { get; init; }

    /// <summary>Omitted or zero uses the gateway's five-minute margin. Maximum: 24 hours.</summary>
    public TimeSpan? MinimumRemainingLifetime { get; init; }
}

/// <summary>An exported runtime secret. Its <see cref="ToString"/> never reveals the value.</summary>
public sealed record ProviderCredentialValue(string Value, DateTimeOffset? ExpirationTime = null)
{
    // Redacts the secret from the generated ToString; explicit property access still exposes it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Value = <redacted>, ExpirationTime = ").Append(ExpirationTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        return true;
    }
}
