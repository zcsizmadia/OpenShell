// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

internal static class Tar
{
    public static async Task<byte[]> BuildAsync(params (string Name, string? Content, TarEntryType Type, string? Link)[] entries)
    {
        using var ms = new MemoryStream();
        var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true);
        await using (writer)
        {
            foreach (var (name, content, type, link) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (link is not null)
                {
                    entry.LinkName = link;
                }

                if (content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                }

                await writer.WriteEntryAsync(entry);
            }
        }

        return ms.ToArray();
    }

    public static (string Name, string? Content, TarEntryType Type, string? Link) File(string name, string content) =>
        (name, content, TarEntryType.RegularFile, null);

    public static (string Name, string? Content, TarEntryType Type, string? Link) Dir(string name) =>
        (name, null, TarEntryType.Directory, null);

    public static async Task<List<(string Name, string Content)>> ReadAsync(byte[] archive)
    {
        var result = new List<(string, string)>();
        await using var reader = new TarReader(new MemoryStream(archive));
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data)
            {
                using var buffer = new MemoryStream();
                await data.CopyToAsync(buffer);
                result.Add((entry.Name, Encoding.UTF8.GetString(buffer.ToArray())));
            }
            else
            {
                result.Add((entry.Name, ""));
            }
        }

        return result;
    }

    /// <summary>A remote that records the uploaded tar and exits with the given code.</summary>
    public static FakeCallInvoker UploadTarget(Action<byte[], Wire.ExecSandboxRequest> onArchive, int exitCode = 0, string stderr = "")
    {
        return new FakeCallInvoker().Duplex<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>("ExecSandboxInteractive", async ch =>
        {
            using var stdin = new MemoryStream();
            Wire.ExecSandboxRequest? start = null;
            await foreach (var frame in ch.ReadAllAsync())
            {
                if (frame.PayloadCase == Wire.ExecSandboxInput.PayloadOneofCase.Start)
                {
                    start = frame.Start;
                }
                else if (frame.PayloadCase == Wire.ExecSandboxInput.PayloadOneofCase.Stdin)
                {
                    frame.Stdin.WriteTo(stdin);
                }
            }

            onArchive(stdin.ToArray(), start!);
            if (stderr.Length > 0)
            {
                ch.Send(Interactive.Err(stderr));
            }

            ch.Send(Interactive.Exit(exitCode));
        });
    }

    /// <summary>A remote whose <c>tar -cf -</c> produces <paramref name="archive"/> in small chunks.</summary>
    public static FakeCallInvoker DownloadSource(byte[] archive, int exitCode = 0, string stderr = "", Action<Wire.ExecSandboxRequest>? onRequest = null)
    {
        return new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", request =>
        {
            onRequest?.Invoke(request);
            return Events(archive, exitCode, stderr);
        });
    }

    private static IEnumerable<Wire.ExecSandboxEvent> Events(byte[] archive, int exitCode, string stderr)
    {
        if (stderr.Length > 0)
        {
            yield return Interactive.Err(stderr);
        }

        for (var offset = 0; offset < archive.Length; offset += 700)
        {
            var count = Math.Min(700, archive.Length - offset);
            yield return new Wire.ExecSandboxEvent { Stdout = new Wire.ExecSandboxStdout { Data = ByteString.CopyFrom(archive, offset, count) } };
        }

        yield return Interactive.Exit(exitCode);
    }
}

