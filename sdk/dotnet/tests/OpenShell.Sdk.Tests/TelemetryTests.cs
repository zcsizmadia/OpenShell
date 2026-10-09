// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>Captures the SDK's spans and duration measurements for the calls made while it is alive.</summary>
internal sealed class TelemetryCapture : IDisposable
{
    private static readonly ActivitySource TestSource = new("OpenShell.Tests.Telemetry");

    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OpenShellTelemetry.ActivitySourceName or "OpenShell.Tests.Telemetry",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(_activities);

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpenShellTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meters.Start();

        Parent = TestSource.StartActivity("test")!;
    }

    public Activity Parent { get; }

    public ConcurrentBag<Activity> Stopped { get; } = [];

    public ConcurrentBag<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    /// <summary>The SDK spans produced under this capture's parent activity.</summary>
    public List<Activity> Spans => [.. Stopped.Where(a => a.Source.Name == OpenShellTelemetry.ActivitySourceName && a.TraceId == Parent.TraceId)];

    public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Durations(string method) =>
        [.. Measurements.Where(m => m.Instrument == "rpc.client.duration" && Equals(m.Tags["rpc.method"], method))];

    public void Dispose()
    {
        Parent.Dispose();
        _activities.Dispose();
        _meters.Dispose();
    }
}

[NotInParallel]
public class TelemetryTests
{
    private static OpenShellClient NewClient(FakeCallInvoker invoker, bool retry = false) =>
        OpenShellClient.ForTesting(
            new OpenShellClientOptions
            {
                Address = new Uri("http://localhost"),
                Retry = new RetryPolicy { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero, Jitter = 0 },
            },
            invoker,
            useRetry: retry);

    private static string? Tag(Activity activity, string key) => activity.GetTagItem(key)?.ToString();

