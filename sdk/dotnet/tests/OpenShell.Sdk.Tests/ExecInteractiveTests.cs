// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Google.Protobuf;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

internal static class Interactive
{
    public static Wire.ExecSandboxEvent Out(string text) => new() { Stdout = new Wire.ExecSandboxStdout { Data = ByteString.CopyFromUtf8(text) } };

    public static Wire.ExecSandboxEvent Err(string text) => new() { Stderr = new Wire.ExecSandboxStderr { Data = ByteString.CopyFromUtf8(text) } };

    public static Wire.ExecSandboxEvent Exit(int code) => new() { Exit = new Wire.ExecSandboxExit { ExitCode = code } };

    public static FakeCallInvoker With(Func<DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>, Task> handler) =>
        new FakeCallInvoker().Duplex("ExecSandboxInteractive", handler);

    public static DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> Channel(FakeCallInvoker invoker) =>
        (DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>)invoker.DuplexCalls.Single(c => c.Method == "ExecSandboxInteractive").Channel;

    /// <summary>Consumes input until the client completes or disposes, never reporting an exit.</summary>
    public static async Task Hang(DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> ch)
    {
        await foreach (var _ in ch.ReadAllAsync())
        {
        }
    }

    /// <summary>Uppercases stdin, reports resizes on stdout, exits 0 at EOF.</summary>
    public static async Task Shell(DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> ch)
    {
        await foreach (var frame in ch.ReadAllAsync())
        {
            switch (frame.PayloadCase)
            {
                case Wire.ExecSandboxInput.PayloadOneofCase.Stdin:
                    ch.Send(Out(frame.Stdin.ToStringUtf8().ToUpperInvariant()));
                    break;
                case Wire.ExecSandboxInput.PayloadOneofCase.Resize:
                    ch.Send(Err($"{frame.Resize.Cols}x{frame.Resize.Rows}"));
                    break;
            }
        }

        ch.Send(Exit(0));
    }

    public static async Task<List<ExecEvent>> CollectAsync(ExecInteractiveSession session)
    {
        var events = new List<ExecEvent>();
        await foreach (var e in session.ReadEventsAsync())
        {
            events.Add(e);
        }

        return events;
    }
}

