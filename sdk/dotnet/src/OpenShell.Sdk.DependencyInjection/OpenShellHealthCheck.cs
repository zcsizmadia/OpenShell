// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenShell;
using OpenShell.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds an OpenShell gateway check to ASP.NET health checks.</summary>
public static class OpenShellHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers a check that calls the gateway health RPC. <paramref name="gatewayName"/> selects a named
    /// registration; omit it for the default gateway.
    /// </summary>
    public static IHealthChecksBuilder AddOpenShellHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "openshell",
        string? gatewayName = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new OpenShellHealthCheck(sp.GetRequiredService<IOpenShellClientFactory>().Create(gatewayName)),
            failureStatus,
            tags));
    }
}

internal sealed class OpenShellHealthCheck(IOpenShellClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await client.Health.CheckAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object> { ["version"] = health.Version, ["status"] = health.Status.ToString() };
            return health.Status switch
            {
                ServiceStatus.Healthy => HealthCheckResult.Healthy("Gateway is healthy.", data),
                ServiceStatus.Degraded => HealthCheckResult.Degraded("Gateway is degraded.", data: data),
                _ => new HealthCheckResult(context.Registration.FailureStatus, $"Gateway reported {health.Status}.", data: data),
            };
        }
        catch (OpenShellException e)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Gateway health call failed.", e);
        }
    }
}
