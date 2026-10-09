// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

internal sealed class FakeTemplateClient(FakeGateway gateway, string workspace) : ISandboxTemplateClient
{
    public ValueTask<SandboxWorkloadTemplate> CreateAsync(SandboxWorkloadTemplate workloadTemplate, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workloadTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadTemplate.Name);
        return ValueTask.FromResult(gateway.Run("Templates.Create", workspace, workloadTemplate.Name, cancellationToken, () =>
        {
            gateway.RequireWorkspace(workspace);
            if (gateway.ReplayName("template", workspace, requestId) is { } replay && gateway.Templates.TryGetValue(new Key(workspace, replay), out var original))
            {
                return original;
            }

            var key = new Key(workspace, workloadTemplate.Name);
            if (gateway.Templates.ContainsKey(key))
            {
                throw FakeGateway.AlreadyExists("sandbox template", workloadTemplate.Name);
            }

            var stored = workloadTemplate with
            {
                Id = gateway.NextId("tmpl"),
                Workspace = workspace,
                ResourceVersion = gateway.NextVersion(),
                CreatedAt = gateway.Time.GetUtcNow(),
            };
            gateway.Templates[key] = stored;
            gateway.RememberRequest("template", workspace, requestId, workloadTemplate.Name);
            return stored;
        }));
    }

    public ValueTask<SandboxWorkloadTemplate> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Templates.Get", workspace, name, cancellationToken, () =>
            gateway.Templates.TryGetValue(new Key(workspace, name), out var template) ? template : throw FakeGateway.NotFound("sandbox template", name)));
    }

    public Pager<SandboxWorkloadTemplate> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return FakePaging.Create(gateway, "Templates.List", workspace, null, options.PageSize, options.PageToken, () =>
        {
            var selector = LabelSelector.Compile(options.LabelSelector);
            return gateway.Templates
                .Where(pair => FakePaging.InScope(pair.Key.Workspace, workspace, options.AllWorkspaces)
                    && selector(pair.Value.Labels ?? new Dictionary<string, string>()))
                .OrderBy(pair => pair.Key.Workspace, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToList();
        });
    }

    public ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Templates.Delete", workspace, name, cancellationToken, () =>
        {
            var key = new Key(workspace, name);
            var existed = gateway.Templates.TryGetValue(key, out var template);
            return FakeGateway.DeleteResult(existed, options, "sandbox template", name, () => gateway.Templates.Remove(key), template?.Id);
        }));
    }
}

public sealed partial class FakeGateway
{
    /// <summary>Registers a service on a sandbox. Names are unique per sandbox; the empty name is the unnamed service.</summary>
    internal ServiceEntry AddService(SandboxEntry sandbox, string name, uint port, bool domain, ServiceAuthorizationMode mode)
    {
        if (port is 0 or > ushort.MaxValue)
        {
            throw Invalid("service target port must be in range 1-65535");
        }

        var key = new SubKey(sandbox.Workspace, sandbox.Name, name);
        if (Services.ContainsKey(key))
        {
            throw AlreadyExists("service", name);
        }

        var service = new ServiceEntry
        {
            Workspace = sandbox.Workspace,
            Sandbox = sandbox.Name,
            SandboxId = sandbox.Id,
            Name = name,
            TargetPort = port,
            Domain = domain,
            AuthorizationMode = mode == ServiceAuthorizationMode.Unspecified ? ServiceAuthorizationMode.Strip : mode,
            ResourceVersion = NextVersion(),
            CreatedAt = Time.GetUtcNow(),
        };
        Services[key] = service;
        return service;
    }

    internal static ServiceEndpoint ToEndpoint(ServiceEntry s) => new()
    {
        Name = s.Name,
        Sandbox = s.Sandbox,
        SandboxId = s.SandboxId,
        TargetPort = s.TargetPort,
        Domain = s.Domain,
        AuthorizationMode = s.AuthorizationMode,
        Url = ServiceUrl(s),
        ResourceVersion = s.ResourceVersion,
        CreatedAt = s.CreatedAt,
    };
}

internal sealed class FakeServiceClient(FakeGateway gateway, string workspace) : IServiceClient
{
    public ValueTask<ServiceEndpoint> ExposeAsync(string sandbox, string name, uint targetPort, ExposeServiceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        options ??= new ExposeServiceOptions();
        return ValueTask.FromResult(gateway.Run("Services.Expose", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var service = gateway.AddService(entry, name, targetPort, options.Domain, options.AuthorizationMode);
            gateway.Touch(entry);
            return FakeGateway.ToEndpoint(service);
        }));
    }

    public ValueTask<ServiceEndpoint> GetAsync(string sandbox, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        return ValueTask.FromResult(gateway.Run("Services.Get", workspace, sandbox, cancellationToken, () =>
        {
            _ = gateway.FindSandbox(workspace, sandbox);
            return gateway.Services.TryGetValue(new SubKey(workspace, sandbox, name), out var service)
                ? FakeGateway.ToEndpoint(service)
                : throw FakeGateway.NotFound("service", name);
        }));
    }

    public Pager<ServiceEndpoint> List(string sandbox, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        options ??= new ListOptions();
        return FakePaging.Create(gateway, "Services.List", workspace, sandbox, options.PageSize, options.PageToken, () =>
        {
            _ = gateway.FindSandbox(workspace, sandbox);
            return gateway.Services.Values
                .Where(s => s.Workspace == workspace && s.Sandbox == sandbox)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(FakeGateway.ToEndpoint)
                .ToList();
        });
    }

    public ValueTask<DeletionResult> DeleteAsync(string sandbox, string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(name);
        return ValueTask.FromResult(gateway.Run("Services.Delete", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var key = new SubKey(workspace, sandbox, name);
            var existed = gateway.Services.ContainsKey(key);
            return FakeGateway.DeleteResult(existed, options, "service", name, () =>
            {
                gateway.Services.Remove(key);
                gateway.Touch(entry);
            });
        }));
    }
}
