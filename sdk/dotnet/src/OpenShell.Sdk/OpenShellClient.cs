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

    /// <summary>Providers, profiles and credential refresh.</summary>
    IProviderClient Providers { get; }

    /// <summary>Provider attachment and readiness for sandboxes.</summary>
    ISandboxProviderClient SandboxProviders { get; }

    /// <summary>Service exposure.</summary>
    IServiceClient Services { get; }

    /// <summary>SSH relay sessions and tunnels.</summary>
    ISshClient Ssh { get; }

    /// <summary>TCP forwarding to sandbox ports.</summary>
    ITcpClient Tcp { get; }

    /// <summary>File transfer.</summary>
    IFileClient Files { get; }

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
        Providers = new ProviderClient(core, workspace);
        SandboxProviders = new SandboxProviderClient(core, workspace);
        Services = new ServiceClient(core, workspace);
        Ssh = new SshClient(core, workspace, Sandboxes);
        Tcp = new TcpForwardClient(core, workspace, Sandboxes, Ssh);
        Files = new FileClient(Exec);
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

    /// <inheritdoc />
    public IProviderClient Providers { get; }

    /// <inheritdoc />
    public ISandboxProviderClient SandboxProviders { get; }

    /// <inheritdoc />
    public IServiceClient Services { get; }

    /// <inheritdoc />
    public ISshClient Ssh { get; }

    /// <inheritdoc />
    public ITcpClient Tcp { get; }

    /// <inheritdoc />
    public IFileClient Files { get; }

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

    private static Microsoft.Extensions.Logging.ILogger LoggerFor(OpenShellClientOptions options) =>
        options.LoggerFactory?.CreateLogger("OpenShell.Sdk") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Creates a client that disposes <paramref name="resource"/> together with its connection.</summary>
    internal static OpenShellClient CreateOwning(OpenShellClientOptions options, IDisposable resource) =>
        new(Build(options, resource), options.DefaultWorkspace, ownsCore: true);

    private static ClientCore Build(OpenShellClientOptions options, IDisposable? resource = null)
    {
        var channel = Transport.ChannelFactory.Create(options);
        var logger = LoggerFor(options);
        Grpc.Core.CallInvoker invoker = channel.CreateCallInvoker();
        if (options.Retry.IsEnabled)
        {
            invoker = new RetryCallInvoker(invoker, options.Retry, TimeProvider.System, logger: logger);
        }

        // Outside the retry layer, so a retried call is one span with the final outcome.
        invoker = new InstrumentedCallInvoker(invoker);

        return new ClientCore(options, channel, new GrpcService.OpenShellClient(invoker)) { OwnedResource = resource, Logger = logger };
    }

    /// <summary>Test seam: builds a client over a pre-built gRPC invoker.</summary>
    internal static OpenShellClient ForTesting(OpenShellClientOptions options, Grpc.Core.CallInvoker invoker, TimeProvider? time = null, bool useRetry = false)
    {
        var logger = LoggerFor(options);
        var effective = useRetry ? new RetryCallInvoker(invoker, options.Retry, time ?? TimeProvider.System, logger: logger) : invoker;
        return new OpenShellClient(
            new ClientCore(options, null, new GrpcService.OpenShellClient(new InstrumentedCallInvoker(effective))) { Time = time ?? TimeProvider.System, Logger = logger },
            options.DefaultWorkspace,
            ownsCore: true);
    }
}
