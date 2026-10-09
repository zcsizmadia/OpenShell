// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Sandbox lifecycle operations, scoped to one workspace.</summary>
public interface ISandboxClient
{
    /// <summary>Creates a sandbox. The call returns before the sandbox is ready; see <see cref="WaitReadyAsync"/>.</summary>
    ValueTask<Sandbox> CreateAsync(string? name, SandboxSpec spec, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a sandbox from a reusable workload template; <paramref name="spec"/> may override fields.</summary>
    ValueTask<Sandbox> CreateFromTemplateAsync(string? name, string templateName, SandboxSpec? spec = null, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a sandbox by name.</summary>
    ValueTask<Sandbox> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists sandboxes lazily, page by page.</summary>
    Pager<Sandbox> List(ListOptions? options = null);

    /// <summary>Lists the ids of all matching sandboxes, across every page.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync(ListOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stops a running sandbox.</summary>
    ValueTask<Sandbox> StopAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Starts a stopped sandbox.</summary>
    ValueTask<Sandbox> StartAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a sandbox. Inspect <see cref="DeletionResult.Outcome"/>; <c>Accepted</c> means cleanup is pending.</summary>
    ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox is ready (or completed).</summary>
    ValueTask<Sandbox> WaitReadyAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox is stopped.</summary>
    ValueTask<Sandbox> WaitStoppedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox name is gone, or resolves to a different id when <see cref="WaitOptions.ExpectedSandboxId"/> is set.</summary>
    ValueTask WaitDeletedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class SandboxClient(ClientCore core, string workspace) : ISandboxClient
{
    public ValueTask<Sandbox> CreateAsync(string? name, SandboxSpec spec, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return CreateCoreAsync(name, null, spec, options, cancellationToken);
    }

    public ValueTask<Sandbox> CreateFromTemplateAsync(string? name, string templateName, SandboxSpec? spec = null, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        return CreateCoreAsync(name, templateName, spec, options, cancellationToken);
    }

    public async ValueTask<Sandbox> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.GetSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetSandboxAsync(request, o), cancellationToken).ConfigureAwait(false);
        return SandboxMapping.ToDomain(response);
    }

    public Pager<Sandbox> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<Sandbox>(
            async (token, ct) =>
            {
                var request = new Wire.ListSandboxesRequest
                {
                    WorkspaceScope = ClientCore.ListScope(workspace, options.AllWorkspaces),
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                    LabelSelector = options.LabelSelector ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListSandboxesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<Sandbox>(
                    response.Sandboxes.Select(s => SandboxMapping.ToDomain(s)).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<IReadOnlyList<string>> ListIdsAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (var sandbox in List(options).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(sandbox.Id);
        }

        return ids;
    }

    public async ValueTask<Sandbox> StopAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.StopSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.StopSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Sandbox> StartAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.StartSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.StartSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.DeleteSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.DeleteSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public ValueTask<Sandbox> WaitReadyAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, static (sb, n) => sb.Phase switch
        {
            SandboxPhase.Ready or SandboxPhase.Completed => WaitStep.Done,
            SandboxPhase.Stopped => WaitStep.Fail($"sandbox '{n}' stopped before becoming ready"),
            SandboxPhase.Error => WaitStep.Fail($"sandbox '{n}' entered error phase"),
            _ => WaitStep.Continue,
        }, cancellationToken);

    public ValueTask<Sandbox> WaitStoppedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, static (sb, n) => sb.Phase switch
        {
            SandboxPhase.Stopped => WaitStep.Done,
            SandboxPhase.Error => WaitStep.Fail($"sandbox '{n}' entered error phase"),
            _ => WaitStep.Continue,
        }, cancellationToken);

    public async ValueTask WaitDeletedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new WaitOptions();
        using var deadline = new Deadline(core.Time, options.Timeout, cancellationToken);
        var delay = options.PollInterval;
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
                throw Deadline.Expired($"timed out waiting for sandbox '{name}' to delete");
            }

            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, $"timed out waiting for sandbox '{name}' to delete").ConfigureAwait(false);
        }
    }

    private async ValueTask<Sandbox> CreateCoreAsync(string? name, string? templateName, SandboxSpec? spec, CreateSandboxOptions? options, CancellationToken cancellationToken)
    {
        var request = new Wire.CreateSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name ?? "",
            WorkloadTemplate = templateName ?? "",
            AwaitMainProcessAttachment = options?.AwaitMainProcessAttachment ?? false,
            RequestId = options?.RequestId ?? "",
        };
        if (spec is not null)
        {
            request.Spec = SandboxMapping.ToWire(spec);
            if (spec.ServiceExposures is { } exposures)
            {
                request.ServiceExposures.AddRange(exposures.Select(SandboxMapping.ToWire));
            }
        }

        SandboxMapping.AddAll(request.Labels, options?.Labels);
        SandboxMapping.AddAll(request.Annotations, options?.Annotations);
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.CreateSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<Sandbox> PollAsync(string name, WaitOptions? options, Func<Sandbox, string, WaitStep> step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new WaitOptions();
        using var deadline = new Deadline(core.Time, options.Timeout, cancellationToken);
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

            var outcome = step(sandbox, name);
            if (outcome.IsDone)
            {
                return sandbox;
            }

            if (outcome.Failure is { } failure)
            {
                throw new OpenShellException(OpenShellErrorKind.Conflict, failure);
            }

            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, timeoutMessage).ConfigureAwait(false);
        }
    }

    private readonly record struct WaitStep(bool IsDone, string? Failure)
    {
        public static WaitStep Done => new(true, null);

        public static WaitStep Continue => new(false, null);

        public static WaitStep Fail(string message) => new(false, message);
    }
}
