// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;

namespace OpenShell.Tests;

public class FakeWatchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task<T> NextAsync<T>(IAsyncEnumerator<T> events)
    {
        return await events.MoveNextAsync().AsTask().WaitAsync(Patience)
            ? events.Current
            : throw new InvalidOperationException("the stream ended");
    }

    private static async Task<bool> EndedAsync<T>(IAsyncEnumerator<T> events) =>
        !await events.MoveNextAsync().AsTask().WaitAsync(Patience);

    [Test]
    public async Task Status_watch_starts_with_a_snapshot_and_follows_changes_until_terminal()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch.WatchAsync("a", new WatchOptions { StopOnTerminal = true }).GetAsyncEnumerator();

        var snapshot = (SandboxUpdated)await NextAsync(events);
        var errorRead = NextAsync(events);
        client.Gateway.SetPhase("a", SandboxPhase.Error);
        var error = (SandboxUpdated)await errorRead;

        await Assert.That(snapshot.Sandbox.Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That(error.Sandbox.Phase).IsEqualTo(SandboxPhase.Error);
        await Assert.That(error.Cursor.Length).IsGreaterThan(0);
        await Assert.That(await EndedAsync(events)).IsTrue();
    }

    [Test]
    public async Task Watching_an_already_terminal_sandbox_ends_after_the_snapshot()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.SetPhase("a", SandboxPhase.Completed, exitCode: 0);

        var events = await client.Watch.WatchAsync("a", new WatchOptions { StopOnTerminal = true }).ToListAsync();

        await Assert.That(events.OfType<SandboxUpdated>().Last().Sandbox.Phase).IsEqualTo(SandboxPhase.Completed);
    }

    [Test]
    public async Task Log_and_event_streams_follow_and_filter()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch
            .WatchAsync("a", new WatchOptions { FollowStatus = false, FollowLogs = true, FollowEvents = true, LogSources = ["sandbox"], LogMinLevel = "WARN" })
            .GetAsyncEnumerator();

        // A watch only sees what happens after it starts, so begin reading before emitting.
        var firstRead = NextAsync(events);
        client.Gateway.EmitLog("a", "dropped: too quiet", "INFO");
        client.Gateway.EmitLog("a", "dropped: wrong source", "ERROR", source: "gateway");
        client.Gateway.EmitLog("a", "kept", "WARNING");
        client.Gateway.EmitEvent("a", "Pulled", "image pulled", "Normal");

        var log = (LogReceived)await firstRead;
        var platform = (PlatformEventReceived)await NextAsync(events);

        await Assert.That(log.Line.Message).IsEqualTo("kept");
        await Assert.That(log.Line.SandboxId.Length).IsGreaterThan(0);
        await Assert.That(platform.Event.Reason).IsEqualTo("Pulled");
    }

    [Test]
    public async Task WatchLogs_forces_log_only_streaming()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch.WatchLogsAsync("a", new WatchOptions { FollowStatus = true }).GetAsyncEnumerator();

        var firstRead = NextAsync(events);
        client.Gateway.SetPhase("a", SandboxPhase.Error);
        client.Gateway.EmitLog("a", "line");
        var first = await firstRead;

        await Assert.That(first).IsTypeOf<LogReceived>();
        await using var defaults = client.Watch.WatchLogsAsync("a").GetAsyncEnumerator();
        var defaultRead = NextAsync(defaults);
        client.Gateway.EmitLog("a", "second");
        await Assert.That(await defaultRead).IsTypeOf<LogReceived>();
    }

    [Test]
    public async Task Tails_replay_recent_logs_and_events_in_order()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.EmitLog("a", "one");
        client.Gateway.EmitEvent("a", "R1", "first");
        client.Gateway.EmitLog("a", "two");
        client.Gateway.EmitEvent("a", "R2", "second");

        await using var events = client.Watch
            .WatchAsync("a", new WatchOptions { FollowStatus = false, FollowLogs = true, FollowEvents = true, LogTailLines = 1, EventTail = 2 })
            .GetAsyncEnumerator();
        var first = await NextAsync(events);
        var second = await NextAsync(events);
        var third = await NextAsync(events);

        await Assert.That(first).IsTypeOf<PlatformEventReceived>();
        await Assert.That(((LogReceived)second).Line.Message).IsEqualTo("two");
        await Assert.That(((PlatformEventReceived)third).Event.Reason).IsEqualTo("R2");
    }

    [Test]
    public async Task Since_filters_old_records_out_of_tails()
    {
        var time = new FakeTimeProvider();
        using var client = await FakeTestHelpers.ClientWithSandboxAsync(new FakeGatewayOptions { TimeProvider = time });
        client.Gateway.EmitLog("a", "old");
        time.Advance(TimeSpan.FromMinutes(10));
        client.Gateway.EmitLog("a", "new");

        await using var events = client.Watch
            .WatchAsync("a", new WatchOptions { FollowStatus = false, FollowLogs = true, LogTailLines = 10, Since = time.GetUtcNow() - TimeSpan.FromMinutes(1) })
            .GetAsyncEnumerator();

        await Assert.That(((LogReceived)await NextAsync(events)).Line.Message).IsEqualTo("new");
    }

    [Test]
    public async Task Resuming_from_a_cursor_delivers_only_later_events()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        var cursor = client.Gateway.EmitLog("a", "seen");
        client.Gateway.EmitLog("a", "unseen");

        await using var events = client.Watch
            .WatchAsync("a", new WatchOptions { FollowStatus = false, FollowLogs = true, ResumeAfterCursor = cursor })
            .GetAsyncEnumerator();

        await Assert.That(((LogReceived)await NextAsync(events)).Line.Message).IsEqualTo("unseen");
    }

    [Test]
    public async Task Resume_cursors_and_sandboxes_are_validated()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var bad = await Assert.That(async () => await client.Watch.WatchAsync("a", new WatchOptions { ResumeAfterCursor = "xyz" }).ToListAsync()).Throws<OpenShellException>();
        var missing = await Assert.That(async () => await client.Watch.WatchAsync("nope").ToListAsync()).Throws<OpenShellException>();
        var missingResume = await Assert.That(async () => await client.Watch.WatchAsync("nope", new WatchOptions { ResumeAfterCursor = "1" }).ToListAsync()).Throws<OpenShellException>();

        await Assert.That(bad!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(missingResume!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Watch.WatchAsync(" ").ToListAsync()).Throws<ArgumentException>();
        await Assert.That(async () => await client.Watch.WatchLogsAsync(" ").ToListAsync()).Throws<ArgumentException>();
    }

    [Test]
    public async Task Draft_updates_reach_status_followers()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch.WatchAsync("a", new WatchOptions { FollowStatus = true }).GetAsyncEnumerator();
        await NextAsync(events);

        client.Gateway.ProposeChunk("a", "web", FakePolicyData.Rule("web"));
        var update = (DraftPolicyUpdated)await NextAsync(events);

        await Assert.That(update.Update.NewChunks).IsEqualTo(1u);
        await Assert.That(update.Update.TotalPending).IsEqualTo(1u);
        await Assert.That(update.Cursor).IsEqualTo("");
    }

    [Test]
    public async Task The_stream_ends_when_the_sandbox_is_deleted()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch.WatchAsync("a").GetAsyncEnumerator();
        await NextAsync(events);

        await client.Sandboxes.DeleteAsync("a");

        await Assert.That(await EndedAsync(events)).IsTrue();
    }

    [Test]
    public async Task Cancelling_ends_the_stream_with_an_operation_canceled()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        using var cts = new CancellationTokenSource();
        await using var events = client.Watch.WatchAsync("a", cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
        await NextAsync(events);

        var pending = events.MoveNextAsync().AsTask();
        await cts.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Timed_transitions_wake_blocked_watchers()
    {
        var time = new FakeTimeProvider();
        using var client = FakeCoreTests.New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromSeconds(30) });
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        await using var events = client.Watch.WatchAsync("a").GetAsyncEnumerator();
        var snapshot = (SandboxUpdated)await NextAsync(events);

        var pending = NextAsync(events);
        time.Advance(TimeSpan.FromSeconds(30));
        var ready = (SandboxUpdated)await pending;

        await Assert.That(snapshot.Sandbox.Phase).IsEqualTo(SandboxPhase.Provisioning);
        await Assert.That(ready.Sandbox.Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task Disposing_the_gateway_fails_a_live_watch()
    {
        var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var events = client.Watch.WatchAsync("a").GetAsyncEnumerator();
        await NextAsync(events);

        var pending = events.MoveNextAsync().AsTask();
        client.Dispose();

        await Assert.That(async () => await pending.WaitAsync(Patience)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task GetLogs_returns_the_filtered_tail_and_the_buffer_size()
    {
        var time = new FakeTimeProvider();
        using var client = await FakeTestHelpers.ClientWithSandboxAsync(new FakeGatewayOptions { TimeProvider = time });
        client.Gateway.EmitLog("a", "debug-line", "DEBUG");
        time.Advance(TimeSpan.FromMinutes(5));
        client.Gateway.EmitLog("a", "info-line", "INFO");
        client.Gateway.EmitLog("a", "warn-line", "WARN");
        client.Gateway.EmitLog("a", "odd-line", "VERBOSE", source: "gateway");
        client.Gateway.EmitLog("a", "error-line", "ERROR");
        client.Gateway.EmitLog("a", "trace-line", "TRACE");

        var all = await client.Watch.GetLogsAsync("a");
        var lastTwo = await client.Watch.GetLogsAsync("a", new LogOptions { Lines = 2 });
        var warnings = await client.Watch.GetLogsAsync("a", new LogOptions { MinLevel = "warn" });
        var recent = await client.Watch.GetLogsAsync("a", new LogOptions { Since = time.GetUtcNow() - TimeSpan.FromMinutes(1) });
        var gatewayOnly = await client.Watch.GetLogsAsync("a", new LogOptions { Sources = ["GATEWAY"] });

        await Assert.That(all.Lines.Count).IsEqualTo(6);
        await Assert.That(all.BufferTotal).IsEqualTo(6u);
        await Assert.That(lastTwo.Lines.Select(l => l.Message)).IsEquivalentTo(new[] { "error-line", "trace-line" });
        await Assert.That(warnings.Lines.Select(l => l.Message)).IsEquivalentTo(new[] { "warn-line", "error-line" });
        await Assert.That(recent.Lines.Count).IsEqualTo(5);
        await Assert.That(gatewayOnly.Lines.Single().Message).IsEqualTo("odd-line");
    }

    [Test]
    public async Task GetLogs_rejects_unknown_minimum_levels_and_missing_sandboxes()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.EmitLog("a", "x");

        var level = await Assert.That(async () => await client.Watch.GetLogsAsync("a", new LogOptions { MinLevel = "LOUD" })).Throws<OpenShellException>();
        var missing = await Assert.That(async () => await client.Watch.GetLogsAsync("nope")).Throws<OpenShellException>();

        await Assert.That(level!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Watch.GetLogsAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task The_record_buffer_is_bounded()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        for (var i = 0; i < 5100; i++)
        {
            client.Gateway.EmitLog("a", "x");
        }

        var logs = await client.Watch.GetLogsAsync("a", new LogOptions { Lines = 3000 });

        await Assert.That(logs.BufferTotal).IsLessThanOrEqualTo(5000u);
    }
}
