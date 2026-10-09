// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Google.Protobuf;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

internal static class Relay
{
    public static FakeCallInvoker WithSandbox(FakeCallInvoker? invoker = null) =>
        (invoker ?? new FakeCallInvoker()).Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => ClientTests.SandboxReply("sb"));

    public static Wire.TcpForwardFrame Data(string text) => new() { Data = ByteString.CopyFromUtf8(text) };

    public static Wire.TcpForwardFrame Data(byte[] bytes) => new() { Data = ByteString.CopyFrom(bytes) };

    /// <summary>Echoes every data frame back until the client completes.</summary>
    public static async Task Echo(DuplexChannel<Wire.TcpForwardFrame, Wire.TcpForwardFrame> ch)
    {
        await foreach (var frame in ch.ReadAllAsync())
        {
            if (frame.PayloadCase == Wire.TcpForwardFrame.PayloadOneofCase.Data)
            {
                ch.Send(new Wire.TcpForwardFrame { Data = frame.Data });
            }
        }
    }

    public static FakeCallInvoker EchoInvoker() =>
        WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Echo);

    public static DuplexChannel<Wire.TcpForwardFrame, Wire.TcpForwardFrame> Channel(FakeCallInvoker invoker, int index = 0) =>
        (DuplexChannel<Wire.TcpForwardFrame, Wire.TcpForwardFrame>)invoker.DuplexCalls.Where(c => c.Method == "ForwardTcp").ElementAt(index).Channel;

    public static async Task<string> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return Encoding.UTF8.GetString(buffer);
    }
}

