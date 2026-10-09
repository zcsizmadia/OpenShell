// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>A loopback HTTP service exposed from a sandbox through the gateway.</summary>
public sealed record ServiceEndpoint
{
    /// <summary>Service name; empty for the unnamed service.</summary>
    public required string Name { get; init; }

    /// <summary>Owning sandbox name.</summary>
    public required string Sandbox { get; init; }

    /// <summary>Owning sandbox id.</summary>
    public string SandboxId { get; init; } = "";

    /// <summary>Port inside the sandbox.</summary>
    public uint TargetPort { get; init; }

    /// <summary>True when the service is published on its own domain.</summary>
    public bool Domain { get; init; }

    /// <summary>How Authorization is treated toward the service.</summary>
    public ServiceAuthorizationMode AuthorizationMode { get; init; }

    /// <summary>Public URL of the service.</summary>
    public string Url { get; init; } = "";

    /// <summary>Optimistic-concurrency version.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Options for <see cref="IServiceClient.ExposeAsync"/>.</summary>
public sealed record ExposeServiceOptions
{
    /// <summary>Publish the service on its own domain.</summary>
    public bool Domain { get; init; }

    /// <summary>Authorization handling; unspecified resolves to <see cref="ServiceAuthorizationMode.Strip"/>.</summary>
    public ServiceAuthorizationMode AuthorizationMode { get; init; } = ServiceAuthorizationMode.Strip;

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }
}

/// <summary>Service exposure operations, scoped to one workspace.</summary>
public interface IServiceClient
{
    /// <summary>Exposes a loopback HTTP port of a sandbox. An empty <paramref name="name"/> selects the unnamed service.</summary>
    ValueTask<ServiceEndpoint> ExposeAsync(string sandbox, string name, uint targetPort, ExposeServiceOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Gets one exposed service.</summary>
    ValueTask<ServiceEndpoint> GetAsync(string sandbox, string name, CancellationToken cancellationToken = default);

    /// <summary>Lists the services of a sandbox lazily, page by page.</summary>
    Pager<ServiceEndpoint> List(string sandbox, ListOptions? options = null);

    /// <summary>Removes an exposed service.</summary>
    ValueTask<DeletionResult> DeleteAsync(string sandbox, string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class ServiceClient(ClientCore core, string workspace) : IServiceClient
{
    public async ValueTask<ServiceEndpoint> ExposeAsync(string sandbox, string name, uint targetPort, ExposeServiceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        options ??= new ExposeServiceOptions();
        var request = new Wire.ExposeServiceRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Name = name,
            TargetPort = targetPort,
            Domain = options.Domain,
            AuthorizationMode = (Wire.ServiceAuthorizationMode)(int)options.AuthorizationMode,
            RequestId = options.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.ExposeServiceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response);
    }

    public async ValueTask<ServiceEndpoint> GetAsync(string sandbox, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        var request = new Wire.GetServiceRequest { WorkspaceScope = ClientCore.Scope(workspace), Sandbox = sandbox, Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetServiceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response);
    }

    public Pager<ServiceEndpoint> List(string sandbox, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        options ??= new ListOptions();
        return new Pager<ServiceEndpoint>(
            async (token, ct) =>
            {
                var request = new Wire.ListServicesRequest
                {
                    WorkspaceScope = ClientCore.ListScope(workspace, options.AllWorkspaces),
                    Sandbox = sandbox,
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListServicesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<ServiceEndpoint>(
                    response.Services.Select(ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<DeletionResult> DeleteAsync(string sandbox, string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        var request = new Wire.DeleteServiceRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteServiceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DeletionResult((DeletionOutcome)(int)response.Outcome);
    }

    internal static ServiceEndpoint ToDomain(Wire.ServiceEndpointResponse response)
    {
        var e = response.Endpoint;
        return new ServiceEndpoint
        {
            Name = e?.Name ?? "",
            Sandbox = e?.Sandbox ?? "",
            SandboxId = e?.SandboxId ?? "",
            TargetPort = e?.TargetPort ?? 0,
            Domain = e?.Domain ?? false,
            AuthorizationMode = e is null ? ServiceAuthorizationMode.Unspecified : (ServiceAuthorizationMode)(int)e.AuthorizationMode,
            Url = response.Url,
            ResourceVersion = e?.Metadata?.ResourceVersion ?? 0,
            CreatedAt = e?.Metadata?.CreatedTime?.ToDateTimeOffset(),
        };
    }
}
