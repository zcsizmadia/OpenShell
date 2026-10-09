// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.Globalization;
using System.Text;

namespace OpenShell;

/// <summary>File transfer between the local machine and a sandbox. Files travel as a tar stream over exec, so the sandbox image needs <c>tar</c> and <c>sh</c>.</summary>
public interface IFileClient
{
    /// <summary>Uploads one local file to <paramref name="remotePath"/>, creating parent directories.</summary>
    ValueTask UploadAsync(string sandbox, string localPath, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads <paramref name="content"/> as the file <paramref name="remotePath"/>. A non-seekable stream is buffered
    /// in memory first because a tar header needs the size up front.
    /// </summary>
    ValueTask UploadAsync(string sandbox, Stream content, string remotePath, UnixFileMode? mode = null, CancellationToken cancellationToken = default);

    /// <summary>Uploads the contents of a local directory into <paramref name="remoteDirectory"/>, creating it if needed.</summary>
    ValueTask UploadDirectoryAsync(string sandbox, string localDirectory, string remoteDirectory, CancellationToken cancellationToken = default);

    /// <summary>Downloads one remote file to <paramref name="localPath"/>. The file is replaced atomically on success.</summary>
    ValueTask DownloadAsync(string sandbox, string remotePath, string localPath, CancellationToken cancellationToken = default);

    /// <summary>Downloads one remote file into <paramref name="destination"/>.</summary>
    ValueTask DownloadAsync(string sandbox, string remotePath, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a remote directory tree into <paramref name="localDirectory"/>. Entries that would escape the destination,
    /// and links of any kind, are rejected with <see cref="InvalidDataException"/>.
    /// </summary>
    ValueTask DownloadDirectoryAsync(string sandbox, string remoteDirectory, string localDirectory, CancellationToken cancellationToken = default);
}

internal sealed class FileClient(IExecClient exec) : IFileClient
{
    private const int StderrLimit = 4096;

    public async ValueTask UploadAsync(string sandbox, string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var (directory, name) = RemotePath.Split(remotePath);
        if (Directory.Exists(localPath))
        {
            throw new ArgumentException($"local path is a directory, not a file: {localPath}", nameof(localPath));
        }

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("local file not found", localPath);
        }

        await RunUploadAsync(
            sandbox,
            directory,
            async (stdin, ct) =>
            {
                var writer = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteEntryAsync(localPath, name, ct).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UploadAsync(string sandbox, Stream content, string remotePath, UnixFileMode? mode = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (directory, name) = RemotePath.Split(remotePath);

        Stream data = content;
        MemoryStream? buffered = null;
        if (!content.CanSeek)
        {
            buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            data = buffered;
        }

        try
        {
            await RunUploadAsync(
                sandbox,
                directory,
                async (stdin, ct) =>
                {
                    var writer = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
                    await using (writer.ConfigureAwait(false))
                    {
                        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                        {
                            Mode = mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                            DataStream = data,
                        };
                        await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask UploadDirectoryAsync(string sandbox, string localDirectory, string remoteDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        RemotePath.ValidateDirectory(remoteDirectory);
        if (!Directory.Exists(localDirectory))
        {
            throw new DirectoryNotFoundException($"local directory not found: {localDirectory}");
        }

        await RunUploadAsync(
            sandbox,
            remoteDirectory,
            (stdin, ct) => new ValueTask(TarFile.CreateFromDirectoryAsync(localDirectory, stdin, includeBaseDirectory: false, ct)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DownloadAsync(string sandbox, string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullPath = Path.GetFullPath(localPath);
        var parent = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            var file = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 81920,
            });
            await using (file.ConfigureAwait(false))
            {
                await DownloadAsync(sandbox, remotePath, file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async ValueTask DownloadAsync(string sandbox, string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        try
        {
            await DownloadCoreAsync(sandbox, remotePath, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("the remote tar stream ended prematurely", e);
        }
    }

    public async ValueTask DownloadDirectoryAsync(string sandbox, string remoteDirectory, string localDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            await DownloadDirectoryCoreAsync(sandbox, remoteDirectory, localDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("the remote tar stream ended prematurely", e);
        }
    }

    private async ValueTask DownloadCoreAsync(string sandbox, string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var (directory, name) = RemotePath.Split(remotePath);
        var archive = new ExecStdoutStream(
            exec.StreamAsync(sandbox, ["tar", "-cf", "-", "-C", directory, "--", name], null, cancellationToken).GetAsyncEnumerator(cancellationToken));
        await using (archive.ConfigureAwait(false))
        {
            var reader = new TarReader(archive, leaveOpen: true);
            await using (reader.ConfigureAwait(false))
            {
                var found = false;
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (entry.EntryType is TarEntryType.Directory)
                    {
                        throw new InvalidDataException($"'{remotePath}' is a directory; use DownloadDirectoryAsync");
                    }

                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || found)
                    {
                        throw new InvalidDataException($"unexpected tar entry '{entry.Name}' of type {entry.EntryType} for '{remotePath}'");
                    }

                    if (!string.Equals(entry.Name, name, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"unexpected tar entry name '{entry.Name}'");
                    }

                    found = true;
                    if (entry.DataStream is { } data)
                    {
                        await data.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!found)
                {
                    throw new InvalidDataException($"remote command produced no file for '{remotePath}'");
                }
            }

            await archive.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DownloadDirectoryCoreAsync(string sandbox, string remoteDirectory, string localDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        RemotePath.ValidateDirectory(remoteDirectory);
        var root = Path.GetFullPath(localDirectory);
        Directory.CreateDirectory(root);
        var archive = new ExecStdoutStream(
            exec.StreamAsync(sandbox, ["tar", "-cf", "-", "-C", remoteDirectory, "."], null, cancellationToken).GetAsyncEnumerator(cancellationToken));
        await using (archive.ConfigureAwait(false))
        {
            var reader = new TarReader(archive, leaveOpen: true);
            await using (reader.ConfigureAwait(false))
            {
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
                {
                    await ExtractAsync(root, entry, cancellationToken).ConfigureAwait(false);
                }
            }

            await archive.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async ValueTask ExtractAsync(string root, TarEntry entry, CancellationToken cancellationToken)
    {
        switch (entry.EntryType)
        {
            case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                throw new InvalidDataException($"refusing to extract link entry '{entry.Name}'");
            case TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                break;
            default:
                return; // Devices, FIFOs and pax metadata have no meaning for a downloaded tree.
        }

        var target = SafeTarget(root, entry.Name);
        if (target is null)
        {
            return; // The archive root entry ("./").
        }

        if (entry.EntryType is TarEntryType.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var file = new FileStream(target, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 81920,
        });
        await using (file.ConfigureAwait(false))
        {
            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Resolves an archive entry name under <paramref name="root"/>, rejecting anything that would land outside it.</summary>
    internal static string? SafeTarget(string root, string entryName)
    {
        if (entryName.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(entryName) || entryName.StartsWith('/') || entryName.StartsWith('\\'))
        {
            throw new InvalidDataException($"unsafe tar entry name '{entryName}'");
        }

        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                throw new InvalidDataException($"unsafe tar entry name '{entryName}'");
            }
        }

        var full = Path.GetFullPath(Path.Combine(root, entryName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
        {
            return null;
        }

        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"unsafe tar entry name '{entryName}'");
        }

        return full;
    }

    private async ValueTask RunUploadAsync(string sandbox, string remoteDirectory, Func<Stream, CancellationToken, ValueTask> writeTar, CancellationToken cancellationToken)
    {
        // The directory is passed as a positional parameter, never interpolated into the script.
        string[] command = ["sh", "-c", "mkdir -p -- \"$1\" && exec tar -xf - -C \"$1\"", "sh", remoteDirectory];
        var session = await exec.InteractiveAsync(sandbox, command, 0, 0, null, allocateTty: false, cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var stderr = new StringBuilder();
            var pump = Task.Run(
                async () =>
                {
                    int? code = null;
                    await foreach (var ev in session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        switch (ev)
                        {
                            case ExecOutput { IsStdErr: true } err when stderr.Length < StderrLimit:
                                stderr.Append(err.Text);
                                break;
                            case ExecExit exit:
                                code = exit.ExitCode;
                                break;
                        }
                    }

                    return code ?? -1;
                },
                CancellationToken.None);
            try
            {
                var stdin = new SessionStdinStream(session);
                await using (stdin.ConfigureAwait(false))
                {
                    await writeTar(stdin, cancellationToken).ConfigureAwait(false);
                }

                await session.CompleteStdinAsync().ConfigureAwait(false);
            }
            catch
            {
                await session.CancelAsync().ConfigureAwait(false);
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (Exception e) when (e is OpenShellException or OperationCanceledException)
                {
                    // The original failure is the useful one.
                }

                throw;
            }

            var exitCode = await pump.ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new OpenShellException(
                    OpenShellErrorKind.Internal,
                    string.Create(CultureInfo.InvariantCulture, $"remote tar exited with code {exitCode}: {stderr.ToString().Trim()}"));
            }
        }
    }
}

internal static class RemotePath
{
    /// <summary>Splits a POSIX file path into its directory and final component.</summary>
    public static (string Directory, string Name) Split(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("remote path must not contain NUL", nameof(path));
        }

        if (path.EndsWith('/'))
        {
            throw new ArgumentException("remote path must name a file, not a directory", nameof(path));
        }

        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        if (name is "." or "..")
        {
            throw new ArgumentException("remote path must name a file", nameof(path));
        }

        var directory = slash < 0 ? "." : slash == 0 ? "/" : path[..slash];
        return (directory, name);
    }

    public static void ValidateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("remote path must not contain NUL", nameof(path));
        }
    }
}

/// <summary>A write-only stream that feeds an interactive session's standard input.</summary>
internal sealed class SessionStdinStream(ExecInteractiveSession session) : Stream
{
    public override bool CanRead => false;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        session.WriteStdinAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        session.WriteStdinAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("use WriteAsync");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Presents the stdout of a streaming exec as a readable stream; a non-zero exit code surfaces at end of stream.</summary>
internal sealed class ExecStdoutStream(IAsyncEnumerator<ExecEvent> events) : Stream
{
    private readonly StringBuilder _stderr = new();
    private ReadOnlyMemory<byte> _pending;
    private bool _finished;

    public override bool CanRead => true;

    public override bool CanWrite => false;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (_finished)
            {
                return 0;
            }

            if (!await events.MoveNextAsync().ConfigureAwait(false))
            {
                _finished = true;
                return 0;
            }

            switch (events.Current)
            {
                case ExecOutput { IsStdErr: false } output:
                    _pending = output.Data;
                    break;
                case ExecOutput err when _stderr.Length < 4096:
                    _stderr.Append(err.Text);
                    break;
                case ExecExit { ExitCode: not 0 } exit:
                    _finished = true;
                    throw new OpenShellException(
                        OpenShellErrorKind.Internal,
                        string.Create(CultureInfo.InvariantCulture, $"remote command exited with code {exit.ExitCode}: {_stderr.ToString().Trim()}"));
            }
        }

        var n = Math.Min(buffer.Length, _pending.Length);
        _pending[..n].CopyTo(buffer);
        _pending = _pending[n..];
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Reads to the end so the exit code is observed, even when the consumer stopped at the tar trailer.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        var scratch = new byte[4096];
        while (await ReadAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await events.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("use ReadAsync");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
