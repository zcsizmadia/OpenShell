// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.IntegrationTests;

public class GatewayIntegrationTests
{
    [Test]
    public async Task Health_check_reports_a_version()
    {
        using var client = GatewayFixture.Connect();

        var health = await client.Health.CheckAsync();

        await Assert.That(health.Status).IsNotEqualTo(ServiceStatus.Unspecified);
        await Assert.That(health.Version).IsNotEmpty();
    }

    [Test]
    public async Task Gateway_info_lists_compute_drivers()
    {
        using var client = GatewayFixture.Connect();

        var info = await client.Health.GetGatewayInfoAsync();

        await Assert.That(info.GatewayVersion).IsNotEmpty();
        await Assert.That(info.ComputeDrivers.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Listing_workspaces_and_sandboxes_succeeds()
    {
        using var client = GatewayFixture.Connect();

        var workspaces = await client.Workspaces.List().ToListAsync();
        var sandboxes = await client.Sandboxes.List(new ListOptions { PageSize = 5 }).ToListAsync();

        await Assert.That(workspaces.Count).IsGreaterThan(0);
        await Assert.That(sandboxes).IsNotNull();
    }

    [Test]
    public async Task Missing_sandbox_reports_not_found()
    {
        using var client = GatewayFixture.Connect();

        var ex = await Assert.That(async () => await client.Sandboxes.GetAsync(GatewayFixture.UniqueName("missing"))).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    [Timeout(600_000)]
    public async Task Sandbox_lifecycle_with_exec_and_streaming(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var name = GatewayFixture.UniqueName("dotnet-smoke");
        var created = await client.Sandboxes.CreateAsync(
            name, new SandboxSpec { Image = GatewayFixture.Image }, new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["sdk"] = "dotnet" } }, cancellationToken);
        string? sandboxId = created.Id;
        try
        {
            var ready = await client.Sandboxes.WaitReadyAsync(name, new WaitOptions { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken);
            await Assert.That(ready.Phase).IsEqualTo(SandboxPhase.Ready);

            var result = await client.Exec.RunAsync(name, ["/bin/sh", "-c", "echo hello; echo oops 1>&2; exit 3"], cancellationToken: cancellationToken);
            await Assert.That(result.StdoutText.Trim()).IsEqualTo("hello");
            await Assert.That(result.StderrText.Trim()).IsEqualTo("oops");
            await Assert.That(result.ExitCode).IsEqualTo(3);

            var streamed = new List<ExecEvent>();
            await foreach (var e in client.Exec.StreamAsync(name, ["/bin/sh", "-c", "echo a; echo b"], cancellationToken: cancellationToken))
            {
                streamed.Add(e);
            }

            await Assert.That(streamed[^1]).IsEqualTo(new ExecExit(0));

            var labelled = await client.Sandboxes.List(new ListOptions { LabelSelector = "sdk=dotnet" }).ToListAsync(cancellationToken);
            await Assert.That(labelled.Any(s => s.Name == name)).IsTrue();
        }
        finally
        {
            var deletion = await client.Sandboxes.DeleteAsync(name, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
            if (deletion.Outcome == DeletionOutcome.Accepted)
            {
                await client.Sandboxes.WaitDeletedAsync(name, new WaitOptions { ExpectedSandboxId = sandboxId, Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
            }
        }
    }
}
