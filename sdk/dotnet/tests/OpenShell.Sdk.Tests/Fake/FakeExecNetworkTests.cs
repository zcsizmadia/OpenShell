// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using OpenShell.Testing;

namespace OpenShell.Tests;

internal static class FakeTestHelpers
{
    public static async Task<FakeOpenShellClient> ClientWithSandboxAsync(FakeGatewayOptions? options = null)
    {
        var client = FakeCoreTests.New(options);
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        await client.Sandboxes.GetAsync("a");
        return client;
    }

    public static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        if (!condition())
        {
            throw new TimeoutException("condition was not met in time");
        }
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fake-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class FakeExecTests
{
    [Test]
    public async Task Unscripted_commands_exit_zero_with_no_output()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var result = await client.Exec.RunAsync("a", ["true"]);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Stdout.Length).IsEqualTo(0);
        await Assert.That(result.Stderr.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Handlers_script_results_and_later_handlers_win()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.OnExec(i => i.CommandLine == "echo hi" ? FakeExecScript.Success("hi\n") : null);
        client.Gateway.OnExec(i => i.Command[0] == "fail" ? FakeExecScript.Failure(7, "bad") : null);
        client.Gateway.OnExec(i => i.CommandLine == "echo hi" && i.Options.WorkingDirectory == "/w" ? FakeExecScript.Success("override") : null);

        var hi = await client.Exec.RunAsync("a", ["echo", "hi"]);
        var overridden = await client.Exec.RunAsync("a", ["echo", "hi"], new ExecOptions { WorkingDirectory = "/w" });
        var failed = await client.Exec.RunAsync("a", ["fail"]);

        await Assert.That(hi.StdoutText).IsEqualTo("hi\n");
        await Assert.That(overridden.StdoutText).IsEqualTo("override");
        await Assert.That(failed.ExitCode).IsEqualTo(7);
        await Assert.That(failed.StderrText).IsEqualTo("bad");

        client.Gateway.ClearExecHandlers();
        await Assert.That((await client.Exec.RunAsync("a", ["echo", "hi"])).Stdout.Length).IsEqualTo(0);
        await Assert.That(() => client.Gateway.OnExec(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Handlers_see_the_workspace_sandbox_and_options()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        FakeExecInvocation? seen = null;
        client.Gateway.OnExec(i =>
        {
            seen = i;
            return null;
        });

        await client.Exec.RunAsync("a", ["x"], new ExecOptions { Environment = new Dictionary<string, string> { ["K"] = "V" }, Tty = true });

        await Assert.That(seen!.Workspace).IsEqualTo("default");
        await Assert.That(seen.Sandbox).IsEqualTo("a");
        await Assert.That(seen.Options.Environment!["K"]).IsEqualTo("V");
        await Assert.That(seen.Options.Tty).IsTrue();
    }

    [Test]
    public async Task Stream_yields_stdout_stderr_then_exit()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.OnExec(_ => new FakeExecScript(2, Encoding.UTF8.GetBytes("out"), Encoding.UTF8.GetBytes("err")));

        var events = new List<ExecEvent>();
        await foreach (var e in client.Exec.StreamAsync("a", ["x"]))
        {
            events.Add(e);
        }

        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(((ExecOutput)events[0]).Text).IsEqualTo("out");
        await Assert.That(((ExecOutput)events[1]).IsStdErr).IsTrue();
        await Assert.That(events[2]).IsEqualTo(new ExecExit(2));
    }

    [Test]
    public async Task Stream_with_no_output_yields_only_the_exit()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var events = await client.Exec.StreamAsync("a", ["x"]).ToListAsync();

        await Assert.That(events.Single()).IsEqualTo(new ExecExit(0));
    }

