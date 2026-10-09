// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

/// <summary>
/// An in-memory <see cref="IOpenShellClient"/> for unit tests. It keeps realistic state (workspace-scoped stores, name
/// uniqueness, resource versions, pagination, phase transitions, watch streams) and raises the same
/// <see cref="OpenShellException"/> kinds as the real client, with no network access.
/// </summary>
/// <remarks>
/// Use <see cref="Gateway"/> to inspect recorded calls, inject failures, script command results and drive time.
/// Views created with <see cref="InWorkspace"/> share the same state.
/// </remarks>
public sealed class FakeOpenShellClient : IOpenShellClient
{
    private readonly bool _ownsGateway;

    /// <summary>Creates a client over a new gateway that it disposes with itself.</summary>
    public FakeOpenShellClient(FakeGatewayOptions? options = null)
        : this(new FakeGateway(options), null, ownsGateway: true)
    {
    }

    /// <summary>Creates a client view over an existing gateway; disposing the view leaves the gateway alone.</summary>
    /// <param name="gateway">The shared state.</param>
    /// <param name="workspace">The workspace for this view; defaults to the gateway's default workspace.</param>
    public FakeOpenShellClient(FakeGateway gateway, string? workspace = null)
        : this(gateway, workspace, ownsGateway: false)
    {
    }

    private FakeOpenShellClient(FakeGateway gateway, string? workspace, bool ownsGateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (workspace is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        }

        _ownsGateway = ownsGateway;
        Gateway = gateway;
        Workspace = workspace ?? gateway.Options.DefaultWorkspace;
        Health = new FakeHealthClient(gateway);
        Sandboxes = new FakeSandboxClient(gateway, Workspace);
        Exec = new FakeExecClient(gateway, Workspace);
        Workspaces = new FakeWorkspaceClient(gateway);
        Templates = new FakeTemplateClient(gateway, Workspace);
        Rootfs = new FakeRootfsClient(gateway, Workspace);
        Config = new FakeConfigClient(gateway, Workspace);
        Policy = new FakePolicyClient(gateway, Workspace);
        Watch = new FakeWatchClient(gateway, Workspace);
        Providers = new FakeProviderClient(gateway, Workspace);
        SandboxProviders = new FakeSandboxProviderClient(gateway, Workspace);
        Services = new FakeServiceClient(gateway, Workspace);
        Ssh = new FakeSshClient(gateway, Workspace);
        Tcp = new FakeTcpClient(gateway, Workspace);
        Files = new FakeFileClient(gateway, Workspace);
    }

    /// <summary>The state and test hooks behind this client.</summary>
    public FakeGateway Gateway { get; }

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

    /// <inheritdoc />
    public IOpenShellClient InWorkspace(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        return new FakeOpenShellClient(Gateway, workspace, ownsGateway: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsGateway)
        {
            Gateway.Dispose();
        }
    }
}
