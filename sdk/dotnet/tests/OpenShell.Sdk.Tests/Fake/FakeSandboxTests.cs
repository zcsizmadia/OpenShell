// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;

namespace OpenShell.Tests;

public class FakeSandboxTests
{
    private static FakeOpenShellClient New(FakeGatewayOptions? options = null) => FakeCoreTests.New(options);

    private static SandboxSpec Spec(string image = "img:1") => FakeCoreTests.Spec(image);

    private static async Task Advance(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }

    [Test]
    public async Task Create_reports_provisioning_then_ready_and_maps_fields()
    {
        using var client = New();

        var created = await client.Sandboxes.CreateAsync(
            "sb",
            new SandboxSpec
            {
                Image = "py:3",
                ServiceExposures = [new ServiceExposure("web", 8080, ServiceAuthorizationMode.BearerPassthrough)],
            },
            new CreateSandboxOptions
            {
                Labels = new Dictionary<string, string> { ["env"] = "dev" },
                Annotations = new Dictionary<string, string> { ["a"] = "b" },
            });
        var read = await client.Sandboxes.GetAsync("sb");

        await Assert.That(created.Phase).IsEqualTo(SandboxPhase.Provisioning);
        await Assert.That(read.Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That(read.Name).IsEqualTo("sb");
        await Assert.That(read.Workspace).IsEqualTo("default");
        await Assert.That(read.Image).IsEqualTo("py:3");
        await Assert.That(read.Labels["env"]).IsEqualTo("dev");
        await Assert.That(read.Annotations["a"]).IsEqualTo("b");
        await Assert.That(read.ServiceUrls["web"]).Contains("web");
        await Assert.That(read.Conditions.Single().Status).IsEqualTo("True");
        await Assert.That(read.CurrentPolicyVersion).IsEqualTo(1u);
        await Assert.That(read.ResourceVersion).IsGreaterThan(0ul);
        await Assert.That(read.CreatedAt).IsNotNull();
    }

    [Test]
    public async Task Create_generates_names_and_rejects_duplicates()
    {
        using var client = New();

        var generated = await client.Sandboxes.CreateAsync(null, Spec());
        await client.Sandboxes.CreateAsync("dup", Spec());
        var ex = await Assert.That(async () => await client.Sandboxes.CreateAsync("dup", Spec())).Throws<OpenShellException>();

        await Assert.That(generated.Name).StartsWith("sandbox-");
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.AlreadyExists);
    }