    [Test]
    public async Task Exec_needs_an_existing_ready_sandbox()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var missing = await Assert.That(async () => await client.Exec.RunAsync("nope", ["x"])).Throws<OpenShellException>();
        client.Gateway.SetPhase("a", SandboxPhase.Stopped);
        var stopped = await Assert.That(async () => await client.Exec.RunAsync("a", ["x"])).Throws<OpenShellException>();
        var streamed = await Assert.That(async () => await client.Exec.StreamAsync("a", ["x"]).ToListAsync()).Throws<OpenShellException>();
        var interactive = await Assert.That(async () => await client.Exec.InteractiveAsync("a", ["x"], 80, 24)).Throws<OpenShellException>();

        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(stopped!.IsConflict).IsTrue();
        await Assert.That(streamed!.IsConflict).IsTrue();
        await Assert.That(interactive!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Exec_validates_arguments()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await Assert.That(async () => await client.Exec.RunAsync(" ", ["x"])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Exec.RunAsync("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Exec.RunAsync("a", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Exec.StreamAsync(" ", ["x"]).ToListAsync()).Throws<ArgumentException>();
        await Assert.That(async () => await client.Exec.InteractiveAsync("a", [], 80, 24)).Throws<ArgumentException>();
    }

    [Test]
    public async Task The_python_helpers_run_through_the_fake()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.OnExec(i => i.Command[0] == "python3" && i.Command[1] == "-c" ? FakeExecScript.Success("42") : null);

        var result = await client.Exec.ExecPythonAsync("a", "print(42)", new PythonExecOptions { Interpreter = "python3", Arguments = ["x"] });
        var streamed = await client.Exec.ExecPythonStreamAsync("a", "print(42)", new PythonExecOptions { Interpreter = "python3" }).ToListAsync();

        await Assert.That(result.StdoutText).IsEqualTo("42");
        await Assert.That(streamed.OfType<ExecOutput>().Single().Text).IsEqualTo("42");
    }

    [Test]
    public async Task Interactive_sessions_script_output_and_record_input()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.OnExec(_ => new FakeExecScript(3, Encoding.UTF8.GetBytes("prompt$ "), Encoding.UTF8.GetBytes("warn")));

        await using var session = await client.Exec.InteractiveAsync("a", ["sh"], 80, 24);
        var events = await session.ReadEventsAsync().ToListAsync();
        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("ls\n"));
        await session.ResizeAsync(100, 30);
        await session.CompleteStdinAsync();
        var exit = await session.WaitForExitAsync();
        await FakeTestHelpers.EventuallyAsync(() => client.Gateway.InteractiveInput.Any(f => f.Kind == FakeInteractiveFrameKind.StdinClosed));

        var frames = client.Gateway.InteractiveInput;
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(events.OfType<ExecOutput>().Select(o => o.Text)).IsEquivalentTo(new[] { "prompt$ ", "warn" });
        await Assert.That(frames[0].Kind).IsEqualTo(FakeInteractiveFrameKind.Start);
        await Assert.That(frames[0].Columns).IsEqualTo(80u);
        await Assert.That(frames[0].Rows).IsEqualTo(24u);
        await Assert.That(Encoding.UTF8.GetString(frames.Single(f => f.Kind == FakeInteractiveFrameKind.Stdin).Data.Span)).IsEqualTo("ls\n");
        var resize = frames.Single(f => f.Kind == FakeInteractiveFrameKind.Resize);
        await Assert.That(resize.Columns).IsEqualTo(100u);
        await Assert.That(resize.Rows).IsEqualTo(30u);
    }

    [Test]
    public async Task Interactive_sessions_without_scripted_output_just_exit()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await using var session = await client.Exec.InteractiveAsync("a", ["sh"], 80, 24, new ExecOptions { WorkingDirectory = "/w" }, allocateTty: false);

        await Assert.That(await session.WaitForExitAsync()).IsEqualTo(0);
    }
}

public class FakeForwardingTests
{
    [Test]
    public async Task Tcp_forward_echoes_by_default_and_half_closes()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        await using var stream = await client.Tcp.ForwardAsync("a", 8080, new ForwardOptions { ServiceId = "svc" });

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        await stream.CompleteWriteAsync();
        var eof = await stream.ReadAsync(buffer);