public class ForwardStreamTests
{
    [Test]
    public async Task Forward_sends_tcp_init_and_echoes_data()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker, "ws");

        await using var stream = await client.Tcp.ForwardAsync("sb", 8080, new ForwardOptions { ServiceId = "svc" });
        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"));

        await Assert.That(await Relay.ReadExactlyAsync(stream, 5)).IsEqualTo("hello");
        var init = Relay.Channel(invoker).Received[0].Init;
        await Assert.That(init.Sandbox).IsEqualTo("sb");
        await Assert.That(init.Workspace).IsEqualTo("ws");
        await Assert.That(init.ServiceId).IsEqualTo("svc");
        await Assert.That(init.Tcp.Host).IsEqualTo("127.0.0.1");
        await Assert.That(init.Tcp.Port).IsEqualTo(8080u);
        await Assert.That(stream.CanRead && stream.CanWrite && !stream.CanSeek).IsTrue();
    }

    [Test]
    public async Task Small_reads_drain_a_larger_frame_in_pieces()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", async ch =>
        {
            ch.Send(Relay.Data("abcdef"));
            await ch.ReadAsync();
        });
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        var first = new byte[4];
        var n1 = await stream.ReadAsync(first, 0, 4, CancellationToken.None);
        var second = new byte[4];
        var n2 = await stream.ReadAsync(second.AsMemory());

        await Assert.That(n1).IsEqualTo(4);
        await Assert.That(Encoding.UTF8.GetString(first)).IsEqualTo("abcd");
        await Assert.That(n2).IsEqualTo(2);
        await Assert.That(Encoding.UTF8.GetString(second, 0, 2)).IsEqualTo("ef");
    }

    [Test]
    public async Task Server_non_data_frames_are_skipped_and_end_of_stream_reads_zero()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", ch =>
        {
            ch.Send(new Wire.TcpForwardFrame { Init = new Wire.TcpForwardInit() });
            ch.Send(Relay.Data("x"));
            return Task.CompletedTask;
        });
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        var buffer = new byte[8];
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(1);
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(0);
        await Assert.That(await stream.ReadAsync(Memory<byte>.Empty)).IsEqualTo(0);
    }

    [Test]
    public async Task Large_writes_are_split_into_bounded_frames()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);
        var payload = new byte[600 * 1024];
        Random.Shared.NextBytes(payload);

        await stream.WriteAsync(payload, 0, payload.Length, CancellationToken.None);
        var echoed = new byte[payload.Length];
        await stream.ReadExactlyAsync(echoed);

        var frames = Relay.Channel(invoker).Received.Where(f => f.PayloadCase == Wire.TcpForwardFrame.PayloadOneofCase.Data).ToArray();
        await Assert.That(frames.Length).IsEqualTo(3);
        await Assert.That(frames.All(f => f.Data.Length <= 256 * 1024)).IsTrue();
        await Assert.That(echoed.AsSpan().SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task CompleteWrite_half_closes_while_reads_continue()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", async ch =>
        {
            await foreach (var _ in ch.ReadAllAsync())
            {
            }

            ch.Send(Relay.Data("bye"));
        });
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        await stream.CompleteWriteAsync();
        await stream.CompleteWriteAsync();

        await Assert.That(await Relay.ReadExactlyAsync(stream, 3)).IsEqualTo("bye");
        await Assert.That(async () => await stream.WriteAsync(new byte[1])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_failing_write_completion_is_swallowed()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);
        Relay.Channel(invoker).FailComplete = true;

        await stream.CompleteWriteAsync();

        await Assert.That(async () => await stream.WriteAsync(new byte[1])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Server_failure_is_mapped_on_read()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>(
            "ForwardTcp", _ => throw new RpcException(new Status(StatusCode.Unavailable, "relay down")));
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        var ex = await Assert.That(async () => await stream.ReadAsync(new byte[4])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Read_honors_cancellation()
    {
        var invoker = Relay.WithSandbox().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", async ch =>
        {
            await foreach (var _ in ch.ReadAllAsync())
            {
            }
        });
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);
        using var cts = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[4], cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.That(async () => await read).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Write_failure_is_mapped()
    {
        var invoker = Relay.EchoInvoker();
        invoker.DuplexFailWritesAfter = 1; // The init frame succeeds, the first data frame fails.
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        var ex = await Assert.That(async () => await stream.WriteAsync(new byte[] { 1 })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Opening_with_a_failing_init_write_is_mapped()
    {
        var invoker = Relay.EchoInvoker();
        invoker.DuplexFailWritesAfter = 0;
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 1)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Opening_with_a_cancelled_token_propagates_cancellation()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 1, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Dispose_cancels_the_call_and_blocks_further_use()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        var stream = await client.Tcp.ForwardAsync("sb", 1);

        await stream.DisposeAsync();
        await stream.DisposeAsync();
        stream.Dispose();

        await Assert.That(Relay.Channel(invoker).ClientDisposed).IsTrue();
        await Assert.That(async () => await stream.ReadAsync(new byte[1])).Throws<ObjectDisposedException>();
        await Assert.That(async () => await stream.WriteAsync(new byte[1])).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Synchronous_dispose_closes_the_stream()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        var stream = await client.Tcp.ForwardAsync("sb", 1);

        stream.Dispose();

        await Assert.That(Relay.Channel(invoker).ClientDisposed).IsTrue();
    }

    [Test]
    public async Task Unsupported_synchronous_members_throw()
    {
        var invoker = Relay.EchoInvoker();
        using var client = ClientTests.NewClient(invoker);
        await using var stream = await client.Tcp.ForwardAsync("sb", 1);

        await Assert.That(() => stream.Read(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Write(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
        await Assert.That(() => stream.SetLength(0)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Length).Throws<NotSupportedException>();
        await Assert.That(() => stream.Position).Throws<NotSupportedException>();
        await Assert.That(() => stream.Position = 1).Throws<NotSupportedException>();
        stream.Flush();
        await stream.FlushAsync();
    }

    [Test]
    public async Task Forward_validates_arguments_and_sandbox_existence()
    {
        var missing = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "no such sandbox");
        using var client = ClientTests.NewClient(missing);

        await Assert.That(async () => await client.Tcp.ForwardAsync("", 80)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 65536)).Throws<ArgumentOutOfRangeException>();
        var ex = await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 80)).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(missing.CountOf("ForwardTcp")).IsEqualTo(0);
    }

    [Test]
    public async Task Disposed_client_rejects_new_streams()
    {
        var invoker = Relay.EchoInvoker();
        var client = ClientTests.NewClient(invoker);
        client.Dispose();

        await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 1)).Throws<ObjectDisposedException>();
    }
}
