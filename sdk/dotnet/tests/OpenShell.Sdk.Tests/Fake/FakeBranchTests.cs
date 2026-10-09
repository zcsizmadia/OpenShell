// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using OpenShell.Testing;
using WireV1 = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>Less common paths: option combinations, other-workspace data, and unusual phases.</summary>
public class FakeBranchTests
{
    private static FakeOpenShellClient New(FakeGatewayOptions? options = null) => FakeCoreTests.New(options);

    private static SandboxSpec Spec() => FakeCoreTests.Spec();

    [Test]
    public async Task Interactive_sessions_pass_every_exec_option_to_scripts()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        FakeExecInvocation? seen = null;
        client.Gateway.OnExec(i =>
        {
            seen = i;
            return null;
        });

        await using var session = await client.Exec.InteractiveAsync(
            "a",
            ["sh"],
            80,
            24,
            new ExecOptions
            {
                WorkingDirectory = "/w",
                Environment = new Dictionary<string, string> { ["K"] = "V" },
                Timeout = TimeSpan.FromSeconds(9),
                Stdin = new byte[] { 1, 2 },
                RequestId = "rid",
                NoLoginShell = true,
            });
        await session.WaitForExitAsync();

        await Assert.That(seen!.Options.WorkingDirectory).IsEqualTo("/w");
        await Assert.That(seen.Options.Environment!["K"]).IsEqualTo("V");
        await Assert.That(seen.Options.Timeout).IsEqualTo(TimeSpan.FromSeconds(9));
        await Assert.That(seen.Options.Stdin.ToArray()).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(seen.Options.RequestId).IsEqualTo("rid");
        await Assert.That(seen.Options.NoLoginShell).IsTrue();
        await Assert.That(seen.Workspace).IsEqualTo("default");
    }

    [Test]
    public async Task Interactive_sessions_without_options_leave_script_options_empty()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        FakeExecInvocation? seen = null;
        client.Gateway.OnExec(i =>
        {
            seen = i;
            return null;
        });

        await using var session = await client.Exec.InteractiveAsync("a", ["sh"], 80, 24);
        await session.WaitForExitAsync();

        await Assert.That(seen!.Options.WorkingDirectory).IsNull();
        await Assert.That(seen.Options.Environment).IsNull();
        await Assert.That(seen.Options.Timeout).IsNull();
        await Assert.That(seen.Options.RequestId).IsNull();
        await Assert.That(seen.CommandLine).IsEqualTo("sh");
    }

    [Test]
    public async Task Deleting_with_explicit_default_options_still_reports_missing_targets()
    {
        using var client = New();

        var ex = await Assert.That(async () => await client.Sandboxes.DeleteAsync("nope", new DeleteOptions())).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Deleting_other_resources_with_default_options_reports_missing_targets()
    {
        using var client = New();

        var ex = await Assert.That(async () => await client.Templates.DeleteAsync("nope", new DeleteOptions())).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Templates_without_environment_and_other_wait_timeouts()
    {
        using var client = New();
        await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "bare", Image = "i" });

        var created = await client.Sandboxes.CreateFromTemplateAsync("a", "bare", new SandboxSpec { Image = "own" });
        var readyTimeout = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a", new WaitOptions { Timeout = TimeSpan.Zero })).Throws<OpenShellException>();
        var deleteTimeout = await Assert.That(async () => await client.Sandboxes.WaitDeletedAsync("a", new WaitOptions { Timeout = TimeSpan.Zero })).Throws<OpenShellException>();

        await Assert.That(created.Image).IsEqualTo("own");
        await Assert.That(readyTimeout!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
        await Assert.That(deleteTimeout!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
    }

    [Test]
    public async Task Driver_config_sections_without_a_usable_token_are_ignored()
    {
        using var client = New();

        await client.Sandboxes.CreateAsync("a", new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = """{"vm":{"rootfs_tar_staging_token":null}}""" } });
        await client.Sandboxes.CreateAsync("b", new SandboxSpec { Template = new SandboxTemplateSpec { DriverConfigJson = """{"vm":{"other":1}}""" } });

        await Assert.That((await client.Sandboxes.ListIdsAsync()).Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(SandboxPhase.Provisioning)]
    [Arguments(SandboxPhase.Starting)]
    [Arguments(SandboxPhase.Completed)]
    public async Task Stop_is_allowed_from_every_running_like_phase(SandboxPhase phase)
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Gateway.SetPhase("a", phase);

        await Assert.That((await client.Sandboxes.StopAsync("a")).Phase).IsEqualTo(SandboxPhase.Stopping);
    }

    [Test]
    [Arguments(SandboxPhase.Provisioning)]
    [Arguments(SandboxPhase.Starting)]
    public async Task Start_is_a_no_op_while_starting_up(SandboxPhase phase)
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        client.Gateway.SetPhase("a", phase);

        await Assert.That((await client.Sandboxes.StartAsync("a")).Phase).IsEqualTo(phase);
    }

    [Test]
    public async Task Services_of_other_sandboxes_stay_separate()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.CreateAsync("b", Spec());
        await client.Services.ExposeAsync("a", "web", 80);
        await client.Services.ExposeAsync("a", "", 81);
        await client.Services.ExposeAsync("a", "pub", 82, new ExposeServiceOptions { Domain = true });
        await client.Services.ExposeAsync("b", "other", 83);

        var forA = await client.Services.List("a").ToListAsync();
        var sandboxB = await client.Sandboxes.GetAsync("b");
        await client.Sandboxes.DeleteAsync("a");

        await Assert.That(forA.Count).IsEqualTo(3);
        await Assert.That(forA.Single(s => s.Name == "").Url).EndsWith("/a");
        await Assert.That(forA.Single(s => s.Name == "pub").Url).StartsWith("https://pub.a.fake.example.com");
        await Assert.That(sandboxB.ServiceUrls.Keys).IsEquivalentTo(new[] { "other" });
        await Assert.That((await client.Services.GetAsync("b", "other")).TargetPort).IsEqualTo(83u);
    }

    [Test]
    public async Task Services_in_other_workspaces_do_not_leak_into_a_sandbox_with_the_same_name()
    {
        using var client = New();
        var other = client.InWorkspace("other");
        await client.Sandboxes.CreateAsync("a", Spec());
        await other.Sandboxes.CreateAsync("a", Spec());
        await other.Services.ExposeAsync("a", "theirs", 90);

        var mine = await client.Sandboxes.GetAsync("a");
        var listed = await client.Services.List("a").ToListAsync();
        await client.Sandboxes.DeleteAsync("a");

        await Assert.That(mine.ServiceUrls.Count).IsEqualTo(0);
        await Assert.That(listed.Count).IsEqualTo(0);
        await Assert.That((await other.Services.GetAsync("a", "theirs")).TargetPort).IsEqualTo(90u);
    }

    [Test]
    public async Task A_not_equal_selector_matches_resources_without_the_label()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("bare", Spec());
        await client.Sandboxes.CreateAsync("tagged", Spec(), new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["tier"] = "web" } });

        var names = (await client.Sandboxes.List(new ListOptions { LabelSelector = "tier!=web" }).ToListAsync()).Select(s => s.Name);

        await Assert.That(names).IsEquivalentTo(new[] { "bare" });
    }

    [Test]
    public async Task Status_followers_skip_logs_and_events_and_log_followers_skip_drafts()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var status = client.Watch.WatchAsync("a").GetAsyncEnumerator();
        var snapshot = status.MoveNextAsync().AsTask();
        await snapshot;
        var pending = status.MoveNextAsync().AsTask();
        client.Gateway.EmitLog("a", "ignored");
        client.Gateway.EmitEvent("a", "R", "ignored");
        client.Gateway.SetPhase("a", SandboxPhase.Error);

        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(((SandboxUpdated)status.Current).Sandbox.Phase).IsEqualTo(SandboxPhase.Error);

        await using var logs = client.Watch.WatchLogsAsync("a").GetAsyncEnumerator();
        var firstLog = logs.MoveNextAsync().AsTask();
        client.Gateway.ProposeChunk("a", "web", FakePolicyData.Rule("web"));
        client.Gateway.SetPhase("a", SandboxPhase.Ready);
        client.Gateway.EmitLog("a", "kept");

        await Assert.That(await firstLog.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(((LogReceived)logs.Current).Line.Message).IsEqualTo("kept");
    }

    [Test]
    public async Task Events_older_than_since_are_filtered_from_follow_streams()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch
            .WatchAsync("a", new WatchOptions { FollowStatus = true, FollowEvents = true, Since = DateTimeOffset.UtcNow.AddHours(1) })
            .GetAsyncEnumerator();
        await events.MoveNextAsync().AsTask();
        var pending = events.MoveNextAsync().AsTask();
        client.Gateway.EmitEvent("a", "R", "too old for the filter");
        client.Gateway.SetPhase("a", SandboxPhase.Error);

        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(events.Current).IsTypeOf<SandboxUpdated>();
    }

    [Test]
    public async Task Merge_policy_accepts_options()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var add = new WireV1.PolicyMergeOperation { AddRule = new WireV1.AddNetworkRule { RuleName = "web", Rule = FakePolicyData.Rule("web") } };

        var result = await client.Config.MergePolicyAsync("a", [add], new SetPolicyOptions { Annotations = new Dictionary<string, string> { ["k"] = "v" }, RequestId = "r" });

        await Assert.That(result.Annotations["k"]).IsEqualTo("v");
    }

    [Test]
    public async Task Rejecting_without_a_reason_and_listing_revisions_with_defaults()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var id = client.Gateway.ProposeChunk("a", "web", FakePolicyData.Rule("web"));

        await client.Policy.RejectChunkAsync("a", id);
        var revisions = await client.Policy.ListRevisions("a").ToListAsync();

        await Assert.That((await client.Policy.GetDraftAsync("a")).Chunks.Single().RejectionReason).IsEqualTo("");
        await Assert.That(revisions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Ssh_tunnels_work_without_options()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await using var withDefaults = await client.Ssh.TunnelAsync("a", new TunnelOptions());
        await using var withNone = await client.Ssh.TunnelAsync("a");
        await withDefaults.WriteAsync(Encoding.UTF8.GetBytes("x"));
        var buffer = new byte[4];

        await Assert.That(await withDefaults.ReadAsync(buffer)).IsEqualTo(1);
    }

    [Test]
    public async Task Provider_deletion_ignores_sandboxes_in_other_workspaces()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.InWorkspace("other").Providers.CreateAsync(FakeProviderData.Github());
        await client.InWorkspace("other").Sandboxes.CreateAsync("s", new SandboxSpec { Providers = ["gh"] });

        var deleted = await client.Providers.DeleteAsync("gh");

        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
    }

    [Test]
    public async Task Provider_updates_notify_every_attached_sandbox_in_order()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("b", new SandboxSpec { Providers = ["gh"] });
        await client.Sandboxes.CreateAsync("a", new SandboxSpec { Providers = ["gh"] });
        await client.InWorkspace("other").Sandboxes.CreateAsync("z", FakeCoreTests.Spec());

        var result = await client.Providers.UpdateAsync(FakeProviderData.Github());

        await Assert.That(result.TargetReceipts.Select(r => r.Desired!.Sandbox)).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task Platform_profiles_reject_builtin_changes_and_report_missing_ones()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { BuiltinProfiles = [new ProviderProfile { Id = "github", DisplayName = "GitHub" }] });
        var item = new ProfileImportItem(new ProviderProfile { Id = "github", DisplayName = "X" });

        var update = await Assert.That(async () => await client.Providers.Profiles.Platform.UpdateAsync("github", 0, item)).Throws<OpenShellException>();
        var delete = await Assert.That(async () => await client.Providers.Profiles.Platform.DeleteAsync("github")).Throws<OpenShellException>();
        var missingUpdate = await Assert.That(async () => await client.Providers.Profiles.Platform.UpdateAsync("zzz", 0, item)).Throws<OpenShellException>();
        var missingDelete = await Assert.That(async () => await client.Providers.Profiles.Platform.DeleteAsync("zzz")).Throws<OpenShellException>();
        var workspaceMissing = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("zzz", 0, item)).Throws<OpenShellException>();
        var workspaceUpdate = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("github", 0, item)).Throws<OpenShellException>();
        var workspaceDelete = await Assert.That(async () => await client.Providers.Profiles.DeleteAsync("github")).Throws<OpenShellException>();

        await Assert.That(update!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(delete!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(missingUpdate!.IsNotFound).IsTrue();
        await Assert.That(missingDelete!.IsNotFound).IsTrue();
        await Assert.That(workspaceMissing!.IsNotFound).IsTrue();
        await Assert.That(workspaceUpdate!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(workspaceDelete!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Refresh_status_only_lists_the_requested_provider_and_workspace()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github("one"));
        await client.Providers.CreateAsync(FakeProviderData.Github("two"));
        await client.InWorkspace("other").Providers.CreateAsync(FakeProviderData.Github("one"));
        static RefreshConfig Config(string provider, string key = "GITHUB_TOKEN")
        {
            return new()
            {
                Provider = provider,
                CredentialKey = key,
                Strategy = RefreshStrategy.Static,
            };
        }

        await client.Providers.Refresh.ConfigureAsync(Config("one"));
        await client.Providers.Refresh.ConfigureAsync(Config("two"));
        await client.InWorkspace("other").Providers.Refresh.ConfigureAsync(Config("one"));

        await Assert.That((await client.Providers.Refresh.GetStatusAsync("one")).Single().Provider).IsEqualTo("one");
        await Assert.That((await client.Providers.Refresh.GetStatusAsync("two", "GITHUB_TOKEN")).Single().Provider).IsEqualTo("two");
    }

    [Test]
    public async Task Provider_status_picks_the_matching_sandbox_and_provider()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github("one"));
        await client.Providers.CreateAsync(FakeProviderData.Github("two"));
        await client.Sandboxes.CreateAsync("s1", FakeCoreTests.Spec());
        await client.Sandboxes.CreateAsync("s2", FakeCoreTests.Spec());
        await client.InWorkspace("other").Providers.CreateAsync(FakeProviderData.Github("one"));
        await client.InWorkspace("other").Sandboxes.CreateAsync("s1", FakeCoreTests.Spec());
        var first = await client.SandboxProviders.AttachAsync("s1", "one");
        await client.SandboxProviders.AttachAsync("s1", "two");
        await client.SandboxProviders.AttachAsync("s2", "one");
        await client.InWorkspace("other").SandboxProviders.AttachAsync("s1", "one");

        var status = await client.SandboxProviders.GetStatusAsync("s1", "one");

        await Assert.That(status.Receipt!.ReceiptId).IsEqualTo(first.Receipt!.ReceiptId);
    }

    [Test]
    public async Task Watch_and_log_filters_accept_unusual_option_shapes()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.EmitLog("a", "x");

        var empty = await client.Watch.GetLogsAsync("a", new LogOptions { Sources = [], MinLevel = "" });
        var defaults = await client.Watch.GetLogsAsync("a", new LogOptions());
        var none = await client.Watch.GetLogsAsync("a", null);

        await Assert.That(empty.Lines.Count).IsEqualTo(1);
        await Assert.That(defaults.Lines.Count).IsEqualTo(1);
        await Assert.That(none.Lines.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Listing_with_page_sizes_larger_than_the_data_returns_one_page()
    {
        using var client = New();
        await client.Sandboxes.CreateAsync("a", Spec());

        var pages = new List<Page<Sandbox>>();
        await foreach (var page in client.Sandboxes.List(new ListOptions { PageSize = 50 }).PagesAsync())
        {
            pages.Add(page);
        }

        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].HasMore).IsFalse();
    }
}