    [Test]
    public async Task Successful_unary_call_emits_a_client_span_and_a_duration()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse { Version = "1" });
        using var client = NewClient(invoker);

        await client.Health.CheckAsync();

        var span = capture.Spans.Single();
        await Assert.That(span.OperationName).IsEqualTo("openshell.v1.OpenShell/Health");
        await Assert.That(span.Kind).IsEqualTo(ActivityKind.Client);
        await Assert.That(Tag(span, "rpc.system")).IsEqualTo("grpc");
        await Assert.That(Tag(span, "rpc.service")).IsEqualTo("openshell.v1.OpenShell");
        await Assert.That(Tag(span, "rpc.method")).IsEqualTo("Health");
        await Assert.That(Tag(span, "rpc.grpc.status_code")).IsEqualTo("0");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
        await Assert.That(span.ParentSpanId).IsEqualTo(capture.Parent.SpanId);

        var duration = capture.Durations("Health").Single();
        await Assert.That(duration.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(duration.Tags["rpc.grpc.status_code"]).IsEqualTo(0);
        await Assert.That(duration.Tags["rpc.service"]).IsEqualTo("openshell.v1.OpenShell");
    }

    [Test]
    public async Task Failed_unary_call_records_the_grpc_status_and_an_error_span()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "gone");
        using var client = NewClient(invoker);

        await Assert.That(async () => await client.Sandboxes.GetAsync("x")).Throws<OpenShellException>();

        var span = capture.Spans.Single();
        await Assert.That(Tag(span, "rpc.grpc.status_code")).IsEqualTo(((int)StatusCode.NotFound).ToString());
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(span.StatusDescription).IsEqualTo("NotFound");
        await Assert.That(capture.Durations("GetSandbox").Single().Tags["rpc.grpc.status_code"]).IsEqualTo((int)StatusCode.NotFound);
    }

    [Test]
    public async Task Server_streaming_span_lasts_until_the_stream_ends()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit() }]);
        using var client = NewClient(invoker);
        using var call = client.Raw.ExecSandbox(new Wire.ExecSandboxRequest());

        await Assert.That(capture.Spans.Count).IsEqualTo(0);
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(capture.Spans.Count).IsEqualTo(0);
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();

        await Assert.That(Tag(capture.Spans.Single(), "rpc.grpc.status_code")).IsEqualTo("0");
    }

    [Test]
    public async Task Disposing_a_stream_early_is_recorded_as_cancelled()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => [new Wire.ExecSandboxEvent()]);
        using var client = NewClient(invoker);

        client.Raw.ExecSandbox(new Wire.ExecSandboxRequest()).Dispose();

        var span = capture.Spans.Single();
        await Assert.That(Tag(span, "rpc.grpc.status_code")).IsEqualTo(((int)StatusCode.Cancelled).ToString());
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task A_stream_that_fails_midway_records_the_failure_once()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Failing());
        using var client = NewClient(invoker);
        using var call = client.Raw.ExecSandbox(new Wire.ExecSandboxRequest());

        await call.ResponseStream.MoveNext(CancellationToken.None);
        await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None)).Throws<RpcException>();
        call.Dispose();

        var span = capture.Spans.Single();
        await Assert.That(Tag(span, "rpc.grpc.status_code")).IsEqualTo(((int)StatusCode.Unavailable).ToString());
        await Assert.That(capture.Durations("ExecSandbox").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Client_streaming_and_duplex_calls_are_traced()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker()
            .ClientStream<Wire.PushSandboxLogsRequest, Wire.PushSandboxLogsResponse>("PushSandboxLogs", _ => new Wire.PushSandboxLogsResponse())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", _ => Task.CompletedTask);
        using var client = NewClient(invoker);

        using (var upload = client.Raw.PushSandboxLogs())
        {
            await upload.RequestStream.CompleteAsync();
            await upload.ResponseAsync;
        }

        using (var duplex = client.Raw.ForwardTcp())
        {
            await duplex.RequestStream.CompleteAsync();
            await Assert.That(await duplex.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
        }

        await Assert.That(capture.Spans.Select(s => Tag(s, "rpc.method")!).Order()).IsEquivalentTo(new[] { "ForwardTcp", "PushSandboxLogs" });
        await Assert.That(capture.Spans.All(s => Tag(s, "rpc.grpc.status_code") == "0")).IsTrue();
    }

    [Test]
    public async Task Blocking_unary_calls_are_traced_for_success_and_failure()
    {
        using var capture = new TelemetryCapture();
        var ok = new InstrumentedCallInvoker(new BlockingStub(null));
        var failing = new InstrumentedCallInvoker(new BlockingStub(new RpcException(new Status(StatusCode.Internal, "boom"))));
        var method = new Method<Wire.HealthRequest, Wire.HealthResponse>(
            MethodType.Unary, "svc", "Ping", Marshallers.Create(_ => [], _ => new Wire.HealthRequest()), Marshallers.Create(_ => [], _ => new Wire.HealthResponse()));

        ok.BlockingUnaryCall(method, null, new CallOptions(), new Wire.HealthRequest());
        await Assert.That(() => failing.BlockingUnaryCall(method, null, new CallOptions(), new Wire.HealthRequest())).Throws<RpcException>();

        var statuses = capture.Spans.Select(s => Tag(s, "rpc.grpc.status_code")!).Order().ToArray();
        await Assert.That(statuses).IsEquivalentTo(new[] { "0", ((int)StatusCode.Internal).ToString() });
    }

    [Test]
    public async Task Synchronous_failure_while_starting_a_call_is_recorded_and_rethrown()
    {
        using var capture = new TelemetryCapture();
        using var client = NewClient(new FakeCallInvoker());

        // No handler is registered, so the fake throws while the call is being created.
        await Assert.That(async () => await client.Health.CheckAsync()).Throws<InvalidOperationException>();

        var span = capture.Spans.Single();
        await Assert.That(Tag(span, "rpc.grpc.status_code")).IsEqualTo(((int)StatusCode.Unknown).ToString());
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task A_retried_call_is_one_span_with_the_final_outcome()
    {
        using var capture = new TelemetryCapture();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts < 3 ? throw new RpcException(new Status(StatusCode.Unavailable, "blip")) : new Wire.HealthResponse());
        using var client = NewClient(invoker, retry: true);

        await client.Health.CheckAsync();

        await Assert.That(attempts).IsEqualTo(3);
        await Assert.That(capture.Spans.Count).IsEqualTo(1);
        await Assert.That(Tag(capture.Spans.Single(), "rpc.grpc.status_code")).IsEqualTo("0");
    }

    [Test]
    public async Task The_callers_current_activity_is_restored_after_the_call_starts()
    {
        using var capture = new TelemetryCapture();
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse());
        using var client = NewClient(invoker);

        using var call = client.Raw.HealthAsync(new Wire.HealthRequest());

        await Assert.That(Activity.Current).IsSameReferenceAs(capture.Parent);
        await call.ResponseAsync;
        await Assert.That(Activity.Current).IsSameReferenceAs(capture.Parent);
    }

    [Test]
    public async Task Without_listeners_calls_pass_straight_through()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse { Version = "ok" })
            .Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => [new Wire.ExecSandboxEvent()])
            .ClientStream<Wire.PushSandboxLogsRequest, Wire.PushSandboxLogsResponse>("PushSandboxLogs", _ => new Wire.PushSandboxLogsResponse())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", _ => Task.CompletedTask);
        var instrumented = new InstrumentedCallInvoker(invoker);
        var health = new Wire.OpenShell.OpenShellClient(instrumented);
        var before = Activity.Current;

        var response = await health.HealthAsync(new Wire.HealthRequest());
        using var stream = health.ExecSandbox(new Wire.ExecSandboxRequest());
        using var upload = health.PushSandboxLogs();
        using var duplex = health.ForwardTcp();

        await Assert.That(response.Version).IsEqualTo("ok");
        await Assert.That(Activity.Current).IsSameReferenceAs(before);
        await Assert.That(await stream.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(() => health.Health(new Wire.HealthRequest())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task The_source_and_meter_names_are_public_constants()
    {
        await Assert.That(OpenShellTelemetry.ActivitySourceName).IsEqualTo("OpenShell.Sdk");
        await Assert.That(OpenShellTelemetry.MeterName).IsEqualTo("OpenShell.Sdk");
    }

    private static IEnumerable<Wire.ExecSandboxEvent> Failing()
    {
        yield return new Wire.ExecSandboxEvent();
        throw new RpcException(new Status(StatusCode.Unavailable, "dropped"));
    }

    private sealed class BlockingStub(RpcException? failure) : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            failure is null ? Activator.CreateInstance<TResponse>() : throw failure;

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
