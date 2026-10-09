// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class WatchClientTests
{
    private static Wire.SandboxStreamEvent LogEvent(string cursor, string message = "m") => new()
    {
        Cursor = cursor,
        Log = new Wire.SandboxLogLine
        {
            SandboxId = "id",
            EventTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
            Level = "INFO",
            Target = "t",
            Message = message,
            Source = "sandbox",
            Fields = { ["k"] = "v" },
        },
    };

    private static Wire.SandboxStreamEvent PlatformEventFrame(string cursor) => new()
    {
        Cursor = cursor,
        Event = new Wire.PlatformEvent { EventTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(5)), Source = "docker", Type = "Normal", Reason = "Started", Message = "up", Metadata = { ["a"] = "b" } },
    };

    private static Wire.SandboxStreamEvent StatusFrame(Wire.SandboxPhase phase) => new()
    {
        Cursor = "ignored",
        Sandbox = new Wire.Sandbox
        {
            Metadata = new Openshell.Datamodel.V1.ObjectMeta { Id = "id", Name = "sb" },
            Status = new Wire.SandboxStatus { Phase = phase },
        },
    };

    private static IEnumerable<Wire.SandboxStreamEvent> Fail(StatusCode code, params Wire.SandboxStreamEvent[] before)
    {
        foreach (var e in before)
        {
            yield return e;
        }

        throw new RpcException(new Status(code, "stream failure"));
    }

    private static async Task<List<WatchEvent>> DrainAsync(IAsyncEnumerable<WatchEvent> source, FakeTimeProvider? time = null)
    {
        var task = Task.Run(async () =>
        {
            var all = new List<WatchEvent>();
            await foreach (var e in source)
            {
                all.Add(e);
            }

            return all;
        });
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            time?.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task;
    }

    [Test]
    public async Task Watch_maps_every_payload_type_and_request_options()
    {
        Wire.WatchSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", r =>
        {
            seen = r;
            return new[]
            {
                StatusFrame(Wire.SandboxPhase.Ready),
                LogEvent("c1", "hello"),
                PlatformEventFrame("c2"),
                new Wire.SandboxStreamEvent { Warning = new Wire.SandboxStreamWarning { Message = "dropped 3" } },
                new Wire.SandboxStreamEvent { DraftPolicyUpdate = new Wire.DraftPolicyUpdate { DraftVersion = 4, NewChunks = 1, TotalPending = 2, Summary = "s" } },
                new Wire.SandboxStreamEvent(),
                new Wire.SandboxStreamEvent { Cursor = "c3", Event = new Wire.PlatformEvent { Message = "no time" } },
            };
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var events = await DrainAsync(client.Watch.WatchAsync("sb", new WatchOptions
        {
            FollowLogs = true,
            FollowEvents = true,
            StopOnTerminal = true,
            LogTailLines = 20,
            EventTail = 5,
            Since = DateTimeOffset.UnixEpoch.AddDays(1),
            LogSources = ["gateway", "sandbox"],
            LogMinLevel = "WARN",
            ResumeAfterCursor = "start",
        }));

        await Assert.That(seen!.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.FollowStatus).IsTrue();
        await Assert.That(seen.FollowLogs).IsTrue();
        await Assert.That(seen.FollowEvents).IsTrue();
        await Assert.That(seen.StopOnTerminal).IsTrue();
        await Assert.That(seen.LogTailLines).IsEqualTo(20u);
        await Assert.That(seen.EventTail).IsEqualTo(5u);
        await Assert.That(seen.SinceTime.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(seen.LogSources).IsEquivalentTo(new[] { "gateway", "sandbox" });
        await Assert.That(seen.LogMinLevel).IsEqualTo("WARN");
        await Assert.That(seen.ResumeAfterCursor).IsEqualTo("start");

        await Assert.That(events.Count).IsEqualTo(6);
        await Assert.That(((SandboxUpdated)events[0]).Sandbox.Phase).IsEqualTo(SandboxPhase.Ready);
        var log = (LogReceived)events[1];
        await Assert.That(log.Cursor).IsEqualTo("c1");
        await Assert.That(log.Line).IsEqualTo(log.Line with { Fields = log.Line.Fields });
        await Assert.That(log.Line.Message).IsEqualTo("hello");
        await Assert.That(log.Line.Level).IsEqualTo("INFO");
        await Assert.That(log.Line.Fields["k"]).IsEqualTo("v");
        await Assert.That(log.Line.Time).IsEqualTo(DateTimeOffset.UnixEpoch);
        var pe = (PlatformEventReceived)events[2];
        await Assert.That(pe.Cursor).IsEqualTo("c2");
        await Assert.That(pe.Event.Reason).IsEqualTo("Started");
        await Assert.That(pe.Event.Time).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(5));
        await Assert.That(pe.Event.Metadata["a"]).IsEqualTo("b");
        var warning = (WatchWarning)events[3];
        await Assert.That(warning.Message).IsEqualTo("dropped 3");
        await Assert.That(warning.Cursor).IsEqualTo("");
        var draft = (DraftPolicyUpdated)events[4];
        await Assert.That(draft.Update).IsEqualTo(new DraftPolicyUpdate(4, 1, 2, "s"));
        await Assert.That(((PlatformEventReceived)events[5]).Event.Time).IsNull();
    }

    [Test]
    public async Task WatchLogs_never_follows_status_and_filters_status_and_draft_events()
    {
        Wire.WatchSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", r =>
        {
            seen = r;
            return new[]
            {
                StatusFrame(Wire.SandboxPhase.Ready),
                LogEvent("c1"),
                new Wire.SandboxStreamEvent { DraftPolicyUpdate = new Wire.DraftPolicyUpdate() },
                new Wire.SandboxStreamEvent { Warning = new Wire.SandboxStreamWarning { Message = "w" } },
            };
        });
        using var client = ClientTests.NewClient(invoker);

        var events = await DrainAsync(client.Watch.WatchLogsAsync("sb", new WatchOptions { FollowLogs = true, FollowStatus = true }));

        await Assert.That(seen!.FollowStatus).IsFalse();
        await Assert.That(events.Select(e => e.GetType())).IsEquivalentTo(new[] { typeof(LogReceived), typeof(WatchWarning) });
    }

    [Test]
    public async Task WatchLogs_uses_defaults_when_options_omitted()
    {
        Wire.WatchSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", r =>
        {
            seen = r;
            return Array.Empty<Wire.SandboxStreamEvent>();
        });
        using var client = ClientTests.NewClient(invoker);

        await DrainAsync(client.Watch.WatchLogsAsync("sb"));

        await Assert.That(seen!.FollowLogs).IsFalse();
        await Assert.That(seen.ResumeAfterCursor).IsEqualTo("");
        await Assert.That(seen.LogMinLevel).IsEqualTo("");
        await Assert.That(seen.SinceTime).IsNull();
    }

    [Test]
    public async Task Reconnects_after_unavailable_and_resumes_from_last_cursor()
    {
        var time = new FakeTimeProvider();
        var requests = new List<Wire.WatchSandboxRequest>();
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", r =>
        {
            requests.Add(r.Clone());
            return requests.Count switch
            {
                1 => Fail(StatusCode.Unavailable, LogEvent("c1"), StatusFrame(Wire.SandboxPhase.Ready), LogEvent("c3")),
                2 => Fail(StatusCode.Unavailable),
                _ => [LogEvent("c4")],
            };
        });
        using var client = ClientTests.NewClient(invoker, time: time);

        var events = await DrainAsync(client.Watch.WatchAsync("sb", new WatchOptions { FollowLogs = true }), time);

        await Assert.That(requests.Select(r => r.ResumeAfterCursor)).IsEquivalentTo(new[] { "", "c3", "c3" });
        await Assert.That(events.OfType<LogReceived>().Select(e => e.Cursor)).IsEquivalentTo(new[] { "c1", "c3", "c4" });
    }

    [Test]
    public async Task Cursor_never_moves_backwards()
    {
        var time = new FakeTimeProvider();
        var requests = new List<Wire.WatchSandboxRequest>();
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", r =>
        {
            requests.Add(r.Clone());
            return requests.Count == 1
                ? Fail(StatusCode.Unavailable, LogEvent("c9"), PlatformEventFrame("c2"))
                : [];
        });
        using var client = ClientTests.NewClient(invoker, time: time);

        await DrainAsync(client.Watch.WatchAsync("sb"), time);

        await Assert.That(requests[1].ResumeAfterCursor).IsEqualTo("c9");
    }

    [Test]
    public async Task Gives_up_after_max_reconnect_attempts()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>(
            "WatchSandbox", _ => Fail(StatusCode.Unavailable));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = DrainAsync(client.Watch.WatchAsync("sb", new WatchOptions { MaxReconnectAttempts = 2 }), time);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(invoker.CountOf("WatchSandbox")).IsEqualTo(3);
    }

    [Test]
    public async Task Backoff_doubles_up_to_the_cap_and_resets_after_an_event()
    {
        var time = new FakeTimeProvider();
        var attempt = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", _ =>
            ++attempt switch
            {
                <= 6 => Fail(StatusCode.Unavailable),
                7 => Fail(StatusCode.Unavailable, LogEvent("c1")),
                _ => [],
            });
        using var client = ClientTests.NewClient(invoker, time: time);

        var events = await DrainAsync(client.Watch.WatchAsync("sb", new WatchOptions { FollowLogs = true }), time);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(attempt).IsEqualTo(8);
    }

    [Test]
    [Arguments(StatusCode.OutOfRange, OpenShellErrorKind.InvalidArgument)]
    [Arguments(StatusCode.NotFound, OpenShellErrorKind.NotFound)]
    [Arguments(StatusCode.PermissionDenied, OpenShellErrorKind.PermissionDenied)]
    public async Task Other_errors_are_terminal_without_reconnect(StatusCode code, OpenShellErrorKind kind)
    {
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", _ => Fail(code));
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await DrainAsync(client.Watch.WatchAsync("sb"))).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(kind);
        await Assert.That(ex.GrpcStatusCode).IsEqualTo(code);
        await Assert.That(invoker.CountOf("WatchSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_during_backoff_stops_the_watch()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>("WatchSandbox", _ => Fail(StatusCode.Unavailable));
        using var client = ClientTests.NewClient(invoker, time: time);
        using var cts = new CancellationTokenSource();

        var task = Task.Run(async () =>
        {
            await foreach (var _ in client.Watch.WatchAsync("sb", cancellationToken: cts.Token))
            {
            }
        });
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task GetLogs_sends_filters_and_maps_lines()
    {
        Wire.GetSandboxLogsRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxLogsRequest, Wire.GetSandboxLogsResponse>("GetSandboxLogs", r =>
        {
            seen = r;
            var reply = new Wire.GetSandboxLogsResponse { BufferTotal = 50 };
            reply.Logs.Add(LogEvent("x", "one").Log);
            reply.Logs.Add(new Wire.SandboxLogLine { Message = "two" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var result = await client.Watch.GetLogsAsync("sb", new LogOptions
        {
            Lines = 10,
            Since = DateTimeOffset.UnixEpoch.AddDays(2),
            Sources = ["gateway"],
            MinLevel = "ERROR",
        });

        await Assert.That(seen!.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Lines).IsEqualTo(10u);
        await Assert.That(seen.SinceTime.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(2));
        await Assert.That(seen.Sources).IsEquivalentTo(new[] { "gateway" });
        await Assert.That(seen.MinLevel).IsEqualTo("ERROR");
        await Assert.That(result.BufferTotal).IsEqualTo(50u);
        await Assert.That(result.Lines.Select(l => l.Message)).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(result.Lines[1].Time).IsNull();
    }

    [Test]
    public async Task GetLogs_defaults_and_errors()
    {
        Wire.GetSandboxLogsRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxLogsRequest, Wire.GetSandboxLogsResponse>("GetSandboxLogs", r =>
        {
            seen = r;
            return new Wire.GetSandboxLogsResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Watch.GetLogsAsync("sb");

        await Assert.That(seen!.Lines).IsEqualTo(0u);
        await Assert.That(seen.SinceTime).IsNull();
        await Assert.That(seen.MinLevel).IsEqualTo("");
        await Assert.That(result.Lines.Count).IsEqualTo(0);

        var failing = new FakeCallInvoker().Throws<Wire.GetSandboxLogsRequest, Wire.GetSandboxLogsResponse>("GetSandboxLogs", StatusCode.NotFound, "no sandbox");
        using var failClient = ClientTests.NewClient(failing);
        var ex = await Assert.That(async () => await failClient.Watch.GetLogsAsync("sb")).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(() => client.Watch.WatchAsync("")).Throws<ArgumentException>();
        await Assert.That(() => client.Watch.WatchLogsAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Watch.GetLogsAsync("")).Throws<ArgumentException>();
    }
}
