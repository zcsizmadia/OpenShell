// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;
using Grpc.Core;
using OpenShell.Testing;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class FakeInternalsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static Wire.TcpForwardFrame Init(uint port = 80) => new()
    {
        Init = new Wire.TcpForwardInit
        {
            Sandbox = "a",
            Workspace = "default",
            Tcp = new Wire.TcpRelayTarget { Host = "127.0.0.1", Port = port },
        },
    };

    private static Wire.ExecSandboxInput Start() => new()
    {
        Start = new Wire.ExecSandboxRequest
        {
            Sandbox = "a",
            WorkspaceScope = new Openshell.Datamodel.V1.WorkspaceSelector { Workspace = "default" },
            Command = { "x" },
        },
    };

    [Test]
    public async Task The_channel_writer_supports_both_write_overloads_and_options()
    {
        var channel = Channel.CreateUnbounded<int>();
        var writer = new ChannelClientWriter<int>(channel.Writer) { WriteOptions = new WriteOptions(WriteFlags.NoCompress) };

        await writer.WriteAsync(1);
        await writer.WriteAsync(2, CancellationToken.None);
        await writer.CompleteAsync();
        var items = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync())
        {
            items.Add(item);
        }

        await Assert.That(items).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(writer.WriteOptions!.Flags).IsEqualTo(WriteFlags.NoCompress);
    }

    [Test]
    public async Task The_channel_reader_yields_items_then_ends_or_throws_the_completion_error()
    {
        var ok = Channel.CreateUnbounded<int>();
        ok.Writer.TryWrite(7);
        ok.Writer.TryComplete();
        var reader = new ChannelStreamReader<int>(ok.Reader);

        await Assert.That(await reader.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(reader.Current).IsEqualTo(7);
        await Assert.That(await reader.MoveNext(CancellationToken.None)).IsFalse();

        var failing = Channel.CreateUnbounded<int>();
        failing.Writer.TryComplete(new RpcException(new Status(StatusCode.Internal, "boom")));
        await Assert.That(async () => await new ChannelStreamReader<int>(failing.Reader).MoveNext(CancellationToken.None)).Throws<RpcException>();
    }

    [Test]
    public async Task The_remote_end_reads_in_pieces_and_skips_empty_chunks()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<byte[]>();
        await using var stream = new RemoteEndStream(input.Reader, output.Writer);
        input.Writer.TryWrite([1, 2, 3, 4]);
        input.Writer.TryWrite([]);
        input.Writer.TryWrite([5]);
        input.Writer.TryComplete();

        var buffer = new byte[3];
        var first = await stream.ReadAsync(buffer);
        var second = await stream.ReadAsync(buffer);
        var third = await stream.ReadAsync(buffer, 0, 3, CancellationToken.None);
        var end = await stream.ReadAsync(buffer);

        await Assert.That(new[] { first, second, third, end }).IsEquivalentTo(new[] { 3, 1, 1, 0 });
    }

    [Test]
    public async Task The_remote_end_writes_to_the_client_and_completes_when_disposed()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<byte[]>();
        var stream = new RemoteEndStream(input.Reader, output.Writer);

        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.WriteAsync(new byte[] { 9, 3, 4 }, 1, 2, CancellationToken.None);
        await stream.FlushAsync(CancellationToken.None);
        stream.Flush();
        await stream.DisposeAsync();
        var chunks = new List<byte[]>();
        await foreach (var chunk in output.Reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        await Assert.That(chunks.Count).IsEqualTo(2);
        await Assert.That(chunks[1]).IsEquivalentTo(new byte[] { 3, 4 });
    }

    [Test]
    public async Task The_remote_end_is_a_forward_only_async_stream()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<byte[]>();
        using var stream = new RemoteEndStream(input.Reader, output.Writer);

        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(stream.CanWrite).IsTrue();
        await Assert.That(stream.CanSeek).IsFalse();
        await Assert.That(() => stream.Length).Throws<NotSupportedException>();
        await Assert.That(() => stream.Position).Throws<NotSupportedException>();
        await Assert.That(() => stream.Position = 1).Throws<NotSupportedException>();
        await Assert.That(() => stream.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
        await Assert.That(() => stream.SetLength(1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Read(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Write(new byte[1], 0, 1)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Duplex_calls_report_success_and_no_trailers()
    {
        using var gateway = new FakeGateway();
        using var forward = ForwardServer.Start(gateway, CancellationToken.None);
        using var interactive = InteractiveServer.Start(gateway, CancellationToken.None);

        await Assert.That(forward.GetStatus().StatusCode).IsEqualTo(StatusCode.OK);
        await Assert.That(forward.GetTrailers().Count).IsEqualTo(0);
        await Assert.That(interactive.GetStatus().StatusCode).IsEqualTo(StatusCode.OK);
        await Assert.That(interactive.GetTrailers().Count).IsEqualTo(0);
        await Assert.That((await forward.ResponseHeadersAsync).Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_forward_call_closed_before_init_fails_with_an_internal_error()
    {
        using var gateway = new FakeGateway();
        using var call = ForwardServer.Start(gateway, CancellationToken.None);

        await call.RequestStream.CompleteAsync();
        var ex = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Patience)).Throws<RpcException>();

        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Internal);
    }

    [Test]
    public async Task An_interactive_call_closed_before_start_just_ends()
    {
        using var gateway = new FakeGateway();
        using var call = InteractiveServer.Start(gateway, CancellationToken.None);

        await call.RequestStream.CompleteAsync();

        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Patience)).IsFalse();
    }

    [Test]
    public async Task Cancelling_a_forward_call_cancels_the_handler_and_ends_the_stream()
    {
        using var gateway = new FakeGateway();
        var started = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();
        gateway.OnForward(async (_, _, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                stopped.SetResult();
                throw;
            }
        });
        var call = ForwardServer.Start(gateway, CancellationToken.None);
        await call.RequestStream.WriteAsync(Init());
        await started.Task.WaitAsync(Patience);

        call.Dispose();
        await stopped.Task.WaitAsync(Patience);

        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Patience)).IsFalse();
    }

    [Test]
    public async Task Cancelling_an_interactive_call_ends_the_server_loop()
    {
        using var gateway = new FakeGateway();
        var call = InteractiveServer.Start(gateway, CancellationToken.None);
        await call.RequestStream.WriteAsync(Start());
        var kinds = new List<Wire.ExecSandboxEvent.PayloadOneofCase>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Patience))
        {
            kinds.Add(call.ResponseStream.Current.PayloadCase);
        }

        call.Dispose();
        await FakeTestHelpers.EventuallyAsync(() => gateway.InteractiveInput.Count == 1);

        await Assert.That(kinds).IsEquivalentTo(new[] { Wire.ExecSandboxEvent.PayloadOneofCase.Exit });
    }

    [Test]
    public async Task A_cancelled_token_stops_the_forward_pumps()
    {
        using var gateway = new FakeGateway();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        gateway.OnForward(async (_, remote, ct) =>
        {
            started.SetResult();
            await remote.ReadExactlyAsync(new byte[1], ct);
        });
        using var call = ForwardServer.Start(gateway, cts.Token);
        await call.RequestStream.WriteAsync(Init());
        await started.Task.WaitAsync(Patience);

        await cts.CancelAsync();

        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Patience)).IsFalse();
    }
}
