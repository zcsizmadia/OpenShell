// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SandboxClientTests
{
    private static Wire.SandboxResponse Reply(string name, Wire.SandboxPhase phase = Wire.SandboxPhase.Ready, string id = "id-1") =>
        ClientTests.SandboxReply(name, phase, id);

    [Test]
    public async Task Create_sends_full_request_and_maps_response()
    {
        Wire.CreateSandboxRequest? seen = null;
        var response = Reply("sb", Wire.SandboxPhase.Provisioning);
        response.ServiceUrls["web"] = "https://web.example.com";
        response.Sandbox.Metadata.Labels["l"] = "1";
        response.Sandbox.Metadata.Annotations["a"] = "2";
        response.Sandbox.Metadata.ResourceVersion = 4;
        response.Sandbox.Metadata.CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1));
        response.Sandbox.Metadata.DeletionTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(2));
        response.Sandbox.HostKeyFingerprint = "SHA256:abc";
        response.Sandbox.Spec = new Wire.SandboxSpec { Template = new Wire.SandboxTemplate { Image = "py:3" } };
        response.Sandbox.Status.ExitCode = 3;
        response.Sandbox.Status.CurrentPolicyVersion = 9;
        response.Sandbox.Status.RestartCount = 2;
        response.Sandbox.Status.Conditions.Add(new Wire.SandboxCondition
        {
            Type = "Ready",
            Status = "False",
            Reason = "Pulling",
            Message = "m",
            TransitionTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
        });
        var invoker = new FakeCallInvoker().Unary<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", r =>
        {
            seen = r;
            return response;
        });
        using var client = ClientTests.NewClient(invoker, "ws1");

        var sandbox = await client.Sandboxes.CreateAsync(
            "sb",
            new SandboxSpec { Image = "py:3", ServiceExposures = [new ServiceExposure("web", 8080, ServiceAuthorizationMode.BearerPassthrough)] },
            new CreateSandboxOptions
            {
                Labels = new Dictionary<string, string> { ["k"] = "v" },
                Annotations = new Dictionary<string, string> { ["ak"] = "av" },
                AwaitMainProcessAttachment = true,
                RequestId = "req-1",
            });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(seen.Name).IsEqualTo("sb");
        await Assert.That(seen.Spec.Template.Image).IsEqualTo("py:3");
        await Assert.That(seen.Labels["k"]).IsEqualTo("v");
        await Assert.That(seen.Annotations["ak"]).IsEqualTo("av");
        await Assert.That(seen.AwaitMainProcessAttachment).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("req-1");
        var exposure = seen.ServiceExposures.Single();
        await Assert.That(exposure.Service).IsEqualTo("web");
        await Assert.That(exposure.TargetPort).IsEqualTo(8080u);
        await Assert.That(exposure.AuthorizationMode).IsEqualTo(Wire.ServiceAuthorizationMode.BearerPassthrough);

        await Assert.That(sandbox.Id).IsEqualTo("id-1");
        await Assert.That(sandbox.Phase).IsEqualTo(SandboxPhase.Provisioning);
        await Assert.That(sandbox.Labels["l"]).IsEqualTo("1");
        await Assert.That(sandbox.Annotations["a"]).IsEqualTo("2");
        await Assert.That(sandbox.ResourceVersion).IsEqualTo(4ul);
        await Assert.That(sandbox.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(sandbox.DeletedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(2));
        await Assert.That(sandbox.ServiceUrls["web"]).IsEqualTo("https://web.example.com");
        await Assert.That(sandbox.HostKeyFingerprint).IsEqualTo("SHA256:abc");
        await Assert.That(sandbox.Image).IsEqualTo("py:3");
        await Assert.That(sandbox.ExitCode).IsEqualTo(3);
        await Assert.That(sandbox.CurrentPolicyVersion).IsEqualTo(9u);
        await Assert.That(sandbox.RestartCount).IsEqualTo(2u);
        var cond = sandbox.Conditions.Single();
        await Assert.That(cond.Reason).IsEqualTo("Pulling");
        await Assert.That(cond.TransitionTime).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    [Test]
    public async Task Sandbox_without_optional_fields_has_safe_defaults()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>(
            "GetSandbox", _ => new Wire.SandboxResponse { Sandbox = new Wire.Sandbox() });
        using var client = ClientTests.NewClient(invoker);

        var sandbox = await client.Sandboxes.GetAsync("x");

        await Assert.That(sandbox.Phase).IsEqualTo(SandboxPhase.Unspecified);
        await Assert.That(sandbox.ExitCode).IsNull();
        await Assert.That(sandbox.Labels.Count).IsEqualTo(0);
        await Assert.That(sandbox.Conditions.Count).IsEqualTo(0);
        await Assert.That(sandbox.HostKeyFingerprint).IsNull();
        await Assert.That(sandbox.Image).IsNull();
        await Assert.That(sandbox.CreatedAt).IsNull();
    }

    [Test]
    public async Task CreateFromTemplate_sets_template_name_and_allows_missing_spec()
    {
        Wire.CreateSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", r =>
        {
            seen = r;
            return Reply("sb");
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Sandboxes.CreateFromTemplateAsync(null, "tmpl");

        await Assert.That(seen!.WorkloadTemplate).IsEqualTo("tmpl");
        await Assert.That(seen.Spec).IsNull();
        await Assert.That(seen.Name).IsEqualTo("");
    }

    [Test]
    public async Task Create_validates_arguments()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

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
    public async Task List_pages_lazily_and_applies_filters()
    {
        var requests = new List<Wire.ListSandboxesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxesRequest, Wire.ListSandboxesResponse>("ListSandboxes", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListSandboxesResponse();
            if (r.PageToken == "")
            {
                reply.Sandboxes.Add(Reply("a").Sandbox);
                reply.NextPageToken = "p2";
            }
            else
            {
                reply.Sandboxes.Add(Reply("b").Sandbox);
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Sandboxes.List(new ListOptions { PageSize = 1, LabelSelector = "env=dev" }).ToListAsync();

        await Assert.That(all.Select(s => s.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[0].PageSize).IsEqualTo(1);
        await Assert.That(requests[0].LabelSelector).IsEqualTo("env=dev");
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task List_all_workspaces_uses_all_selector_and_resume_token()
    {
        Wire.ListSandboxesRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxesRequest, Wire.ListSandboxesResponse>("ListSandboxes", r =>
        {
            seen = r;
            return new Wire.ListSandboxesResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        _ = await client.Sandboxes.List(new ListOptions { AllWorkspaces = true, PageToken = "resume" }).ToListAsync();

        await Assert.That(seen!.WorkspaceScope.SelectionCase).IsEqualTo(Openshell.Datamodel.V1.WorkspaceSelector.SelectionOneofCase.AllWorkspaces);
        await Assert.That(seen.PageToken).IsEqualTo("resume");
    }

    [Test]
    public async Task Stop_and_start_return_updated_sandbox()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.StopSandboxRequest, Wire.SandboxResponse>("StopSandbox", r => Reply(r.Name, Wire.SandboxPhase.Stopping))
            .Unary<Wire.StartSandboxRequest, Wire.SandboxResponse>("StartSandbox", r => Reply(r.Name, Wire.SandboxPhase.Starting));
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Sandboxes.StopAsync("a")).Phase).IsEqualTo(SandboxPhase.Stopping);
        await Assert.That((await client.Sandboxes.StartAsync("a")).Phase).IsEqualTo(SandboxPhase.Starting);
    }

    [Test]
    [Arguments(Wire.DeletionOutcome.Completed, DeletionOutcome.Completed, true)]
    [Arguments(Wire.DeletionOutcome.Accepted, DeletionOutcome.Accepted, false)]
    [Arguments(Wire.DeletionOutcome.AlreadyAbsent, DeletionOutcome.AlreadyAbsent, true)]
    [Arguments(Wire.DeletionOutcome.Unspecified, DeletionOutcome.Unspecified, false)]
    public async Task Delete_returns_typed_outcome(Wire.DeletionOutcome wire, DeletionOutcome expected, bool complete)
    {
        Wire.DeleteSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteSandboxRequest, Wire.DeleteSandboxResponse>("DeleteSandbox", r =>
        {
            seen = r;
            return new Wire.DeleteSandboxResponse { Outcome = wire, SandboxId = "sid" };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Sandboxes.DeleteAsync("a", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(expected);
        await Assert.That(result.IsComplete).IsEqualTo(complete);
        await Assert.That(result.ResourceId).IsEqualTo("sid");
        await Assert.That(seen!.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
    }

    [Test]
    public async Task Delete_without_id_has_null_resource_id()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteSandboxRequest, Wire.DeleteSandboxResponse>(
            "DeleteSandbox", _ => new Wire.DeleteSandboxResponse { Outcome = Wire.DeletionOutcome.Completed });
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Sandboxes.DeleteAsync("a")).ResourceId).IsNull();
    }

    [Test]
    public async Task WaitReady_polls_until_ready_with_backoff()
    {
        var time = new FakeTimeProvider();
        var phases = new Queue<Wire.SandboxPhase>([Wire.SandboxPhase.Provisioning, Wire.SandboxPhase.Provisioning, Wire.SandboxPhase.Ready]);
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phases.Dequeue()));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitReadyAsync("a", new WaitOptions { PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, task);

        await Assert.That((await task).Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(3);
    }

    [Test]
    public async Task WaitReady_accepts_completed()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", Wire.SandboxPhase.Completed));
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Sandboxes.WaitReadyAsync("a")).Phase).IsEqualTo(SandboxPhase.Completed);
    }

    [Test]
    [Arguments(Wire.SandboxPhase.Error, "error phase")]
    [Arguments(Wire.SandboxPhase.Stopped, "stopped before becoming ready")]
    public async Task WaitReady_fails_fast_on_terminal_phases(Wire.SandboxPhase phase, string expected)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phase));
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains(expected);
        await Assert.That(ex.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
    }

    [Test]
    public async Task WaitReady_times_out_with_deadline_exceeded()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", Wire.SandboxPhase.Provisioning));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitReadyAsync("a", new WaitOptions { Timeout = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
        await Assert.That(ex.Message).Contains("timed out");
    }

    [Test]
    public async Task WaitReady_honors_caller_cancellation()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", Wire.SandboxPhase.Provisioning));
        using var client = ClientTests.NewClient(invoker, time: new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var task = client.Sandboxes.WaitReadyAsync("a", cancellationToken: cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WaitReady_propagates_gateway_errors()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.PermissionDenied, "no");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.PermissionDenied);
    }

    [Test]
    public async Task WaitStopped_completes_on_stopped_and_fails_on_error()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", Wire.SandboxPhase.Stopped));
        using var client = ClientTests.NewClient(invoker);
        await Assert.That((await client.Sandboxes.WaitStoppedAsync("a")).Phase).IsEqualTo(SandboxPhase.Stopped);

        var failing = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", Wire.SandboxPhase.Error));
        using var failClient = ClientTests.NewClient(failing);
        await Assert.That(async () => await failClient.Sandboxes.WaitStoppedAsync("a")).Throws<OpenShellException>();
    }

    [Test]
    public async Task WaitStopped_keeps_polling_through_stopping()
    {
        var time = new FakeTimeProvider();
        var phases = new Queue<Wire.SandboxPhase>([Wire.SandboxPhase.Stopping, Wire.SandboxPhase.Stopped]);
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phases.Dequeue()));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitStoppedAsync("a").AsTask();
        await AdvanceUntilDone(time, task);

        await Assert.That((await task).Phase).IsEqualTo(SandboxPhase.Stopped);
    }

    [Test]
    public async Task WaitDeleted_returns_when_not_found()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ =>
            ++calls < 2 ? Reply("a", Wire.SandboxPhase.Deleting) : throw new RpcException(new Status(StatusCode.NotFound, "gone")));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitDeletedAsync("a").AsTask();
        await AdvanceUntilDone(time, task);
        await task;

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task WaitDeleted_returns_when_name_resolves_to_a_different_id()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", id: "replacement"));
        using var client = ClientTests.NewClient(invoker);

        await client.Sandboxes.WaitDeletedAsync("a", new WaitOptions { ExpectedSandboxId = "original" });

        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task WaitDeleted_keeps_waiting_while_same_id_present_then_times_out()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", id: "original"));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitDeletedAsync(
            "a", new WaitOptions { ExpectedSandboxId = "original", Timeout = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
    }

    [Test]
    public async Task WaitDeleted_propagates_other_errors()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.Unavailable, "x");
        using var client = ClientTests.NewClient(invoker);

        await Assert.That(async () => await client.Sandboxes.WaitDeletedAsync("a")).Throws<OpenShellException>();
    }

    private static async Task AdvanceUntilDone(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }
}
