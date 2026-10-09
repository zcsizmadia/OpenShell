// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ExecPythonTests
{
    [Test]
    public async Task BuildCommand_defaults_and_overrides()
    {
        await Assert.That(ExecPythonExtensions.BuildCommand("print(1)", null)).IsEquivalentTo(new[] { "python", "-c", "print(1)" });
        await Assert.That(ExecPythonExtensions.BuildCommand("x", new PythonExecOptions { Interpreter = "python3.12", Arguments = ["a", "b"] }))
            .IsEquivalentTo(new[] { "python3.12", "-c", "x", "a", "b" });
        await Assert.That(ExecPythonExtensions.BuildCommand("x", new PythonExecOptions { Interpreter = "" }))
            .IsEquivalentTo(new[] { "python", "-c", "x" });
        await Assert.That(() => ExecPythonExtensions.BuildCommand(" ", null)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ExecPython_runs_the_script_and_buffers_output()
    {
        Wire.ExecSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", r =>
        {
            seen = r;
            return [Interactive.Out("42\n"), Interactive.Exit(0)];
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.ExecPythonAsync(
            "sb",
            "import sys; print(sys.argv[1])",
            new PythonExecOptions { Arguments = ["42"], Exec = new ExecOptions { WorkingDirectory = "/w" } });

        await Assert.That(result.StdoutText).IsEqualTo("42\n");
        await Assert.That(seen!.Command).IsEquivalentTo(new[] { "python", "-c", "import sys; print(sys.argv[1])", "42" });
        await Assert.That(seen.Workdir).IsEqualTo("/w");
    }

    [Test]
    public async Task ExecPythonStream_yields_events()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [Interactive.Out("a"), Interactive.Exit(3)]);
        using var client = ClientTests.NewClient(invoker);

        var events = new List<ExecEvent>();
        await foreach (var e in client.Exec.ExecPythonStreamAsync("sb", "pass"))
        {
            events.Add(e);
        }

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[1]).IsEqualTo(new ExecExit(3));
    }

    [Test]
    public async Task Null_exec_client_is_rejected()
    {
        await Assert.That(async () => await ExecPythonExtensions.ExecPythonAsync(null!, "sb", "x")).Throws<ArgumentNullException>();
        await Assert.That(() => ExecPythonExtensions.ExecPythonStreamAsync(null!, "sb", "x")).Throws<ArgumentNullException>();
    }
}
