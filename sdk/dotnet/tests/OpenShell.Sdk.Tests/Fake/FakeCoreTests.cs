// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;

namespace OpenShell.Tests;

public class FakeCoreTests
{
    internal static FakeOpenShellClient New(FakeGatewayOptions? options = null) => new(options);

    internal static SandboxSpec Spec(string image = "img:1") => new() { Image = image };

    [Test]
    public async Task Client_exposes_every_surface()
    {
        using var client = New();

        await Assert.That(client.Workspace).IsEqualTo("default");
        await Assert.That(client.Health).IsNotNull();
        await Assert.That(client.Sandboxes).IsNotNull();
        await Assert.That(client.Exec).IsNotNull();
        await Assert.That(client.Workspaces).IsNotNull();
        await Assert.That(client.Templates).IsNotNull();
        await Assert.That(client.Rootfs).IsNotNull();
        await Assert.That(client.Config).IsNotNull();
        await Assert.That(client.Policy).IsNotNull();
        await Assert.That(client.Watch).IsNotNull();
        await Assert.That(client.Providers).IsNotNull();
        await Assert.That(client.SandboxProviders).IsNotNull();
        await Assert.That(client.Services).IsNotNull();
        await Assert.That(client.Ssh).IsNotNull();
        await Assert.That(client.Tcp).IsNotNull();
        await Assert.That(client.Files).IsNotNull();
        await Assert.That(client.Gateway.Options.DefaultWorkspace).IsEqualTo("default");
        await Assert.That(client.Providers.Profiles.Platform.Platform).IsNotNull();
    }

