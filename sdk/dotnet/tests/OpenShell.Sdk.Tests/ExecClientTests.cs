// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ExecClientTests
{
    private static Wire.ExecSandboxEvent Out(string text) =>
        new() { Stdout = new Wire.ExecSandboxStdout { Data = ByteString.CopyFromUtf8(text) } };

    private static Wire.ExecSandboxEvent Err(string text) =>
        new() { Stderr = new Wire.ExecSandboxStderr { Data = ByteString.CopyFromUtf8(text) } };

    private static Wire.ExecSandboxEvent Exit(int code) =>
        new() { Exit = new Wire.ExecSandboxExit { ExitCode = code } };

    [Test]
    public async Task Run_buffers_stdout_stderr_and_exit_code()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [Out("hel"), Err("warn"), Out("lo"), Exit(3)]);
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["echo", "hello"]);

        await Assert.That(result.StdoutText).IsEqualTo("hello");
        await Assert.That(result.StderrText).IsEqualTo("warn");
        await Assert.That(result.ExitCode).IsEqualTo(3);
        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task Run_buffer_grows_across_many_chunks()
    {
        var chunk = new string('x', 1000);
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => Enumerable.Range(0, 20).Select(_ => Out(chunk)).Append(Exit(0)));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["x"]);

        await Assert.That(result.Stdout.Length).IsEqualTo(20_000);
        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Request_carries_all_options()
    {
        Wire.ExecSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", r =>
        {
            seen = r;
            return [Exit(0)];
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        await client.Exec.RunAsync(
            "sb",
            ["sh", "-c", "cat"],
            new ExecOptions
            {
                WorkingDirectory = "/work",
                Environment = new Dictionary<string, string> { ["A"] = "1" },
                Timeout = TimeSpan.FromSeconds(30),
                Stdin = new byte[] { 1, 2, 3 },
                Tty = true,
                NoLoginShell = true,
                RequestId = "rid",
            });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.Command).IsEquivalentTo(new[] { "sh", "-c", "cat" });
        await Assert.That(seen.Workdir).IsEqualTo("/work");
        await Assert.That(seen.Environment["A"]).IsEqualTo("1");
        await Assert.That(seen.ExecutionTimeout.Seconds).IsEqualTo(30L);
        await Assert.That(seen.Stdin.ToByteArray()).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(seen.Tty).IsTrue();
        await Assert.That(seen.NoLoginShell).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("rid");
    }

    [Test]
    public async Task Stream_yields_events_in_order_ending_with_exit()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [Out("a"), Err("b"), Exit(0)]);
        using var client = ClientTests.NewClient(invoker);

        var events = new List<ExecEvent>();
        await foreach (var e in client.Exec.StreamAsync("sb", ["x"]))
        {
            events.Add(e);
        }

        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(((ExecOutput)events[0]).IsStdErr).IsFalse();
        await Assert.That(((ExecOutput)events[0]).Text).IsEqualTo("a");
        await Assert.That(((ExecOutput)events[1]).IsStdErr).IsTrue();
        await Assert.That(events[2]).IsEqualTo(new ExecExit(0));
    }

    [Test]
    public async Task Stream_without_exit_event_throws()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => [Out("partial")]);
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["x"])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(ex.Message).Contains("exit");
    }

    [Test]
    public async Task Empty_payload_events_are_ignored()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [new Wire.ExecSandboxEvent(), Exit(0)]);
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["x"]);

        await Assert.That(result.Stdout.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Exec.RunAsync("", ["x"])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Exec.RunAsync("sb", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Exec.RunAsync("sb", [])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Stream_failure_is_mapped()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => Throwing());
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["x"])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
    }

    private static IEnumerable<Wire.ExecSandboxEvent> Throwing()
    {
        yield return Out("x");
        throw new RpcException(new Status(StatusCode.NotFound, "sandbox gone"));
    }
}
