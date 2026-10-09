// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SandboxProviderClientTests
{
    private static Wire.Sandbox WireSandbox() => ClientTests.SandboxReply("sandbox").Sandbox;

    private static async Task Pump(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }
    }

    [Test]
    public async Task Attach_sends_guards_and_maps_the_receipt()
    {
        Wire.AttachSandboxProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.AttachSandboxProviderRequest, Wire.AttachSandboxProviderResponse>("AttachSandboxProvider", r =>
        {
            seen = r;
            return new Wire.AttachSandboxProviderResponse { Sandbox = WireSandbox(), Attached = true, Receipt = ProviderFixtures.Receipt() };
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var result = await client.SandboxProviders.AttachAsync(
            "sandbox", "gh", new SandboxProviderMutationOptions { ExpectedResourceVersion = 7, RequestId = "r" });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Sandbox).IsEqualTo("sandbox");
        await Assert.That(seen.Provider).IsEqualTo("gh");
        await Assert.That(seen.ExpectedResourceVersion).IsEqualTo(7ul);
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(result.Attached).IsTrue();
        await Assert.That(result.Sandbox.Name).IsEqualTo("sandbox");
        await Assert.That(result.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Attach);
        await Assert.That(result.Receipt.Desired!.ProviderEnvRevision).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Attach_without_receipt_or_options_uses_defaults()
    {
        Wire.AttachSandboxProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.AttachSandboxProviderRequest, Wire.AttachSandboxProviderResponse>("AttachSandboxProvider", r =>
        {
            seen = r;
            return new Wire.AttachSandboxProviderResponse { Sandbox = WireSandbox() };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.SandboxProviders.AttachAsync("sandbox", "gh");

        await Assert.That(seen!.ExpectedResourceVersion).IsEqualTo(0ul);
        await Assert.That(result.Attached).IsFalse();
        await Assert.That(result.Receipt).IsNull();
    }

    [Test]
    public async Task Detach_maps_the_receipt()
    {
        Wire.DetachSandboxProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DetachSandboxProviderRequest, Wire.DetachSandboxProviderResponse>("DetachSandboxProvider", r =>
        {
            seen = r;
            return new Wire.DetachSandboxProviderResponse
            {
                Sandbox = WireSandbox(),
                Detached = true,
                Receipt = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Detach),
            };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.SandboxProviders.DetachAsync(
            "sandbox", "gh", new SandboxProviderMutationOptions { ExpectedResourceVersion = 2, RequestId = "r" });

        await Assert.That(seen!.ExpectedResourceVersion).IsEqualTo(2ul);
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(result.Detached).IsTrue();
        await Assert.That(result.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Detach);

        var defaults = await client.SandboxProviders.DetachAsync("sandbox", "gh");
        await Assert.That(seen.ExpectedResourceVersion).IsEqualTo(0ul);
        await Assert.That(defaults.Detached).IsTrue();
    }

    [Test]
    public async Task List_pages_attached_providers()
    {
        var requests = new List<Wire.ListSandboxProvidersRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxProvidersRequest, Wire.ListSandboxProvidersResponse>("ListSandboxProviders", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListSandboxProvidersResponse();
            reply.Providers.Add(ProviderFixtures.WireProvider(r.PageToken == "" ? "a" : "b"));
            if (r.PageToken == "")
            {
                reply.NextPageToken = "p2";
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var all = await client.SandboxProviders.List("sandbox", new ListOptions { PageSize = 2 }).ToListAsync();
        _ = await client.SandboxProviders.List("sandbox").ToListAsync();

        await Assert.That(all.Select(p => p.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].Sandbox).IsEqualTo("sandbox");
        await Assert.That(requests[0].WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(requests[0].PageSize).IsEqualTo(2);
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task GetStatus_by_provider_and_receipt_id_validates_binding()
    {
        Wire.GetSandboxProviderStatusRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>("GetSandboxProviderStatus", r =>
        {
            seen = r;
            return new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready) };
        });
        using var client = ClientTests.NewClient(invoker);

        var status = await client.SandboxProviders.GetStatusAsync("sandbox", "provider", "receipt");
        var byReceiptOnly = await client.SandboxProviders.GetStatusAsync("sandbox", receiptId: "receipt");
        var byProviderOnly = await client.SandboxProviders.GetStatusAsync("sandbox", "provider");

        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(byReceiptOnly.State).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(byProviderOnly.State).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(seen!.Provider).IsEqualTo("provider");
        await Assert.That(seen.ReceiptId).IsEqualTo("");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("default");
    }

    [Test]
    public async Task GetStatus_requires_a_selector()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("sandbox")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("", "p")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync((ProviderMutationReceipt)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task GetStatus_rejects_a_response_for_another_authority()
    {
        var other = ProviderFixtures.Receipt();
        other.Desired.Sandbox = "other-sandbox";
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus",
            _ => new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(other, Wire.ProviderReadinessState.Ready) });
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("sandbox", "provider", "receipt")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Internal);
    }

    [Test]
    public async Task GetStatus_rejects_a_response_without_status()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus", _ => new Wire.GetSandboxProviderStatusResponse());
        using var client = ClientTests.NewClient(invoker);

        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("sandbox", "provider")).Throws<OpenShellException>();
    }

    [Test]
    public async Task GetStatus_errors_expose_only_the_protocol_code()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus", StatusCode.NotFound, "secret-bearing server detail");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("sandbox", "provider")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await Assert.That(ex.Message).DoesNotContain("secret-bearing");
        await Assert.That(ex.Message).Contains("NotFound");
        await Assert.That(ex.InnerException).IsNull();
    }

    [Test]
    public async Task GetStatus_by_receipt_uses_the_receipts_workspace_and_sandbox()
    {
        Wire.GetSandboxProviderStatusRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>("GetSandboxProviderStatus", r =>
        {
            seen = r;
            return new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready) };
        });
        using var client = ClientTests.NewClient(invoker, "another-workspace");

        var status = await client.SandboxProviders.GetStatusAsync(ProviderFixtures.Domain());

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("default");
        await Assert.That(seen.Sandbox).IsEqualTo("sandbox");
        await Assert.That(seen.ReceiptId).IsEqualTo("receipt");
        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync(ProviderFixtures.Domain() with { ReceiptId = "" })).Throws<OpenShellException>();
    }

    [Test]
    public async Task Wait_completes_once_the_exact_receipt_is_ready()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>("GetSandboxProviderStatus", _ =>
            new Wire.GetSandboxProviderStatusResponse
            {
                Status = ++calls == 1
                    ? ProviderFixtures.Pending(ProviderFixtures.Receipt())
                    : ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready),
            });
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.SandboxProviders.WaitAsync(ProviderFixtures.Domain(), TimeSpan.FromSeconds(30)).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Wait_for_detach_requires_revocation()
    {
        var time = new FakeTimeProvider();
        var detach = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Detach);
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus",
            _ => new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(detach, Wire.ProviderReadinessState.Revoked) });
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.SandboxProviders.WaitAsync(ProviderFixtures.Domain(Wire.ProviderMutationKind.Detach), TimeSpan.FromSeconds(30)).AsTask();
        await Pump(time, task);

        await Assert.That((await task).Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Wait_times_out_with_the_last_observed_state()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus",
            _ => new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Pending(ProviderFixtures.Receipt()) });
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.SandboxProviders.WaitAsync(ProviderFixtures.Domain(), TimeSpan.FromSeconds(2)).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
        await Assert.That(result.Status.State).IsEqualTo(ProviderReadinessState.Pending);
        await Assert.That(result.Status.Reason).IsEqualTo(ProviderReadinessReason.WaitingForProcess);
    }

    [Test]
    public async Task Wait_reports_a_later_mutation_as_terminal()
    {
        var time = new FakeTimeProvider();
        var newer = ProviderFixtures.Receipt();
        newer.Desired.ProviderEnvRevision = 2;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus",
            _ => new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(newer, Wire.ProviderReadinessState.Ready) });
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.SandboxProviders.WaitAsync(ProviderFixtures.Domain(), TimeSpan.FromSeconds(30)).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.Terminal);
        await Assert.That(result.Status.State).IsEqualTo(ProviderReadinessState.Superseded);
    }

    [Test]
    public async Task Wait_from_an_observed_status_returns_immediately_when_complete()
    {
        var invoker = new FakeCallInvoker();
        using var client = ClientTests.NewClient(invoker);
        var status = ProviderFixtures.DomainStatus(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);

        var result = await client.SandboxProviders.WaitAsync(status, TimeSpan.FromSeconds(5));

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(invoker.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Wait_from_a_pending_status_continues_polling()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>(
            "GetSandboxProviderStatus",
            _ => new Wire.GetSandboxProviderStatusResponse { Status = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready) });
        using var client = ClientTests.NewClient(invoker, time: time);
        var pending = ProviderReadinessLogic.ToDomain(ProviderFixtures.Pending(ProviderFixtures.Receipt()));

        var task = client.SandboxProviders.WaitAsync(pending, TimeSpan.FromSeconds(30)).AsTask();
        await Pump(time, task);

        await Assert.That((await task).Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Wait_from_a_status_without_receipt_is_invalid()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.SandboxProviders.WaitAsync(
            new ProviderReadinessStatus { State = ProviderReadinessState.Pending }, TimeSpan.FromSeconds(1))).Throws<OpenShellException>();
    }

    [Test]
    public async Task WaitAll_shares_one_deadline_across_receipts()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxProviderStatusRequest, Wire.GetSandboxProviderStatusResponse>("GetSandboxProviderStatus", r =>
        {
            var receipt = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Update);
            receipt.ReceiptId = r.ReceiptId;
            receipt.Desired.Sandbox = r.Sandbox;
            return new Wire.GetSandboxProviderStatusResponse
            {
                Status = r.ReceiptId == "ready"
                    ? ProviderFixtures.Status(receipt, Wire.ProviderReadinessState.Ready)
                    : ProviderFixtures.Pending(receipt),
            };
        });
        using var client = ClientTests.NewClient(invoker, time: time);
        var ready = ProviderFixtures.Domain(Wire.ProviderMutationKind.Update) with { ReceiptId = "ready" };
        var stuck = ProviderFixtures.Domain(Wire.ProviderMutationKind.Update) with { ReceiptId = "stuck" };

        var task = client.SandboxProviders.WaitAllAsync([ready, stuck], TimeSpan.FromSeconds(2)).AsTask();
        await Pump(time, task);
        var results = await task;

        await Assert.That(results[0].Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(results[1].Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
    }

    [Test]
    public async Task Wait_validates_arguments()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var receipt = ProviderFixtures.Domain();

        await Assert.That(async () => await client.SandboxProviders.WaitAsync(receipt, TimeSpan.Zero)).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync(receipt, TimeSpan.FromHours(2))).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync((ProviderMutationReceipt)null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync((ProviderReadinessStatus)null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAllAsync(null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAllAsync([receipt], TimeSpan.Zero)).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.AttachAsync("", "p")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.AttachAsync("s", "")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.DetachAsync("", "p")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.DetachAsync("s", "")).Throws<ArgumentException>();
        await Assert.That(() => client.SandboxProviders.List("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Root_client_exposes_providers_in_the_current_workspace()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRequest, Wire.ProviderResponse>(
            "GetProvider", _ => new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider() });
        using var client = ClientTests.NewClient(invoker);

        await client.InWorkspace("scoped").Providers.GetAsync("p");

        await Assert.That(((Wire.GetProviderRequest)invoker.Calls.Single().Request).WorkspaceScope.Workspace).IsEqualTo("scoped");
        await Assert.That(client.SandboxProviders).IsNotNull();
    }
}