    [Test]
    public async Task Create_with_the_same_request_id_returns_the_original()
    {
        using var client = New();
        var options = new CreateSandboxOptions { RequestId = "req-1" };

        var first = await client.Sandboxes.CreateAsync("a", Spec(), options);
        var replay = await client.Sandboxes.CreateAsync("other", Spec(), options);

        await Assert.That(replay.Id).IsEqualTo(first.Id);
        await Assert.That((await client.Sandboxes.ListIdsAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_request_id_whose_sandbox_is_gone_creates_a_new_one()
    {
        using var client = New();
        var options = new CreateSandboxOptions { RequestId = "req-1" };
        await client.Sandboxes.CreateAsync("a", Spec(), options);
        await client.Sandboxes.DeleteAsync("a");

        var again = await client.Sandboxes.CreateAsync("b", Spec(), options);

        await Assert.That(again.Name).IsEqualTo("b");
    }

    [Test]
    public async Task Create_validates_arguments()
    {
        using var client = New();

        await Assert.That(async () => await client.Sandboxes.CreateAsync("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Sandboxes.CreateFromTemplateAsync("a", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.GetAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.StopAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.StartAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.DeleteAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.WaitStoppedAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.WaitDeletedAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_rejects_unknown_providers_and_attaches_known_ones()
    {
        using var client = New();
        var missing = await Assert.That(async () => await client.Sandboxes.CreateAsync("a", new SandboxSpec { Providers = ["gh"] })).Throws<OpenShellException>();
        await Assert.That(missing!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);

        await client.Providers.CreateAsync(new Provider { Name = "gh", Type = "github" });
        await client.Sandboxes.CreateAsync("a", new SandboxSpec { Providers = ["gh"] });

        await Assert.That(await client.SandboxProviders.List("a").ToListAsync()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Create_from_template_merges_the_template_and_spec()
    {
        using var client = New();
        await client.Templates.CreateAsync(new SandboxWorkloadTemplate
        {
            Name = "t",
            Image = "tmpl:1",
            Environment = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
        });

        var plain = await client.Sandboxes.CreateFromTemplateAsync("p", "t");
        var overridden = await client.Sandboxes.CreateFromTemplateAsync(
            "o",
            "t",
            new SandboxSpec { Template = new SandboxTemplateSpec { Image = "own:1", Environment = new Dictionary<string, string> { ["B"] = "3" } } });
        var missing = await Assert.That(async () => await client.Sandboxes.CreateFromTemplateAsync("m", "nope")).Throws<OpenShellException>();

        await Assert.That(plain.Image).IsEqualTo("tmpl:1");
        await Assert.That(overridden.Image).IsEqualTo("own:1");
        await Assert.That(missing!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
    }

    [Test]
    public async Task A_rootfs_staging_token_works_exactly_once()
    {
        using var client = New();
        var slot = await client.Rootfs.BeginStagingAsync("rootfs.tar", 10);
        var spec = new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = RootfsDriverConfig.WithStagingToken(slot.StagingToken) } };

        await client.Sandboxes.CreateAsync("a", spec);
        var reuse = await Assert.That(async () => await client.Sandboxes.CreateAsync("b", spec)).Throws<OpenShellException>();
        var unknown = new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = RootfsDriverConfig.WithStagingToken("rootfs-nope") } };
        var stranger = await Assert.That(async () => await client.Sandboxes.CreateAsync("c", unknown)).Throws<OpenShellException>();

        await Assert.That(reuse!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(stranger!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Driver_config_that_is_not_json_or_not_an_object_is_handled()
    {
        using var client = New();
        var bad = new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = "{not json" } };
        var ex = await Assert.That(async () => await client.Sandboxes.CreateAsync("a", bad)).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);

        await client.Sandboxes.CreateAsync("b", new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = "[1,2]" } });
        await client.Sandboxes.CreateAsync("c", new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = """{"vm":"text","other":{"x":1},"n":{"rootfs_tar_staging_token":5}}""" } });
        await client.Sandboxes.CreateAsync("d", new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = "  " } });
    }

    [Test]
    public async Task List_pages_filters_and_scopes()
    {
        using var client = New();
        foreach (var name in new[] { "c", "a", "b" })
        {
            await client.Sandboxes.CreateAsync(name, Spec(), new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["tier"] = name == "b" ? "db" : "web" } });
        }

        await client.InWorkspace("other").Sandboxes.CreateAsync("z", Spec());

        var paged = new List<int>();
        await foreach (var page in client.Sandboxes.List(new ListOptions { PageSize = 2 }).PagesAsync())
        {
            paged.Add(page.Items.Count);
        }

        var names = (await client.Sandboxes.List().ToListAsync()).Select(s => s.Name);
        var web = (await client.Sandboxes.List(new ListOptions { LabelSelector = "tier=web" }).ToListAsync()).Select(s => s.Name);
        var notWeb = (await client.Sandboxes.List(new ListOptions { LabelSelector = "tier!=web" }).ToListAsync()).Select(s => s.Name);
        var all = (await client.Sandboxes.List(new ListOptions { AllWorkspaces = true }).ToListAsync()).Select(s => s.Name);
        var ids = await client.Sandboxes.ListIdsAsync(new ListOptions { AllWorkspaces = true });

        await Assert.That(paged).IsEquivalentTo(new[] { 2, 1 });
        await Assert.That(names).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(web).IsEquivalentTo(new[] { "a", "c" });
        await Assert.That(notWeb).IsEquivalentTo(new[] { "b" });
        await Assert.That(all).IsEquivalentTo(new[] { "a", "b", "c", "z" });
        await Assert.That(ids.Count).IsEqualTo(4);
    }

    [Test]
    public async Task List_resumes_from_a_token_and_rejects_bad_ones()
    {
        using var client = New();
        foreach (var name in new[] { "a", "b", "c" })
        {
            await client.Sandboxes.CreateAsync(name, Spec());
        }

        var firstPage = await client.Sandboxes.List(new ListOptions { PageSize = 1 }).PagesAsync().FirstAsync();
        var rest = await client.Sandboxes.List(new ListOptions { PageSize = 5, PageToken = firstPage.NextPageToken }).ToListAsync();

        await Assert.That(rest.Select(s => s.Name)).IsEquivalentTo(new[] { "b", "c" });
        foreach (var token in new[] { "x1", "oabc", "o99" })
        {
            var ex = await Assert.That(async () => await client.Sandboxes.List(new ListOptions { PageToken = token }).ToListAsync()).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        }
    }

    [Test]
    [Arguments("tier")]
    [Arguments("!missing")]
    [Arguments("tier==web")]
    [Arguments(" tier = web , !missing ")]
    public async Task Label_selectors_support_presence_absence_and_equality(string selector)
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec(), new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["tier"] = "web" } });

        await Assert.That((await client.Sandboxes.List(new ListOptions { LabelSelector = selector }).ToListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("=v")]
    [Arguments("!=v")]
    [Arguments("!")]
    [Arguments("a b=c")]
    public async Task Malformed_label_selectors_are_rejected(string selector)
    {
        using var client = New();

        var ex = await Assert.That(async () => await client.Sandboxes.List(new ListOptions { LabelSelector = selector }).ToListAsync()).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Get_of_a_missing_sandbox_is_not_found()
    {
        using var client = New();

        var ex = await Assert.That(async () => await client.Sandboxes.GetAsync("nope")).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Stop_and_start_walk_the_phases()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, TransitionDelay = TimeSpan.FromSeconds(3) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var stopping = await client.Sandboxes.StopAsync("a");
        var again = await client.Sandboxes.StopAsync("a");
        time.Advance(TimeSpan.FromSeconds(3));
        var stopped = await client.Sandboxes.GetAsync("a");
        var idempotent = await client.Sandboxes.StopAsync("a");
        var starting = await client.Sandboxes.StartAsync("a");
        var restart = await client.Sandboxes.StartAsync("a");
        time.Advance(TimeSpan.FromSeconds(3));
        var ready = await client.Sandboxes.GetAsync("a");
        var noop = await client.Sandboxes.StartAsync("a");

        await Assert.That(stopping.Phase).IsEqualTo(SandboxPhase.Stopping);
        await Assert.That(again.Phase).IsEqualTo(SandboxPhase.Stopping);
        await Assert.That(stopped.Phase).IsEqualTo(SandboxPhase.Stopped);
        await Assert.That(idempotent.Phase).IsEqualTo(SandboxPhase.Stopped);
        await Assert.That(starting.Phase).IsEqualTo(SandboxPhase.Starting);
        await Assert.That(restart.Phase).IsEqualTo(SandboxPhase.Starting);
        await Assert.That(ready.Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That(ready.RestartCount).IsEqualTo(1u);
        await Assert.That(noop.Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task Stop_and_start_reject_phases_that_cannot_transition()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Gateway.SetPhase("a", SandboxPhase.Error);

        var stop = await Assert.That(async () => await client.Sandboxes.StopAsync("a")).Throws<OpenShellException>();
        var start = await Assert.That(async () => await client.Sandboxes.StartAsync("a")).Throws<OpenShellException>();
        await Assert.That(stop!.IsConflict).IsTrue();
        await Assert.That(start!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Delete_completes_immediately_by_default()
    {
        using var client = New();
        var created = await client.Sandboxes.CreateAsync("a", Spec());

        var result = await client.Sandboxes.DeleteAsync("a");

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(result.ResourceId).IsEqualTo(created.Id);
        await Assert.That(async () => await client.Sandboxes.GetAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Delete_of_a_missing_sandbox_honours_allow_missing()
    {
        using var client = New();

        var tolerated = await client.Sandboxes.DeleteAsync("nope", new DeleteOptions { AllowMissing = true });
        var ex = await Assert.That(async () => await client.Sandboxes.DeleteAsync("nope")).Throws<OpenShellException>();

        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task A_slow_delete_is_accepted_then_removed()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, DeletionDelay = TimeSpan.FromSeconds(10) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var accepted = await client.Sandboxes.DeleteAsync("a");
        var repeat = await client.Sandboxes.DeleteAsync("a");
        var deleting = await client.Sandboxes.GetAsync("a");
        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.That(accepted.Outcome).IsEqualTo(DeletionOutcome.Accepted);
        await Assert.That(repeat.Outcome).IsEqualTo(DeletionOutcome.Accepted);
        await Assert.That(deleting.Phase).IsEqualTo(SandboxPhase.Deleting);
        await Assert.That(deleting.DeletedAt).IsNotNull();
        await Assert.That(async () => await client.Sandboxes.GetAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Deleting_a_sandbox_removes_its_services_and_files()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Services.ExposeAsync("a", "web", 80);
        client.Gateway.PutFile("a", "/x", new byte[] { 1 });
        await client.Sandboxes.DeleteAsync("a");
        await client.Sandboxes.CreateAsync("a", Spec());

        await Assert.That(await client.Services.List("a").ToListAsync()).Count().IsEqualTo(0);
        await Assert.That(client.Gateway.ReadFile("a", "/x")).IsNull();
    }

    [Test]
    public async Task WaitReady_polls_through_provisioning()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromSeconds(5) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var task = client.Sandboxes.WaitReadyAsync("a", new WaitOptions { PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await Advance(time, task);

        await Assert.That((await task).Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task WaitReady_accepts_completed_and_fails_fast_on_terminal_phases()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Gateway.SetPhase("a", SandboxPhase.Completed, exitCode: 0);
        await Assert.That((await client.Sandboxes.WaitReadyAsync("a")).ExitCode).IsEqualTo(0);

        client.Gateway.SetPhase("a", SandboxPhase.Error);
        var error = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();
        client.Gateway.SetPhase("a", SandboxPhase.Stopped);
        var stopped = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();

        await Assert.That(error!.Message).Contains("error phase");
        await Assert.That(stopped!.Message).Contains("stopped before becoming ready");
        await Assert.That(error.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
    }

    [Test]
    public async Task WaitReady_times_out_with_deadline_exceeded()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromHours(1) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var task = client.Sandboxes.WaitReadyAsync("a", new WaitOptions { Timeout = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await Advance(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
    }

    [Test]
    public async Task WaitReady_honours_caller_cancellation_and_propagates_errors()
    {
        using var client = New(new FakeGatewayOptions { TimeProvider = new FakeTimeProvider(), ProvisioningDelay = TimeSpan.FromHours(1) });
        await client.Sandboxes.CreateAsync("a", Spec());
        using var cts = new CancellationTokenSource();
        var waiting = client.Sandboxes.WaitReadyAsync("a", cancellationToken: cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.That(async () => await waiting).Throws<OperationCanceledException>();

        var ex = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("missing")).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task WaitStopped_completes_on_stopped_and_fails_on_error()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, TransitionDelay = TimeSpan.FromSeconds(3) });
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.StopAsync("a");

        var task = client.Sandboxes.WaitStoppedAsync("a").AsTask();
        await Advance(time, task);
        await Assert.That((await task).Phase).IsEqualTo(SandboxPhase.Stopped);

        client.Gateway.SetPhase("a", SandboxPhase.Error);
        await Assert.That(async () => await client.Sandboxes.WaitStoppedAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task WaitDeleted_returns_on_not_found_or_replaced_ids()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time, DeletionDelay = TimeSpan.FromSeconds(4) });
        var original = await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.DeleteAsync("a");

        var task = client.Sandboxes.WaitDeletedAsync("a").AsTask();
        await Advance(time, task);
        await task;

        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.WaitDeletedAsync("a", new WaitOptions { ExpectedSandboxId = original.Id });
    }

    [Test]
    public async Task WaitDeleted_times_out_while_the_same_sandbox_remains()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time });
        var created = await client.Sandboxes.CreateAsync("a", Spec());

        var task = client.Sandboxes.WaitDeletedAsync(
            "a", new WaitOptions { ExpectedSandboxId = created.Id, Timeout = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await Advance(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
    }

    [Test]
    public async Task WaitDeleted_propagates_other_failures()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Gateway.FailNext("Sandboxes.Get", new OpenShellException(OpenShellErrorKind.Unavailable, "down"));

        await Assert.That(async () => await client.Sandboxes.WaitDeletedAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task SetPhase_and_hooks_validate_their_targets()
    {
        using var client = New();

        await Assert.That(() => client.Gateway.SetPhase(" ", SandboxPhase.Error)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.SetPhase("nope", SandboxPhase.Error)).Throws<OpenShellException>();
        await Assert.That(() => client.Gateway.EmitLog("nope", "x")).Throws<OpenShellException>();
        await Assert.That(() => client.Gateway.EmitLog(" ", "x")).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.EmitLog("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Gateway.EmitEvent(" ", "r", "m")).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.EmitEvent("a", null!, "m")).Throws<ArgumentNullException>();
        await Assert.That(() => client.Gateway.EmitEvent("a", "r", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Gateway.EmitEvent("nope", "r", "m")).Throws<OpenShellException>();
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    public static async ValueTask<T> FirstAsync<T>(this IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("sequence is empty");
    }
}