public class ExecInteractiveTests
{
    [Test]
    public async Task Start_frame_carries_command_tty_and_size()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker, "ws");

        await using var session = await client.Exec.InteractiveAsync(
            "sb",
            ["bash"],
            120,
            40,
            new ExecOptions { WorkingDirectory = "/w", Environment = new Dictionary<string, string> { ["A"] = "1" } });

        var start = (await WaitForFrameAsync(invoker)).Start;
        await Assert.That(start.Sandbox).IsEqualTo("sb");
        await Assert.That(start.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(start.Command).IsEquivalentTo(new[] { "bash" });
        await Assert.That(start.Tty).IsTrue();
        await Assert.That(start.Cols).IsEqualTo(120u);
        await Assert.That(start.Rows).IsEqualTo(40u);
        await Assert.That(start.Workdir).IsEqualTo("/w");
        await Assert.That(start.Environment["A"]).IsEqualTo("1");
    }

    [Test]
    public async Task Tty_can_be_disabled()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker);

        await using var session = await client.Exec.InteractiveAsync("sb", ["cat"], 0, 0, allocateTty: false);

        await Assert.That((await WaitForFrameAsync(invoker)).Start.Tty).IsFalse();
    }

    [Test]
    public async Task Stdin_resize_output_and_exit_flow_end_to_end()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);

        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("hello"));
        await session.ResizeAsync(100, 30);
        await session.CompleteStdinAsync();
        await session.CompleteStdinAsync();
        var events = await Interactive.CollectAsync(session);

        await Assert.That(events.OfType<ExecOutput>().Where(o => !o.IsStdErr).Select(o => o.Text).Single()).IsEqualTo("HELLO");
        await Assert.That(events.OfType<ExecOutput>().Where(o => o.IsStdErr).Select(o => o.Text).Single()).IsEqualTo("100x30");
        await Assert.That(events[^1]).IsEqualTo(new ExecExit(0));
        await Assert.That(await session.ExitCode).IsEqualTo(0);
        await Assert.That(await session.WaitForExitAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task WaitForExit_drains_output_when_nobody_reads()
    {
        var invoker = Interactive.With(async ch =>
        {
            await ch.ReadAsync();
            ch.Send(Interactive.Out("ignored"));
            ch.Send(new Wire.ExecSandboxEvent());
            ch.Send(Interactive.Exit(7));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        await Assert.That(await session.WaitForExitAsync()).IsEqualTo(7);
    }

    [Test]
    public async Task WaitForExit_after_a_reader_started_waits_for_its_result()
    {
        var invoker = Interactive.With(async ch =>
        {
            await foreach (var _ in ch.ReadAllAsync())
            {
            }

            ch.Send(Interactive.Exit(4));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        var reader = Interactive.CollectAsync(session);

        var wait = session.WaitForExitAsync().AsTask();
        await session.CompleteStdinAsync();
        await reader;

        await Assert.That(await wait).IsEqualTo(4);
    }

    [Test]
    public async Task Events_can_only_be_enumerated_once()
    {
        var invoker = Interactive.With(async ch =>
        {
            await ch.ReadAsync();
            ch.Send(Interactive.Exit(0));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        await Interactive.CollectAsync(session);

        await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_stream_that_ends_without_exit_is_an_error()
    {
        var invoker = Interactive.With(async ch =>
        {
            await ch.ReadAsync();
            ch.Send(Interactive.Out("partial"));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        var ex = await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(async () => await session.ExitCode).Throws<OpenShellException>();
    }

    [Test]
    public async Task A_gateway_failure_is_mapped_and_fails_the_exit_code()
    {
        var invoker = Interactive.With(async ch =>
        {
            await ch.ReadAsync();
            throw new RpcException(new Status(StatusCode.NotFound, "sandbox gone"));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        var ex = await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(async () => await session.WaitForExitAsync()).Throws<OpenShellException>();
    }

    [Test]
    public async Task Cancelling_the_enumeration_cancels_the_exit_code()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        using var cts = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            await foreach (var _ in session.ReadEventsAsync(cts.Token))
            {
            }
        });
        await cts.CancelAsync();

        await Assert.That(async () => await reader).Throws<OperationCanceledException>();
        await Assert.That(async () => await session.ExitCode).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Large_stdin_is_split_into_bounded_frames()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        var payload = new byte[600 * 1024];
        Array.Fill(payload, (byte)'a');

        await session.WriteStdinAsync(payload);
        await session.CompleteStdinAsync();
        await Interactive.CollectAsync(session);

        var frames = Interactive.Channel(invoker).Received.Where(f => f.PayloadCase == Wire.ExecSandboxInput.PayloadOneofCase.Stdin).ToArray();
        await Assert.That(frames.Length).IsEqualTo(3);
        await Assert.That(frames.Sum(f => f.Stdin.Length)).IsEqualTo(payload.Length);
    }

    [Test]
    public async Task Input_after_completion_or_dispose_is_rejected()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker);
        var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        await session.CompleteStdinAsync();
        await Assert.That(async () => await session.WriteStdinAsync(new byte[1])).Throws<InvalidOperationException>();
        await Assert.That(async () => await session.ResizeAsync(1, 1)).Throws<InvalidOperationException>();

        await session.DisposeAsync();
        await session.DisposeAsync();
        await Assert.That(async () => await session.WriteStdinAsync(new byte[1])).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Disposing_before_exit_faults_the_exit_code_and_cancels_the_call()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        var exit = session.ExitCode;

        await session.DisposeAsync();

        var ex = await Assert.That(async () => await exit).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Cancelled);
        await Assert.That(Interactive.Channel(invoker).ClientDisposed).IsTrue();
    }

    [Test]
    public async Task CancelAsync_stops_the_command()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        await session.CancelAsync();

        await Assert.That(Interactive.Channel(invoker).ClientDisposed).IsTrue();
    }

    [Test]
    public async Task Write_failures_are_mapped()
    {
        var invoker = Interactive.With(Interactive.Shell);
        invoker.DuplexFailWritesAfter = 1; // Only the start frame gets through.
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);

        var ex = await Assert.That(async () => await session.WriteStdinAsync(new byte[] { 1 })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Start_failures_are_mapped_and_cancellation_propagates()
    {
        var failing = Interactive.With(Interactive.Shell);
        failing.DuplexFailWritesAfter = 0;
        using var failingClient = ClientTests.NewClient(failing);
        var ex = await Assert.That(async () => await failingClient.Exec.InteractiveAsync("sb", ["x"], 1, 1)).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);

        using var client = ClientTests.NewClient(Interactive.With(Interactive.Shell));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.That(async () => await client.Exec.InteractiveAsync("sb", ["x"], 1, 1, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task A_failing_stdin_completion_is_swallowed()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        Interactive.Channel(invoker).FailComplete = true;

        await session.CompleteStdinAsync();

        await Assert.That(Interactive.Channel(invoker).Received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_exceptions_from_the_transport_cancel_the_exit_code()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        Interactive.Channel(invoker).MoveNextFault = new OperationCanceledException();

        await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OperationCanceledException>();
        await Assert.That(async () => await session.ExitCode).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task A_call_disposed_underneath_the_reader_reads_as_cancellation()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        Interactive.Channel(invoker).MoveNextFault = new ObjectDisposedException("call");

        await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OperationCanceledException>();
        await Assert.That(async () => await session.ExitCode).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Reading_after_disposal_is_a_cancellation()
    {
        var invoker = Interactive.With(Interactive.Hang);
        using var client = ClientTests.NewClient(invoker);
        var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        await session.DisposeAsync();

        await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Arguments_are_validated()
    {
        using var client = ClientTests.NewClient(Interactive.With(Interactive.Shell));

        await Assert.That(async () => await client.Exec.InteractiveAsync("", ["x"], 1, 1)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Exec.InteractiveAsync("sb", null!, 1, 1)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Exec.InteractiveAsync("sb", [], 1, 1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task A_disposed_client_cannot_start_sessions()
    {
        var client = ClientTests.NewClient(Interactive.With(Interactive.Shell));
        client.Dispose();

        await Assert.That(async () => await client.Exec.InteractiveAsync("sb", ["x"], 1, 1)).Throws<ObjectDisposedException>();
    }

    private static async Task<Wire.ExecSandboxInput> WaitForFrameAsync(FakeCallInvoker invoker)
    {
        var channel = Interactive.Channel(invoker);
        for (var i = 0; i < 200 && channel.Received.Count == 0; i++)
        {
            await Task.Delay(5);
        }

        return channel.Received[0];
    }
}
