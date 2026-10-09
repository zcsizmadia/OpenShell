// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenShell.Testing;

internal sealed class FakeSandboxClient(FakeGateway gateway, string workspace) : ISandboxClient
{
    private const string RootfsTokenField = "rootfs_tar_staging_token";

    public ValueTask<Sandbox> CreateAsync(string? name, SandboxSpec spec, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Create(name, null, spec, options, cancellationToken);
    }

    public ValueTask<Sandbox> CreateFromTemplateAsync(string? name, string templateName, SandboxSpec? spec = null, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        return Create(name, templateName, spec, options, cancellationToken);
    }

    public ValueTask<Sandbox> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Sandboxes.Get", workspace, name, cancellationToken, () => gateway.Project(gateway.FindSandbox(workspace, name))));
    }

    public Pager<Sandbox> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return FakePaging.Create(gateway, "Sandboxes.List", workspace, null, options.PageSize, options.PageToken, () => Matching(options).Select(gateway.Project).ToList());
    }

    public ValueTask<IReadOnlyList<string>> ListIdsAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ListOptions();
        return ValueTask.FromResult(gateway.Run<IReadOnlyList<string>>("Sandboxes.ListIds", workspace, null, cancellationToken, () => [.. Matching(options).Select(e => e.Id)]));
    }

    public ValueTask<Sandbox> StopAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Sandboxes.Stop", workspace, name, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, name);
            if (entry.Phase is SandboxPhase.Stopped or SandboxPhase.Stopping)
            {
                return gateway.Project(entry);
            }

            if (entry.Phase is not (SandboxPhase.Ready or SandboxPhase.Starting or SandboxPhase.Provisioning or SandboxPhase.Completed))
            {
                throw FakeGateway.Conflict($"sandbox '{name}' cannot be stopped in phase {entry.Phase}");
            }

            entry.Phase = SandboxPhase.Stopping;
            gateway.BeginTransition(entry, SandboxPhase.Stopped, gateway.Options.TransitionDelay);
            gateway.Touch(entry);
            return gateway.Project(entry);
        }));
    }

    public ValueTask<Sandbox> StartAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Sandboxes.Start", workspace, name, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, name);
            if (entry.Phase is SandboxPhase.Ready or SandboxPhase.Starting or SandboxPhase.Provisioning)
            {
                return gateway.Project(entry);
            }

            if (entry.Phase != SandboxPhase.Stopped)
            {
                throw FakeGateway.Conflict($"sandbox '{name}' cannot be started in phase {entry.Phase}");
            }

            entry.Phase = SandboxPhase.Starting;
            entry.RestartCount++;
            gateway.BeginTransition(entry, SandboxPhase.Ready, gateway.Options.TransitionDelay);
            gateway.Touch(entry);
            return gateway.Project(entry);
        }));
    }

    public ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Sandboxes.Delete", workspace, name, cancellationToken, () =>
        {
            if (!gateway.Sandboxes.TryGetValue(new Key(workspace, name), out var entry))
            {
                return options is { AllowMissing: true }
                    ? new DeletionResult(DeletionOutcome.AlreadyAbsent)
                    : throw FakeGateway.NotFound("sandbox", name);
            }

            if (entry.Phase == SandboxPhase.Deleting)
            {
                return new DeletionResult(DeletionOutcome.Accepted, entry.Id);
            }

            var delay = gateway.Options.DeletionDelay;
            if (delay <= TimeSpan.Zero)
            {
                gateway.RemoveSandboxLocked(entry);
                return new DeletionResult(DeletionOutcome.Completed, entry.Id);
            }

            entry.Phase = SandboxPhase.Deleting;
            entry.DeletedAt = gateway.Time.GetUtcNow();
            gateway.BeginTransition(entry, SandboxPhase.Deleting, delay, remove: true);
            gateway.Touch(entry);
            return new DeletionResult(DeletionOutcome.Accepted, entry.Id);
        }));
    }

    public ValueTask<Sandbox> WaitReadyAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, ReadyStep, cancellationToken);

    public ValueTask<Sandbox> WaitStoppedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, StoppedStep, cancellationToken);

    public async ValueTask WaitDeletedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new WaitOptions();
        using var deadline = new Deadline(gateway.Time, options.Timeout, cancellationToken);
        var delay = options.PollInterval;
        var timeoutMessage = $"timed out waiting for sandbox '{name}' to delete";
        while (true)
        {
            try
            {
                var sandbox = await GetAsync(name, deadline.Token).ConfigureAwait(false);
                if (options.ExpectedSandboxId is { } expected && sandbox.Id != expected)
                {
                    return;
                }
            }
            catch (OpenShellException e) when (e.IsNotFound)
            {
                return;
            }
            catch (OperationCanceledException) when (deadline.TimedOut)
            {
                throw Deadline.Expired(timeoutMessage);
            }

            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, timeoutMessage).ConfigureAwait(false);
        }
    }

    // A wait step returns null when the wait is over, an empty string to keep polling, or the failure message.
    private static string? ReadyStep(Sandbox sandbox, string name)
    {
        return sandbox.Phase switch
        {
            SandboxPhase.Ready or SandboxPhase.Completed => null,
            SandboxPhase.Stopped => $"sandbox '{name}' stopped before becoming ready",
            SandboxPhase.Error => $"sandbox '{name}' entered error phase",
            _ => "",
        };
    }

    private static string? StoppedStep(Sandbox sandbox, string name)
    {
        return sandbox.Phase switch
        {
            SandboxPhase.Stopped => null,
            SandboxPhase.Error => $"sandbox '{name}' entered error phase",
            _ => "",
        };
    }

    private IEnumerable<SandboxEntry> Matching(ListOptions options)
    {
        var selector = LabelSelector.Compile(options.LabelSelector);
        return gateway.Sandboxes.Values
            .Where(e => FakePaging.InScope(e.Workspace, workspace, options.AllWorkspaces) && selector(e.Labels))
            .OrderBy(e => e.Workspace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal);
    }

    private ValueTask<Sandbox> Create(string? name, string? templateName, SandboxSpec? spec, CreateSandboxOptions? options, CancellationToken cancellationToken) =>
        ValueTask.FromResult(gateway.Run("Sandboxes.Create", workspace, name, cancellationToken, () =>
        {
            gateway.RequireWorkspace(workspace);
            if (gateway.ReplayName("sandbox", workspace, options?.RequestId) is { } replay
                && gateway.Sandboxes.TryGetValue(new Key(workspace, replay), out var original))
            {
                return gateway.Project(original);
            }

            var sandboxName = string.IsNullOrWhiteSpace(name) ? gateway.NextId("sandbox") : name;
            if (gateway.Sandboxes.ContainsKey(new Key(workspace, sandboxName)))
            {
                throw FakeGateway.AlreadyExists("sandbox", sandboxName);
            }

            var effective = ApplyTemplate(templateName, spec);
            ConsumeRootfsTokens(effective.Template?.DriverConfigJson);
            foreach (var provider in effective.Providers ?? [])
            {
                if (!gateway.ProviderStore.ContainsKey(new Key(workspace, provider)))
                {
                    throw FakeGateway.NotFound("provider", provider);
                }
            }

            var now = gateway.Time.GetUtcNow();
            var entry = new SandboxEntry
            {
                Id = gateway.NextId("sbx"),
                Name = sandboxName,
                Workspace = workspace,
                Phase = SandboxPhase.Provisioning,
                CreatedAt = now,
                Spec = effective,
                CreatePolicy = effective.Policy?.Clone(),
            };
            Copy(options?.Labels, entry.Labels);
            Copy(options?.Annotations, entry.Annotations);
            entry.Providers.AddRange(effective.Providers ?? []);
            foreach (var exposure in effective.ServiceExposures ?? [])
            {
                gateway.AddService(entry, exposure.Name, exposure.TargetPort, false, exposure.AuthorizationMode);
            }

            gateway.Sandboxes[new Key(workspace, sandboxName)] = entry;
            gateway.AddRevision(entry, effective.Policy?.Clone() ?? new Openshell.Sandbox.V1.SandboxPolicy(), null);
            gateway.BeginTransition(entry, SandboxPhase.Ready, gateway.Options.ProvisioningDelay);
            gateway.Touch(entry);
            gateway.RememberRequest("sandbox", workspace, options?.RequestId, sandboxName);
            return gateway.Project(entry);
        }));

    private SandboxSpec ApplyTemplate(string? templateName, SandboxSpec? spec)
    {
        spec ??= new SandboxSpec();
        if (templateName is null)
        {
            return spec;
        }

        if (!gateway.Templates.TryGetValue(new Key(workspace, templateName), out var template))
        {
            throw FakeGateway.NotFound("sandbox template", templateName);
        }

        var environment = new Dictionary<string, string>(template.Environment ?? new Dictionary<string, string>());
        Copy(spec.Template?.Environment, environment);
        var merged = (spec.Template ?? new SandboxTemplateSpec()) with
        {
            Image = spec.Template?.Image ?? spec.Image ?? template.Image,
            Environment = environment,
            DriverConfigJson = spec.Template?.DriverConfigJson ?? template.DriverConfigJson,
        };
        return spec with { Template = merged };
    }

    /// <summary>Each staged rootfs archive token works once, like the gateway's single-use slots.</summary>
    private void ConsumeRootfsTokens(string? driverConfigJson)
    {
        if (string.IsNullOrWhiteSpace(driverConfigJson))
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(driverConfigJson);
        }
        catch (JsonException e)
        {
            throw new OpenShellException(OpenShellErrorKind.InvalidArgument, "driver config is not valid JSON", e);
        }

        if (root is not JsonObject drivers)
        {
            return;
        }

        foreach (var (_, section) in drivers)
        {
            if (section is JsonObject driver
                && driver.TryGetPropertyValue(RootfsTokenField, out var node)
                && node?.GetValueKind() == JsonValueKind.String)
            {
                var token = node.GetValue<string>();
                if (!gateway.StagedSlots.TryGetValue(token, out var slot) || slot.Consumed)
                {
                    throw FakeGateway.Invalid("rootfs staging token is unknown or already used");
                }

                slot.Consumed = true;
            }
        }
    }

    private async ValueTask<Sandbox> PollAsync(string name, WaitOptions? options, Func<Sandbox, string, string?> step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new WaitOptions();
        using var deadline = new Deadline(gateway.Time, options.Timeout, cancellationToken);
        var delay = options.PollInterval;
        var timeoutMessage = $"timed out waiting for sandbox '{name}'";
        while (true)
        {
            Sandbox sandbox;
            try
            {
                sandbox = await GetAsync(name, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.TimedOut)
            {
                throw Deadline.Expired(timeoutMessage);
            }

            // null: done, empty: keep waiting, otherwise a terminal failure message.
            var outcome = step(sandbox, name);
            if (outcome is null)
            {
                return sandbox;
            }

            if (outcome.Length > 0)
            {
                throw new OpenShellException(OpenShellErrorKind.Conflict, outcome);
            }

            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, timeoutMessage).ConfigureAwait(false);
        }
    }

    private static void Copy(IReadOnlyDictionary<string, string>? source, Dictionary<string, string> target)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (k, v) in source)
        {
            target[k] = v;
        }
    }
}
