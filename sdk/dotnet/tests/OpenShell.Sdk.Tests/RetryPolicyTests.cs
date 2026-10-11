// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class RetryPolicyTests
{
    private static readonly RetryPolicy Fast = new()
    {
        MaxAttempts = 4,
        InitialBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromSeconds(1),
        Jitter = 0,
    };

    private static OpenShellClient NewClient(FakeCallInvoker invoker, FakeTimeProvider time, RetryPolicy policy, TimeSpan? timeout = null) =>
        OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), Retry = policy, DefaultTimeout = timeout },
            invoker,
            time,
            useRetry: true);

    private static async Task Drive(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
    }

    [Test]
    public async Task Retries_transient_reads_until_success()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts < 3
                ? throw new RpcException(new Status(StatusCode.Unavailable, "blip"))
                : new Wire.HealthResponse { Version = "ok" });
        using var client = NewClient(invoker, time, Fast);

        var task = client.Health.CheckAsync().AsTask();
        await Drive(time, task);

        await Assert.That((await task).Version).IsEqualTo("ok");
        await Assert.That(attempts).IsEqualTo(3);
    }

    [Test]
    public async Task Gives_up_after_max_attempts_with_the_last_error()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "down");
        using var client = NewClient(invoker, time, Fast);

        var task = client.Health.CheckAsync().AsTask();
        await Drive(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(4);
    }

    [Test]
    public async Task Does_not_retry_writes_by_default()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", StatusCode.Unavailable, "x");
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);

        await Assert.That(async () => await client.Sandboxes.CreateAsync("a", new SandboxSpec())).Throws<OpenShellException>();

        await Assert.That(invoker.CountOf("CreateSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task Writes_can_be_opted_in_with_a_custom_predicate()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", _ =>
            ++attempts < 2
                ? throw new RpcException(new Status(StatusCode.Unavailable, "x"))
                : ClientTests.SandboxReply("a"));
        using var client = NewClient(invoker, time, Fast with { IsIdempotent = name => name == "CreateSandbox" });

        var task = client.Sandboxes.CreateAsync("a", new SandboxSpec()).AsTask();
        await Drive(time, task);

        await Assert.That((await task).Name).IsEqualTo("a");
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Does_not_retry_non_retryable_status_codes()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "nope");
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);

        var ex = await Assert.That(async () => await client.Sandboxes.GetAsync("a")).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task Retryable_codes_are_configurable()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ =>
            ++attempts < 2
                ? throw new RpcException(new Status(StatusCode.ResourceExhausted, "slow down"))
                : ClientTests.SandboxReply("a"));
        using var client = NewClient(invoker, time, Fast with { RetryableStatusCodes = new HashSet<StatusCode> { StatusCode.ResourceExhausted } });

        var task = client.Sandboxes.GetAsync("a").AsTask();
        await Drive(time, task);

        await Assert.That((await task).Name).IsEqualTo("a");
    }

    [Test]
    public async Task None_and_single_attempt_policies_pass_straight_through()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "x");
        using var none = NewClient(invoker, new FakeTimeProvider(), RetryPolicy.None);

        await Assert.That(async () => await none.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
        await Assert.That(RetryPolicy.None.IsEnabled).IsFalse();
        await Assert.That(RetryPolicy.Default.IsEnabled).IsTrue();
    }

    [Test]
    public async Task Stops_retrying_when_the_caller_deadline_would_pass()
    {
        // A fake clock far from the wall clock: the deadline must come from this clock, not DateTime.UtcNow.
        // Mixing the two once made this test retry into a fake-time backoff that nothing advanced, hanging the run.
        var time = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "x");
        // The 50ms deadline is shorter than the first 100ms backoff, so no retry is attempted.
        using var client = NewClient(invoker, time, Fast, timeout: TimeSpan.FromMilliseconds(50));

        // Real-time guard so a regression fails instead of waiting forever on the fake clock.
        await Assert.That(async () => await client.Health.CheckAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Throws<OpenShellException>();

        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
    }

    [Test]
    public async Task Stops_retrying_when_the_caller_cancels()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "x");
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.Health.CheckAsync(cts.Token)).Throws<OpenShellException>();

        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_during_backoff_aborts_the_wait()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "x");
        using var client = NewClient(invoker, time, Fast);
        using var cts = new CancellationTokenSource();

        var task = client.Health.CheckAsync(cts.Token).AsTask();
        await Task.Yield();
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Backoff_grows_exponentially_and_is_capped()
    {
        var policy = new RetryPolicy { InitialBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500), BackoffMultiplier = 2, Jitter = 0 };

        await Assert.That(policy.DelayBefore(1, () => 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(100));
        await Assert.That(policy.DelayBefore(2, () => 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(policy.DelayBefore(3, () => 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(400));
        await Assert.That(policy.DelayBefore(4, () => 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(policy.DelayBefore(9, () => 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    [Arguments(0.0, 80)]
    [Arguments(0.5, 100)]
    [Arguments(1.0, 120)]
    public async Task Jitter_spreads_delays_symmetrically(double random, int expectedMs)
    {
        var policy = new RetryPolicy { InitialBackoff = TimeSpan.FromMilliseconds(100), Jitter = 0.2 };

        await Assert.That(policy.DelayBefore(1, () => random).TotalMilliseconds).IsEqualTo(expectedMs);
    }

    [Test]
    public async Task Jitter_is_clamped_to_the_unit_interval()
    {
        var policy = new RetryPolicy { InitialBackoff = TimeSpan.FromMilliseconds(100), Jitter = 5 };

        await Assert.That(policy.DelayBefore(1, () => 0).TotalMilliseconds).IsEqualTo(0);
        await Assert.That(policy.DelayBefore(1, () => 1).TotalMilliseconds).IsEqualTo(200);
    }

    [Test]
    [Arguments("Health", true)]
    [Arguments("GetSandbox", true)]
    [Arguments("ListSandboxes", true)]
    [Arguments("CreateSandbox", false)]
    [Arguments("DeleteSandbox", false)]
    [Arguments("ExecSandbox", false)]
    public async Task Default_idempotency_covers_reads_only(string rpc, bool expected)
    {
        await Assert.That(RetryPolicy.DefaultIsIdempotent(rpc)).IsEqualTo(expected);
    }

    [Test]
    public async Task Retried_call_exposes_headers_status_trailers_and_dispose()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse { Version = "v" });
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);

        using var call = client.Raw.HealthAsync(new Wire.HealthRequest());
        var response = await call.ResponseAsync;
        var headers = await call.ResponseHeadersAsync;

        await Assert.That(response.Version).IsEqualTo("v");
        await Assert.That(headers.Count).IsEqualTo(0);
        await Assert.That(call.GetStatus().StatusCode).IsEqualTo(StatusCode.OK);
        await Assert.That(call.GetTrailers().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Headers_task_faults_when_the_call_ultimately_fails()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "x");
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);

        using var call = client.Raw.GetSandboxAsync(new Wire.GetSandboxRequest { Name = "a" });

        await Assert.That(async () => await call.ResponseHeadersAsync).Throws<RpcException>();
    }

    [Test]
    public async Task Non_unary_calls_pass_through_untouched()
    {
        var invoker = new FakeCallInvoker()
            .Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => [new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit() }])
            .ClientStream<Wire.PushSandboxLogsRequest, Wire.PushSandboxLogsResponse>("PushSandboxLogs", _ => new Wire.PushSandboxLogsResponse())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", _ => Task.CompletedTask);
        using var client = NewClient(invoker, new FakeTimeProvider(), Fast);

        using var stream = client.Raw.ExecSandbox(new Wire.ExecSandboxRequest());
        await Assert.That(await stream.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();

        using var clientStream = client.Raw.PushSandboxLogs();
        await clientStream.RequestStream.CompleteAsync();
        await clientStream.ResponseAsync;

        using var duplex = client.Raw.ForwardTcp();
        await duplex.RequestStream.CompleteAsync();
        await Assert.That(await duplex.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();

        await Assert.That(() => client.Raw.Health(new Wire.HealthRequest())).Throws<NotSupportedException>();
    }
}