public class FileUploadTests
{
    [Test]
    public async Task Upload_path_sends_a_tar_with_the_file_under_its_remote_name()
    {
        var local = Path.Combine(Path.GetTempPath(), $"os-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "payload");
        try
        {
            byte[]? archive = null;
            Wire.ExecSandboxRequest? start = null;
            var invoker = Tar.UploadTarget((a, s) => (archive, start) = (a, s));
            using var client = ClientTests.NewClient(invoker);

            await client.Files.UploadAsync("sb", local, "/data/out/result.txt");

            var entries = await Tar.ReadAsync(archive!);
            await Assert.That(entries.Single()).IsEqualTo(("result.txt", "payload"));
            await Assert.That(start!.Command).IsEquivalentTo(new[] { "sh", "-c", "mkdir -p -- \"$1\" && exec tar -xf - -C \"$1\"", "sh", "/data/out" });
            await Assert.That(start.Tty).IsFalse();
            await Assert.That(start.Sandbox).IsEqualTo("sb");
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Test]
    public async Task Upload_stream_works_for_seekable_and_forward_only_streams()
    {
        var archives = new List<byte[]>();
        var invoker = Tar.UploadTarget((a, _) => archives.Add(a));
        using var client = ClientTests.NewClient(invoker);

        await client.Files.UploadAsync("sb", new MemoryStream("seekable"u8.ToArray()), "rel.txt", UnixFileMode.UserRead | UnixFileMode.UserExecute);
        await client.Files.UploadAsync("sb", new ForwardOnlyStream("forward"u8.ToArray()), "/f.txt");

        var first = await ReadEntryAsync(archives[0]);
        await Assert.That(first.Name).IsEqualTo("rel.txt");
        await Assert.That(first.Mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var second = await Tar.ReadAsync(archives[1]);
        await Assert.That(second.Single()).IsEqualTo(("f.txt", "forward"));
        var starts = invoker.DuplexCalls.Count;
        await Assert.That(starts).IsEqualTo(2);
    }

    [Test]
    public async Task Remote_directory_is_derived_from_the_path()
    {
        var starts = new List<Wire.ExecSandboxRequest>();
        var invoker = Tar.UploadTarget((_, s) => starts.Add(s));
        using var client = ClientTests.NewClient(invoker);

        await client.Files.UploadAsync("sb", new MemoryStream([1]), "plain.bin");
        await client.Files.UploadAsync("sb", new MemoryStream([1]), "/root.bin");

        await Assert.That(starts[0].Command[^1]).IsEqualTo(".");
        await Assert.That(starts[1].Command[^1]).IsEqualTo("/");
    }

    [Test]
    public async Task Upload_directory_sends_every_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"os-updir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "A");
        await File.WriteAllTextAsync(Path.Combine(dir, "sub", "b.txt"), "B");
        try
        {
            byte[]? archive = null;
            Wire.ExecSandboxRequest? start = null;
            var invoker = Tar.UploadTarget((a, s) => (archive, start) = (a, s));
            using var client = ClientTests.NewClient(invoker);

            await client.Files.UploadDirectoryAsync("sb", dir, "/work/project");

            var files = (await Tar.ReadAsync(archive!)).Where(e => e.Content.Length > 0).Select(e => (e.Name.Replace('\\', '/'), e.Content)).ToArray();
            await Assert.That(files).IsEquivalentTo(new[] { ("a.txt", "A"), ("sub/b.txt", "B") });
            await Assert.That(start!.Command[^1]).IsEqualTo("/work/project");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task A_failing_remote_tar_reports_its_exit_code_and_stderr()
    {
        var invoker = Tar.UploadTarget((_, _) => { }, exitCode: 2, stderr: "tar: no space left");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream([1]), "/x")).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("code 2");
        await Assert.That(ex.Message).Contains("no space left");
    }

    [Test]
    public async Task A_remote_that_never_reports_an_exit_fails_the_upload()
    {
        var invoker = new FakeCallInvoker().Duplex<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>("ExecSandboxInteractive", async ch =>
        {
            await foreach (var _ in ch.ReadAllAsync())
            {
            }
        });
        using var client = ClientTests.NewClient(invoker);

        await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream([1]), "/x")).Throws<OpenShellException>();
    }

