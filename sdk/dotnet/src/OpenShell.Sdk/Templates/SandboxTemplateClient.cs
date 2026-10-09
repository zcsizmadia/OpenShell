// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>GPU request for a workload. Presence of the record means a GPU is requested.</summary>
/// <param name="Count">Number of GPUs; null uses the driver's default assignment.</param>
public sealed record GpuRequirements(uint? Count = null);

/// <summary>Portable resource requirements for sandboxes created from a template.</summary>
public sealed record WorkloadResources
{
    /// <summary>CPU quantity, for example <c>500m</c> or <c>2</c>.</summary>
    public string? Cpu { get; init; }

    /// <summary>Memory quantity, for example <c>512Mi</c> or <c>2Gi</c>.</summary>
    public string? Memory { get; init; }

    /// <summary>GPU request, when any.</summary>
    public GpuRequirements? Gpu { get; init; }
}

/// <summary>Desired startup behaviour for sandboxes created from a template.</summary>
/// <param name="ReadyWithin">Target time to become ready.</param>
/// <param name="MaxBurst">Maximum number of sandboxes started at once.</param>
public sealed record StartupServiceLevel(TimeSpan? ReadyWithin = null, uint MaxBurst = 0);

/// <summary>A reusable, named workload shape that sandboxes can be created from.</summary>
public sealed record SandboxWorkloadTemplate
{
    /// <summary>Canonical name within the workspace.</summary>
    public required string Name { get; init; }

    /// <summary>Immutable identifier; empty before creation.</summary>
    public string? Id { get; init; }

    /// <summary>Owning workspace; empty before creation.</summary>
    public string? Workspace { get; init; }

    /// <summary>User labels.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>User annotations.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Optimistic-concurrency version.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Fully-qualified OCI image reference.</summary>
    public string? Image { get; init; }

    /// <summary>Environment injected into the sandbox runtime.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Portable resource requirements.</summary>
    public WorkloadResources? Resources { get; init; }

    /// <summary>Driver-keyed opaque configuration as a JSON object.</summary>
    public string? DriverConfigJson { get; init; }

    /// <summary>Desired startup service level.</summary>
    public StartupServiceLevel? Startup { get; init; }
}

/// <summary>Reusable sandbox workload templates, scoped to one workspace.</summary>
public interface ISandboxTemplateClient
{
    /// <summary>Creates a template. <see cref="SandboxWorkloadTemplate.Name"/> is required.</summary>
    ValueTask<SandboxWorkloadTemplate> CreateAsync(SandboxWorkloadTemplate workloadTemplate, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a template by name.</summary>
    ValueTask<SandboxWorkloadTemplate> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists templates lazily; set <see cref="ListOptions.AllWorkspaces"/> to span workspaces.</summary>
    Pager<SandboxWorkloadTemplate> List(ListOptions? options = null);

    /// <summary>Deletes a template. Inspect <see cref="DeletionResult.Outcome"/>.</summary>
    ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class SandboxTemplateClient(ClientCore core, string workspace) : ISandboxTemplateClient
{
    public async ValueTask<SandboxWorkloadTemplate> CreateAsync(SandboxWorkloadTemplate workloadTemplate, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workloadTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadTemplate.Name);
        var request = new Wire.CreateSandboxTemplateRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Template = ToWire(workloadTemplate),
            RequestId = requestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.CreateSandboxTemplateAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Template);
    }

    public async ValueTask<SandboxWorkloadTemplate> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.GetSandboxTemplateRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetSandboxTemplateAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Template);
    }

    public Pager<SandboxWorkloadTemplate> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<SandboxWorkloadTemplate>(
            async (token, ct) =>
            {
                var request = new Wire.ListSandboxTemplatesRequest
                {
                    WorkspaceScope = ClientCore.ListScope(workspace, options.AllWorkspaces),
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                    LabelSelector = options.LabelSelector ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListSandboxTemplatesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<SandboxWorkloadTemplate>(
                    response.Templates.Select(ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.DeleteSandboxTemplateRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteSandboxTemplateAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DeletionResult((DeletionOutcome)(int)response.Outcome);
    }

    internal static Wire.SandboxWorkloadTemplate ToWire(SandboxWorkloadTemplate t)
    {
        var meta = new ObjectMeta { Name = t.Name };
        SandboxMapping.AddAll(meta.Labels, t.Labels);
        SandboxMapping.AddAll(meta.Annotations, t.Annotations);

        var workload = new Wire.SandboxWorkloadConfig { Image = t.Image ?? "" };
        SandboxMapping.AddAll(workload.Environment, t.Environment);
        if (t.Resources is { } r)
        {
            var resources = new Wire.SandboxResources { Cpu = r.Cpu ?? "", Memory = r.Memory ?? "" };
            if (r.Gpu is { } gpu)
            {
                resources.Gpu = new Wire.GpuResourceRequirements();
                if (gpu.Count is { } count)
                {
                    resources.Gpu.Count = count;
                }
            }

            workload.Resources = resources;
        }

        var spec = new Wire.SandboxWorkloadTemplateSpec { Workload = workload };
        if (t.DriverConfigJson is { } json)
        {
            spec.DriverConfig = Struct.Parser.ParseJson(json);
        }

        if (t.Startup is { } startup)
        {
            spec.DesiredServiceLevel = new Wire.SandboxServiceLevel
            {
                Startup = new Wire.SandboxStartup { MaxBurst = startup.MaxBurst },
            };
            if (startup.ReadyWithin is { } within)
            {
                spec.DesiredServiceLevel.Startup.ReadyWithin = Duration.FromTimeSpan(within);
            }
        }

        return new Wire.SandboxWorkloadTemplate { Metadata = meta, Spec = spec };
    }

    internal static SandboxWorkloadTemplate ToDomain(Wire.SandboxWorkloadTemplate t)
    {
        var meta = t.Metadata;
        var workload = t.Spec?.Workload;
        return new SandboxWorkloadTemplate
        {
            Name = meta?.Name ?? "",
            Id = meta?.Id is { Length: > 0 } id ? id : null,
            Workspace = meta?.Workspace is { Length: > 0 } ws ? ws : null,
            Labels = SandboxMapping.Copy(meta?.Labels),
            Annotations = SandboxMapping.Copy(meta?.Annotations),
            ResourceVersion = meta?.ResourceVersion ?? 0,
            CreatedAt = meta?.CreatedTime?.ToDateTimeOffset(),
            Image = workload?.Image is { Length: > 0 } image ? image : null,
            Environment = SandboxMapping.Copy(workload?.Environment),
            Resources = workload?.Resources is { } res
                ? new WorkloadResources
                {
                    Cpu = res.Cpu is { Length: > 0 } cpu ? cpu : null,
                    Memory = res.Memory is { Length: > 0 } mem ? mem : null,
                    Gpu = res.Gpu is { } g ? new GpuRequirements(g.HasCount ? g.Count : null) : null,
                }
                : null,
            DriverConfigJson = t.Spec?.DriverConfig is { } dc ? JsonFormatter.Default.Format(dc) : null,
            Startup = t.Spec?.DesiredServiceLevel?.Startup is { } s
                ? new StartupServiceLevel(s.ReadyWithin?.ToTimeSpan(), s.MaxBurst)
                : null,
        };
    }
}
