// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Gateway health.</summary>
public enum ServiceStatus
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Fully operational.</summary>
    Healthy = 1,
    /// <summary>Operational with reduced capability.</summary>
    Degraded = 2,
    /// <summary>Not operational.</summary>
    Unhealthy = 3,
}

/// <summary>Result of a health check.</summary>
public sealed record HealthResult(ServiceStatus Status, string Version);

/// <summary>A compute backend available on the gateway.</summary>
public sealed record ComputeDriverInfo(string Name);

/// <summary>One successful gateway/extension negotiation.</summary>
public sealed record ExtensionInfo(
    string Kind,
    string ConfiguredName,
    string ImplementationName,
    string ImplementationVersion,
    uint ProtocolMajor,
    uint ProtocolMinor,
    IReadOnlyList<string> SupportedCapabilities,
    IReadOnlyList<string> RequiredCapabilities);

/// <summary>Operational metadata about the gateway.</summary>
public sealed record GatewayInfo(
    ServiceStatus Status,
    string GatewayVersion,
    IReadOnlyList<ComputeDriverInfo> ComputeDrivers,
    IReadOnlyList<ExtensionInfo> Extensions);

/// <summary>The authenticated caller.</summary>
public sealed record CurrentUser(
    string Subject,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    string IdentityProvider);

/// <summary>Health and identity operations.</summary>
public interface IHealthClient
{
    /// <summary>Checks that the gateway is reachable and reports its status.</summary>
    ValueTask<HealthResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets gateway version, compute drivers and negotiated extensions.</summary>
    ValueTask<GatewayInfo> GetGatewayInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the identity the gateway sees for the current credentials.</summary>
    ValueTask<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

internal sealed class HealthClient(ClientCore core) : IHealthClient
{
    public async ValueTask<HealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var r = await core.UnaryAsync(o => core.Grpc.HealthAsync(new Wire.HealthRequest(), o), cancellationToken).ConfigureAwait(false);
        return new HealthResult((ServiceStatus)(int)r.Status, r.Version);
    }

    public async ValueTask<GatewayInfo> GetGatewayInfoAsync(CancellationToken cancellationToken = default)
    {
        var r = await core.UnaryAsync(o => core.Grpc.GetGatewayInfoAsync(new Wire.GetGatewayInfoRequest(), o), cancellationToken).ConfigureAwait(false);
        return new GatewayInfo(
            (ServiceStatus)(int)r.Status,
            r.GatewayVersion,
            r.ComputeDrivers.Select(d => new ComputeDriverInfo(d.Name)).ToArray(),
            r.Extensions.Select(e => new ExtensionInfo(
                e.Kind.ToString(),
                e.ConfiguredName,
                e.ImplementationName,
                e.ImplementationVersion,
                e.ProtocolMajor,
                e.ProtocolMinor,
                e.SupportedCapabilities.ToArray(),
                e.RequiredCapabilities.ToArray())).ToArray());
    }

    public async ValueTask<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var r = await core.UnaryAsync(o => core.Grpc.GetCurrentUserAsync(new Wire.GetCurrentUserRequest(), o), cancellationToken).ConfigureAwait(false);
        return new CurrentUser(r.Subject, r.DisplayName, r.Roles.ToArray(), r.Scopes.ToArray(), r.IdentityProvider);
    }
}