    [Test]
    public async Task A_stdin_write_failure_cancels_the_session_and_surfaces_the_cause()
    {
        var invoker = Tar.UploadTarget((_, _) => { });
        invoker.DuplexFailWritesAfter = 1;
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream(new byte[100]), "/x")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Upload_validates_arguments()
    {
        using var client = ClientTests.NewClient(Tar.UploadTarget((_, _) => { }));
        var dir = Path.GetTempPath();

        await Assert.That(async () => await client.Files.UploadAsync("sb", " ", "/x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", dir, "/x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", Path.Combine(dir, $"missing-{Guid.NewGuid():N}"), "/x")).Throws<FileNotFoundException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", (Stream)null!, "/x")).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream(), "")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream(), "/dir/")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream(), "/dir/..")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadAsync("sb", new MemoryStream(), "/a\0b")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadDirectoryAsync("sb", " ", "/x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadDirectoryAsync("sb", dir, " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadDirectoryAsync("sb", dir, "/a\0b")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.UploadDirectoryAsync("sb", Path.Combine(dir, $"missing-{Guid.NewGuid():N}"), "/x")).Throws<DirectoryNotFoundException>();
    }

    private static async Task<(string Name, UnixFileMode Mode)> ReadEntryAsync(byte[] archive)
    {
        await using var reader = new TarReader(new MemoryStream(archive));
        var entry = (await reader.GetNextEntryAsync())!;
        return (entry.Name, entry.Mode);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class FileDownloadTests
{
    private static string TempPath(string name = "dl.txt") => Path.Combine(Path.GetTempPath(), $"os-{Guid.NewGuid():N}", name);

    [Test]
    public async Task Download_writes_the_file_atomically_and_sends_the_tar_command()
    {
        var archive = await Tar.BuildAsync(Tar.File("report.txt", new string('x', 5000)));
        Wire.ExecSandboxRequest? request = null;
        var invoker = Tar.DownloadSource(archive, onRequest: r => request = r);
        using var client = ClientTests.NewClient(invoker);
        var target = TempPath();
        try
        {
            await client.Files.DownloadAsync("sb", "/srv/report.txt", target);

            await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo(new string('x', 5000));
            await Assert.That(request!.Command).IsEquivalentTo(new[] { "tar", "-cf", "-", "-C", "/srv", "--", "report.txt" });
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(target)!).Length).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(target)!, recursive: true);
        }
    }