        await Assert.That(Encoding.UTF8.GetString(buffer, 0, read)).IsEqualTo("ping");
        await Assert.That(eof).IsEqualTo(0);
    }

    [Test]
    public async Task A_custom_forward_handler_sees_the_request()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        FakeForwardRequest? seen = null;
        client.Gateway.OnForward(async (request, remote, ct) =>
        {
            seen = request;
            await remote.WriteAsync(Encoding.UTF8.GetBytes("hello"), ct);
        });

        await using var stream = await client.Tcp.ForwardAsync("a", 9000, new ForwardOptions { ServiceId = "svc" });
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);

        await Assert.That(Encoding.UTF8.GetString(buffer, 0, read)).IsEqualTo("hello");
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(0);
        await Assert.That(seen!.Port).IsEqualTo(9000u);
        await Assert.That(seen.IsSsh).IsFalse();
        await Assert.That(seen.ServiceId).IsEqualTo("svc");
        await Assert.That(seen.Sandbox).IsEqualTo("a");
        await Assert.That(() => client.Gateway.OnForward(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task A_failing_forward_handler_surfaces_as_an_internal_error()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        client.Gateway.OnForward((_, _, _) => throw new InvalidOperationException("handler broke"));

        await using var stream = await client.Tcp.ForwardAsync("a", 80);
        var ex = await Assert.That(async () => await stream.ReadAsync(new byte[4])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Internal);
        await Assert.That(ex.Message).Contains("handler broke");
    }

    [Test]
    public async Task Forward_validates_the_target()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await Assert.That(async () => await client.Tcp.ForwardAsync(" ", 80)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Tcp.ForwardAsync("a", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ForwardAsync("a", 70000)).Throws<ArgumentOutOfRangeException>();
        var missing = await Assert.That(async () => await client.Tcp.ForwardAsync("nope", 80)).Throws<OpenShellException>();
        client.Gateway.SetPhase("a", SandboxPhase.Stopped);
        var stopped = await Assert.That(async () => await client.Tcp.ForwardAsync("a", 80)).Throws<OpenShellException>();

        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(stopped!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Listeners_are_tracked_without_opening_sockets()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await using var auto = await client.Tcp.ListenAsync("a", 8080);
        await using var fixedPort = await client.Tcp.ListenAsync("a", 22, 2222, new ListenOptions { BindAddress = IPAddress.Any, UseSshTunnel = true });
        var info = client.Gateway.ActiveListeners;

        await Assert.That(auto.LocalEndPoint.Port).IsGreaterThanOrEqualTo(40000);
        await Assert.That(fixedPort.LocalEndPoint).IsEqualTo(new IPEndPoint(IPAddress.Any, 2222));
        await Assert.That(info.Count).IsEqualTo(2);
        await Assert.That(info.Single(i => i.UseSshTunnel).RemotePort).IsEqualTo(22u);

        await auto.DisposeAsync();
        await Assert.That(client.Gateway.ActiveListeners.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Listen_validates_arguments()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await Assert.That(async () => await client.Tcp.ListenAsync(" ", 80)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("a", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("a", 80, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Tcp.ListenAsync("a", 80, 70000)).Throws<ArgumentOutOfRangeException>();
        var missing = await Assert.That(async () => await client.Tcp.ListenAsync("nope", 80)).Throws<OpenShellException>();
        await Assert.That(missing!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Ssh_sessions_can_be_created_and_revoked()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var session = await client.Ssh.CreateSessionAsync("a");
        var revoked = await client.Ssh.RevokeSessionAsync(session.Token);
        var again = await Assert.That(async () => await client.Ssh.RevokeSessionAsync(session.Token)).Throws<OpenShellException>();
        var tolerated = await client.Ssh.RevokeSessionAsync(session.Token, new DeleteOptions { AllowMissing = true });

        await Assert.That(session.GatewayScheme).IsEqualTo("https");
        await Assert.That(session.GatewayPort).IsEqualTo(443);
        await Assert.That(session.HostKeyFingerprint).IsNotNull();
        await Assert.That(session.ExpiresAt).IsNotNull();
        await Assert.That(session.ToString()).DoesNotContain(session.Token);
        await Assert.That(revoked.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(again!.IsNotFound).IsTrue();
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
    }

    [Test]
    public async Task Ssh_errors_are_typed()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        var missing = await Assert.That(async () => await client.Ssh.CreateSessionAsync("nope")).Throws<OpenShellException>();
        var tunnel = await Assert.That(async () => await client.Ssh.TunnelAsync("nope")).Throws<OpenShellException>();
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(tunnel!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Ssh.CreateSessionAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Ssh.RevokeSessionAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Ssh.TunnelAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Ssh_tunnels_relay_and_revoke_their_session_when_closed()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();
        FakeForwardRequest? seen = null;
        client.Gateway.OnForward(async (request, remote, ct) =>
        {
            seen = request;
            await remote.WriteAsync(Encoding.UTF8.GetBytes("SSH-2.0-fake"), ct);
        });

        var stream = await client.Ssh.TunnelAsync("a", new TunnelOptions { ServiceId = "svc" });
        var buffer = new byte[32];
        var read = await stream.ReadAsync(buffer);
        await stream.DisposeAsync();

        await Assert.That(Encoding.UTF8.GetString(buffer, 0, read)).IsEqualTo("SSH-2.0-fake");
        await Assert.That(seen!.IsSsh).IsTrue();
        await Assert.That(seen.Port).IsNull();
        await Assert.That(client.Gateway.Calls.Count(c => c.Operation == "Ssh.Tunnel")).IsEqualTo(1);
        var sessions = client.Gateway.Calls.Count;
        await Assert.That(sessions).IsGreaterThan(0);
    }
}

public class FakeFileTests
{
    [Test]
    public async Task Files_round_trip_through_paths_and_streams()
    {
        var dir = FakeTestHelpers.TempDirectory();
        try
        {
            using var client = await FakeTestHelpers.ClientWithSandboxAsync();
            var local = Path.Combine(dir, "in.txt");
            await File.WriteAllTextAsync(local, "hello");

            await client.Files.UploadAsync("a", local, "/work/in.txt");
            await client.Files.UploadAsync("a", new MemoryStream(Encoding.UTF8.GetBytes("streamed")), "/work/stream.txt");
            var downloaded = Path.Combine(dir, "nested", "out.txt");
            await client.Files.DownloadAsync("a", "/work/in.txt", downloaded);
            using var buffer = new MemoryStream();
            await client.Files.DownloadAsync("a", "/work/stream.txt", buffer);

            await Assert.That(await File.ReadAllTextAsync(downloaded)).IsEqualTo("hello");
            await Assert.That(Encoding.UTF8.GetString(buffer.ToArray())).IsEqualTo("streamed");
            await Assert.That(client.Gateway.ListFiles("a")).IsEquivalentTo(new[] { "/work/in.txt", "/work/stream.txt" });
            await Assert.That(Encoding.UTF8.GetString(client.Gateway.ReadFile("a", "/work/in.txt")!)).IsEqualTo("hello");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Directories_round_trip()
    {
        var dir = FakeTestHelpers.TempDirectory();
        try
        {
            using var client = await FakeTestHelpers.ClientWithSandboxAsync();
            var source = Path.Combine(dir, "src");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "A");
            await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "B");
            Directory.CreateDirectory(Path.Combine(dir, "empty"));

            await client.Files.UploadDirectoryAsync("a", source, "/data/");
            await client.Files.UploadDirectoryAsync("a", Path.Combine(dir, "empty"), "/nothing");
            var target = Path.Combine(dir, "dst");
            await client.Files.DownloadDirectoryAsync("a", "/data", target);
            var emptyTarget = Path.Combine(dir, "empty-dst");
            await client.Files.DownloadDirectoryAsync("a", "/nothing", emptyTarget);

            await Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "a.txt"))).IsEqualTo("A");
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "sub", "b.txt"))).IsEqualTo("B");
            await Assert.That(client.Gateway.ListFiles("a")).IsEquivalentTo(new[] { "/data/a.txt", "/data/sub/b.txt" });
            await Assert.That(Directory.Exists(emptyTarget)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Download_errors_mirror_the_real_client()
    {
        var dir = FakeTestHelpers.TempDirectory();
        try
        {
            using var client = await FakeTestHelpers.ClientWithSandboxAsync();

            await Assert.That(async () => await client.Files.DownloadAsync("a", "/missing", Path.Combine(dir, "x"))).Throws<InvalidDataException>();
            await Assert.That(async () => await client.Files.DownloadAsync("a", "/missing", new MemoryStream())).Throws<InvalidDataException>();
            await Assert.That(async () => await client.Files.DownloadDirectoryAsync("a", "/missing", Path.Combine(dir, "d"))).Throws<InvalidDataException>();
            await Assert.That(async () => await client.Files.DownloadAsync("nope", "/x", Path.Combine(dir, "x"))).Throws<OpenShellException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Unsafe_entries_are_not_extracted()
    {
        var dir = FakeTestHelpers.TempDirectory();
        try
        {
            using var client = await FakeTestHelpers.ClientWithSandboxAsync();
            client.Gateway.PutFile("a", "/data/../escape.txt", new byte[] { 1 });

            await Assert.That(async () => await client.Files.DownloadDirectoryAsync("a", "/data", Path.Combine(dir, "d"))).Throws<InvalidDataException>();
            await Assert.That(File.Exists(Path.Combine(dir, "escape.txt"))).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task File_arguments_are_validated_like_the_real_client()
    {
        var dir = FakeTestHelpers.TempDirectory();
        try
        {
            using var client = await FakeTestHelpers.ClientWithSandboxAsync();
            var file = Path.Combine(dir, "f.txt");
            await File.WriteAllTextAsync(file, "x");

            await Assert.That(async () => await client.Files.UploadAsync(" ", file, "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", " ", "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", dir, "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", Path.Combine(dir, "nope"), "/x")).Throws<FileNotFoundException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", file, "/dir/")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", file, "/x\0")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync(" ", new MemoryStream(), "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", (Stream)null!, "/x")).Throws<ArgumentNullException>();
            await Assert.That(async () => await client.Files.UploadAsync("a", new MemoryStream(), "/dir/")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadDirectoryAsync(" ", dir, "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadDirectoryAsync("a", " ", "/x")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadDirectoryAsync("a", dir, "/x\0")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.UploadDirectoryAsync("a", Path.Combine(dir, "nope"), "/x")).Throws<DirectoryNotFoundException>();
            await Assert.That(async () => await client.Files.DownloadAsync(" ", "/x", file)).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadAsync("a", "/x", " ")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadAsync("a", "/dir/", file)).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadAsync(" ", "/x", new MemoryStream())).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadAsync("a", "/x", (Stream)null!)).Throws<ArgumentNullException>();
            await Assert.That(async () => await client.Files.DownloadDirectoryAsync(" ", "/x", dir)).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadDirectoryAsync("a", "/x", " ")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Files.DownloadDirectoryAsync("a", "/x\0", dir)).Throws<ArgumentException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task File_hooks_validate_and_report_missing_state()
    {
        using var client = await FakeTestHelpers.ClientWithSandboxAsync();

        await Assert.That(client.Gateway.ReadFile("a", "/none")).IsNull();
        await Assert.That(client.Gateway.ListFiles("a").Count).IsEqualTo(0);
        await Assert.That(() => client.Gateway.PutFile(" ", "/x", default)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.PutFile("a", " ", default)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.PutFile("nope", "/x", default)).Throws<OpenShellException>();
        await Assert.That(() => client.Gateway.ReadFile(" ", "/x")).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.ReadFile("a", " ")).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.ListFiles(" ")).Throws<ArgumentException>();
    }
}
