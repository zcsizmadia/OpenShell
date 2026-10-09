// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using GrpcService = Openshell.V1.OpenShell;

namespace OpenShell;

/// <summary>The entry point to the OpenShell gateway API, for mocking and dependency injection.</summary>
public interface IOpenShellClient : IDisposable
{
    /// <summary>The workspace this view operates in.</summary>
    string Workspace { get; }

    /// <summary>Health and identity.</summary>
    IHealthClient Health { get; }

    /// <summary>Sandbox lifecycle.</summary>
    ISandboxClient Sandboxes { get; }

    /// <summary>Command execution.</summary>
    IExecClient Exec { get; }

    /// <summary>Workspace and membership management.</summary>
    IWorkspaceClient Workspaces { get; }

    /// <summary>Reusable sandbox workload templates.</summary>
    ISandboxTemplateClient Templates { get; }

    /// <summary>Local rootfs tar staging for drivers that boot from an archive.</summary>
    IRootfsClient Rootfs { get; }

    /// <summary>Sandbox and gateway configuration.</summary>
    IConfigClient Config { get; }

    /// <summary>Draft policy review and policy revisions.</summary>
    IPolicyClient Policy { get; }

    /// <summary>Watch streams and logs.</summary>
    ISandboxWatchClient Watch { get; }

    /// <summary>Returns a view of this client bound to another workspace; the connection is shared.</summary>
    IOpenShellClient InWorkspace(string workspace);
}

/// <summary>A connected client for one OpenShell gateway. Thread safe; create one and reuse it.</summary>
public sealed class OpenShellClient : IOpenShellClient
{
    private readonly ClientCore _core;
    private readonly bool _ownsCore;

    /// <summary>Connects lazily; no network I/O happens until the first call.</summary>
    public OpenShellClient(OpenShellClientOptions options)
        : this(Build(options), options.DefaultWorkspace, ownsCore: true)
    {
    }

    private OpenShellClient(ClientCore core, string workspace, bool ownsCore)
    {
        _core = core;
        _ownsCore = ownsCore;
        Workspace = workspace;
        Health = new HealthClient(core);
        Sandboxes = new SandboxClient(core, workspace);
        Exec = new ExecClient(core, workspace);
        Workspaces = new WorkspaceClient(core);
        Templates = new SandboxTemplateClient(core, workspace);
        Rootfs = new RootfsClient(core, workspace);
        Config = new ConfigClient(core, workspace);
        Policy = new PolicyClient(core, workspace);
        Watch = new SandboxWatchClient(core, workspace);
    }

    /// <inheritdoc />
    public string Workspace { get; }

    /// <inheritdoc />
    public IHealthClient Health { get; }

    /// <inheritdoc />
    public ISandboxClient Sandboxes { get; }

    /// <inheritdoc />
    public IExecClient Exec { get; }

    /// <inheritdoc />
    public IWorkspaceClient Workspaces { get; }

    /// <inheritdoc />
    public ISandboxTemplateClient Templates { get; }

    /// <inheritdoc />
    public IRootfsClient Rootfs { get; }

    /// <inheritdoc />
    public IConfigClient Config { get; }

    /// <inheritdoc />
    public IPolicyClient Policy { get; }

    /// <inheritdoc />
    public ISandboxWatchClient Watch { get; }

    /// <summary>The generated gRPC client, for RPCs the curated surface does not cover yet.</summary>
    public GrpcService.OpenShellClient Raw => _core.Grpc;

    /// <inheritdoc />
    public IOpenShellClient InWorkspace(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        return new OpenShellClient(_core, workspace, ownsCore: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsCore)
        {
            _core.Dispose();
        }
    }

    private static ClientCore Build(OpenShellClientOptions options)
    {
        var channel = Transport.ChannelFactory.Create(options);
        return new ClientCore(options, channel, new GrpcService.OpenShellClient(channel));
    }

    /// <summary>Test seam: builds a client over a pre-built gRPC invoker.</summary>
    internal static OpenShellClient ForTesting(OpenShellClientOptions options, Grpc.Core.CallInvoker invoker, TimeProvider? time = null) =>
        new(new ClientCore(options, null, new GrpcService.OpenShellClient(invoker)) { Time = time ?? TimeProvider.System },
            options.DefaultWorkspace,
            ownsCore: true);
}