    [Test]
    public async Task Download_replaces_an_existing_file_and_leaves_it_alone_on_failure()
    {
        var target = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "old");
        try
        {
            var good = ClientTests.NewClient(Tar.DownloadSource(await Tar.BuildAsync(Tar.File("a.txt", "new"))));
            await good.Files.DownloadAsync("sb", "/a.txt", target);
            await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("new");

            var bad = ClientTests.NewClient(Tar.DownloadSource([], exitCode: 1, stderr: "tar: a.txt: Cannot stat"));
            await Assert.That(async () => await bad.Files.DownloadAsync("sb", "/a.txt", target)).Throws<OpenShellException>();

            await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("new");
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(target)!).Length).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(target)!, recursive: true);
        }
    }

    [Test]
    public async Task Download_into_a_stream_copies_the_content()
    {
        var invoker = Tar.DownloadSource(await Tar.BuildAsync(Tar.File("n.bin", "bytes")));
        using var client = ClientTests.NewClient(invoker);
        using var destination = new MemoryStream();

        await client.Files.DownloadAsync("sb", "n.bin", destination);

        await Assert.That(Encoding.UTF8.GetString(destination.ToArray())).IsEqualTo("bytes");
    }

    [Test]
    public async Task A_failing_remote_tar_reports_stderr()
    {
        var invoker = Tar.DownloadSource([], exitCode: 2, stderr: "tar: nope: Cannot open");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Files.DownloadAsync("sb", "/nope", new MemoryStream())).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("code 2");
        await Assert.That(ex.Message).Contains("Cannot open");
    }

    [Test]
    public async Task Unexpected_archive_contents_are_rejected()
    {
        async Task Expect(byte[] archive, string path, string message)
        {
            using var client = ClientTests.NewClient(Tar.DownloadSource(archive));
            var ex = await Assert.That(async () => await client.Files.DownloadAsync("sb", path, new MemoryStream())).Throws<InvalidDataException>();
            await Assert.That(ex!.Message).Contains(message);
        }

        await Expect(await Tar.BuildAsync(Tar.Dir("d")), "/d", "directory");
        await Expect(await Tar.BuildAsync(Tar.File("other.txt", "x")), "/a.txt", "entry name");
        await Expect(await Tar.BuildAsync(Tar.File("a.txt", "x"), Tar.File("b.txt", "y")), "/a.txt", "unexpected tar entry");
        await Expect(await Tar.BuildAsync((("a.txt"), null, TarEntryType.SymbolicLink, "/etc/passwd")), "/a.txt", "unexpected tar entry");
        await Expect(new byte[1024], "/a.txt", "no file");
    }

    [Test]
    public async Task A_truncated_or_empty_archive_is_reported_as_invalid_data()
    {
        using var empty = ClientTests.NewClient(Tar.DownloadSource([]));
        var ex = await Assert.That(async () => await empty.Files.DownloadAsync("sb", "/a.txt", new MemoryStream())).Throws<InvalidDataException>();
        await Assert.That(ex!.Message).Contains("prematurely");

        var full = await Tar.BuildAsync(Tar.File("a.txt", new string('z', 4000)));
        using var truncated = ClientTests.NewClient(Tar.DownloadSource(full[..1000]));
        await Assert.That(async () => await truncated.Files.DownloadAsync("sb", "/a.txt", new MemoryStream())).Throws<InvalidDataException>();

        using var directory = ClientTests.NewClient(Tar.DownloadSource(full[..700]));
        var root = TempPath("tree");
        try
        {
            await Assert.That(async () => await directory.Files.DownloadDirectoryAsync("sb", "/srv", root)).Throws<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Test]
    public async Task Download_validates_arguments()
    {
        using var client = ClientTests.NewClient(Tar.DownloadSource([]));

        await Assert.That(async () => await client.Files.DownloadAsync("sb", "/x", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.DownloadAsync("sb", "", new MemoryStream())).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.DownloadAsync("sb", "/x", (Stream)null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Files.DownloadDirectoryAsync("sb", "/x", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.DownloadDirectoryAsync("sb", "", TempPath())).Throws<ArgumentException>();
        await Assert.That(async () => await client.Files.DownloadDirectoryAsync("sb", "/a\0b", TempPath())).Throws<ArgumentException>();
    }

    [Test]
    public async Task Download_directory_extracts_a_tree()
    {
        var archive = await Tar.BuildAsync(
            Tar.Dir("./"),
            Tar.Dir("./sub/"),
            Tar.File("./top.txt", "T"),
            Tar.File("./sub/deep.txt", "D"),
            ("./fifo", null, TarEntryType.Fifo, null));
        Wire.ExecSandboxRequest? request = null;
        var invoker = Tar.DownloadSource(archive, onRequest: r => request = r);
        using var client = ClientTests.NewClient(invoker);
        var root = TempPath("tree");
        try
        {
            await client.Files.DownloadDirectoryAsync("sb", "/srv/app", root);

            await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "top.txt"))).IsEqualTo("T");
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "sub", "deep.txt"))).IsEqualTo("D");
            await Assert.That(Directory.GetFileSystemEntries(root).Length).IsEqualTo(2);
            await Assert.That(request!.Command).IsEquivalentTo(new[] { "tar", "-cf", "-", "-C", "/srv/app", "." });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Test]
    public async Task Download_directory_rejects_traversal_absolute_names_and_links()
    {
        var cases = new (string Name, (string Name, string? Content, TarEntryType Type, string? Link) Entry)[]
        {
            ("parent traversal", Tar.File("../evil.txt", "x")),
            ("nested traversal", Tar.File("a/../../evil.txt", "x")),
            ("backslash traversal", Tar.File("a\\..\\..\\evil.txt", "x")),
            ("absolute", Tar.File("/etc/evil.txt", "x")),
            ("symlink", ("link", null, TarEntryType.SymbolicLink, "/etc")),
            ("hardlink", ("hard", null, TarEntryType.HardLink, "target")),
        };
        foreach (var (name, entry) in cases)
        {
            var archive = await Tar.BuildAsync(Tar.File("ok.txt", "fine"), entry);
            using var client = ClientTests.NewClient(Tar.DownloadSource(archive));
            var root = TempPath("tree");
            try
            {
                await Assert.That(async () => await client.Files.DownloadDirectoryAsync("sb", "/srv", root)).Throws<InvalidDataException>();
                await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt"))).IsFalse();
                await Assert.That(name).IsNotNull();
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
        }
    }

    [Test]
    public async Task SafeTarget_resolves_only_inside_the_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "safe-root"));

        await Assert.That(FileClient.SafeTarget(root, "a/b.txt")).IsEqualTo(Path.Combine(root, "a", "b.txt"));
        await Assert.That(FileClient.SafeTarget(root, "./c.txt")).IsEqualTo(Path.Combine(root, "c.txt"));
        await Assert.That(FileClient.SafeTarget(root, "./")).IsNull();
        await Assert.That(FileClient.SafeTarget(root + Path.DirectorySeparatorChar, ".")).IsNull();
        await Assert.That(() => FileClient.SafeTarget(root, "a\0b")).Throws<InvalidDataException>();
        await Assert.That(() => FileClient.SafeTarget(root, "..")).Throws<InvalidDataException>();
        await Assert.That(() => FileClient.SafeTarget(root, "x/../../y")).Throws<InvalidDataException>();
        await Assert.That(() => FileClient.SafeTarget(root, "/abs")).Throws<InvalidDataException>();
        await Assert.That(() => FileClient.SafeTarget(root, "\\abs")).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ExecStdoutStream_handles_edge_cases()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ =>
            new[] { Interactive.Out("abcdef"), Interactive.Exit(0) });
        using var client = ClientTests.NewClient(invoker);
        var stream = new ExecStdoutStream(client.Exec.StreamAsync("sb", ["x"]).GetAsyncEnumerator());
        await using (stream)
        {
            var small = new byte[4];
            await Assert.That(await stream.ReadAsync(small, 0, 4, CancellationToken.None)).IsEqualTo(4);
            await Assert.That(await stream.ReadAsync(small.AsMemory())).IsEqualTo(2);
            await Assert.That(await stream.ReadAsync(small.AsMemory())).IsEqualTo(0);
            await Assert.That(await stream.ReadAsync(small.AsMemory())).IsEqualTo(0);
            await stream.DrainAsync(CancellationToken.None);
            await Assert.That(stream.CanRead && !stream.CanWrite && !stream.CanSeek).IsTrue();
            await Assert.That(() => stream.Read(small, 0, 1)).Throws<NotSupportedException>();
            await Assert.That(() => stream.Write(small, 0, 1)).Throws<NotSupportedException>();
            await Assert.That(() => stream.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
            await Assert.That(() => stream.SetLength(0)).Throws<NotSupportedException>();
            await Assert.That(() => stream.Length).Throws<NotSupportedException>();
            await Assert.That(() => stream.Position).Throws<NotSupportedException>();
            await Assert.That(() => stream.Position = 0).Throws<NotSupportedException>();
            stream.Flush();
        }
    }

    [Test]
    public async Task SessionStdinStream_is_write_only()
    {
        var invoker = Interactive.With(Interactive.Shell);
        using var client = ClientTests.NewClient(invoker);
        await using var session = await client.Exec.InteractiveAsync("sb", ["x"], 1, 1);
        var stdin = new SessionStdinStream(session);

        await stdin.WriteAsync(new byte[] { 1, 2 }, 0, 2, CancellationToken.None);
        await stdin.FlushAsync();
        stdin.Flush();

        await Assert.That(stdin.CanRead).IsFalse();
        await Assert.That(stdin.CanWrite).IsTrue();
        await Assert.That(stdin.CanSeek).IsFalse();
        await Assert.That(() => stdin.Read(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stdin.Write(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stdin.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
        await Assert.That(() => stdin.SetLength(0)).Throws<NotSupportedException>();
        await Assert.That(() => stdin.Length).Throws<NotSupportedException>();
        await Assert.That(() => stdin.Position).Throws<NotSupportedException>();
        await Assert.That(() => stdin.Position = 0).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Gateway_failure_during_download_is_mapped()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Throwing());
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Files.DownloadAsync("sb", "/x", new MemoryStream())).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    private static IEnumerable<Wire.ExecSandboxEvent> Throwing()
    {
        throw new RpcException(new Status(StatusCode.NotFound, "sandbox gone"));
#pragma warning disable CS0162 // Unreachable code: makes this an iterator that throws on first MoveNext.
        yield break;
#pragma warning restore CS0162
    }
}