    [Test]
    public async Task Disposing_the_owning_client_rejects_further_calls()
    {
        var client = New();
        client.Dispose();
        client.Dispose();

        await Assert.That(async () => await client.Health.CheckAsync()).Throws<ObjectDisposedException>();
        await Assert.That(() => client.Gateway.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Disposing_a_view_keeps_the_gateway_alive()
    {
        using var gateway = new FakeGateway();
        var view = new FakeOpenShellClient(gateway);
        view.Dispose();
        var other = view.InWorkspace("team");
        other.Dispose();

        await Assert.That((await new FakeOpenShellClient(gateway).Health.CheckAsync()).Status).IsEqualTo(ServiceStatus.Healthy);
        gateway.Dispose();
        gateway.Dispose();
        await Assert.That(async () => await new FakeOpenShellClient(gateway).Health.CheckAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Constructors_validate_arguments()
    {
        using var gateway = new FakeGateway();

        await Assert.That(() => new FakeOpenShellClient((FakeGateway)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new FakeOpenShellClient(gateway, " ")).Throws<ArgumentException>();
        await Assert.That(new FakeOpenShellClient(gateway, "team").Workspace).IsEqualTo("team");
        using var client = New();
        await Assert.That(() => client.InWorkspace(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task InWorkspace_views_share_one_store()
    {
        using var client = New();
        var team = client.InWorkspace("team");
        await team.Sandboxes.CreateAsync("a", Spec());

        await Assert.That(team.Workspace).IsEqualTo("team");
        await Assert.That((await team.Sandboxes.GetAsync("a")).Workspace).IsEqualTo("team");
        await Assert.That(async () => await client.Sandboxes.GetAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Calls_are_recorded_in_order_and_can_be_cleared()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.GetAsync("a");

        var calls = client.Gateway.Calls;
        await Assert.That(calls.Select(c => c.Operation)).IsEquivalentTo(new[] { "Sandboxes.Create", "Sandboxes.Get" });
        await Assert.That(calls[0].Sequence).IsEqualTo(1);
        await Assert.That(calls[1].Target).IsEqualTo("a");
        await Assert.That(calls[1].Workspace).IsEqualTo("default");

        client.Gateway.ClearCalls();
        await Assert.That(client.Gateway.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FailNext_fails_matching_calls_once_by_default_and_clones_the_error()
    {
        using var client = New();
        var error = new OpenShellException(OpenShellErrorKind.Unavailable, "down");
        client.Gateway.FailNext("Health.Check", error);

        var first = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();
        await Assert.That(first!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(first.Message).IsEqualTo("down");
        await Assert.That(ReferenceEquals(first, error)).IsFalse();
        await Assert.That((await client.Health.CheckAsync()).Status).IsEqualTo(ServiceStatus.Healthy);
    }

    [Test]
    public async Task FailNext_supports_counts_prefixes_and_wildcards()
    {
        using var client = New();
        client.Gateway.FailNext("Sandboxes.*", new OpenShellException(OpenShellErrorKind.Internal, "boom"), times: 2);

        await Assert.That(async () => await client.Sandboxes.GetAsync("x")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Sandboxes.CreateAsync("x", Spec())).Throws<OpenShellException>();
        await client.Sandboxes.CreateAsync("x", Spec());

        client.Gateway.FailNext("*", new OpenShellException(OpenShellErrorKind.Unavailable, "any"));
        await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        client.Gateway.FailNext("Workspaces.Get", new OpenShellException(OpenShellErrorKind.Unavailable, "other"));
        await client.Health.CheckAsync();
        client.Gateway.ClearFailures();
        await Assert.That((await client.Workspaces.GetAsync("default")).Name).IsEqualTo("default");
    }

    [Test]
    public async Task FailNext_validates_arguments()
    {
        using var client = New();
        var error = new OpenShellException(OpenShellErrorKind.Internal, "x");

        await Assert.That(() => client.Gateway.FailNext(" ", error)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.FailNext("*", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Gateway.FailNext("*", error, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task A_cancelled_token_throws_before_anything_is_recorded()
    {
        using var client = New();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.Health.CheckAsync(cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(client.Gateway.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Health_results_can_be_replaced()
    {
        using var client = New(new FakeGatewayOptions { GatewayVersion = "9.9.9" });
        await Assert.That((await client.Health.CheckAsync()).Version).IsEqualTo("9.9.9");
        await Assert.That((await client.Health.GetGatewayInfoAsync()).ComputeDrivers.Single().Name).IsEqualTo("fake");
        await Assert.That((await client.Health.GetCurrentUserAsync()).Subject).IsEqualTo("fake-user");

        client.Gateway.Health = new HealthResult(ServiceStatus.Degraded, "x");
        client.Gateway.GatewayInfo = new GatewayInfo(ServiceStatus.Degraded, "x", [], []);
        client.Gateway.CurrentUser = new CurrentUser("u", "U", [], [], "idp");

        await Assert.That((await client.Health.CheckAsync()).Status).IsEqualTo(ServiceStatus.Degraded);
        await Assert.That((await client.Health.GetGatewayInfoAsync()).GatewayVersion).IsEqualTo("x");
        await Assert.That((await client.Health.GetCurrentUserAsync()).Subject).IsEqualTo("u");
    }

    [Test]
    public async Task Unknown_workspaces_are_created_on_demand_or_rejected()
    {
        using var lenient = New();
        await lenient.InWorkspace("new-ws").Sandboxes.CreateAsync("a", Spec());
        await Assert.That((await lenient.Workspaces.GetAsync("new-ws")).Name).IsEqualTo("new-ws");

        using var strict = New(new FakeGatewayOptions { AutoCreateWorkspaces = false });
        var ex = await Assert.That(async () => await strict.InWorkspace("nope").Sandboxes.CreateAsync("a", Spec())).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await strict.Workspaces.CreateAsync("yes");
        await strict.InWorkspace("yes").Sandboxes.CreateAsync("a", Spec());
    }

    [Test]
    public async Task Ids_are_deterministic()
    {
        using var first = New();
        using var second = New();

        await Assert.That((await first.Sandboxes.CreateAsync("a", Spec())).Id).IsEqualTo((await second.Sandboxes.CreateAsync("a", Spec())).Id);
    }

    [Test]
    public async Task Scheduled_transitions_follow_the_injected_clock_and_timers()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromSeconds(5) });
        await client.Sandboxes.CreateAsync("a", Spec());

        await Assert.That((await client.Sandboxes.GetAsync("a")).Phase).IsEqualTo(SandboxPhase.Provisioning);
        time.Advance(TimeSpan.FromSeconds(5));
        await Assert.That((await client.Sandboxes.GetAsync("a")).Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task Timers_that_fire_after_disposal_are_ignored()
    {
        var time = new FakeTimeProvider();
        var client = New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromSeconds(5) });
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Dispose();

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(client.Gateway.Calls.Count).IsEqualTo(1);
    }
}
