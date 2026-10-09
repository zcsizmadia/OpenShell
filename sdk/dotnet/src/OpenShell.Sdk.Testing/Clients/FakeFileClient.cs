// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    /// <summary>Puts a file into a sandbox's in-memory file system, as if it had been uploaded.</summary>
    public void PutFile(string sandbox, string remotePath, ReadOnlyMemory<byte> content, string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            StoreFile(entry, remotePath, content.ToArray());
        }
    }

    /// <summary>Reads a file from a sandbox's in-memory file system; null when it does not exist.</summary>
    public byte[]? ReadFile(string sandbox, string remotePath, string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        lock (Sync)
        {
            var key = new Key(workspace ?? Options.DefaultWorkspace, sandbox);
            return Files.TryGetValue(key, out var files) && files.TryGetValue(remotePath, out var content) ? content.ToArray() : null;
        }
    }

    /// <summary>The file paths stored in a sandbox, sorted.</summary>
    public IReadOnlyList<string> ListFiles(string sandbox, string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        lock (Sync)
        {
            var key = new Key(workspace ?? Options.DefaultWorkspace, sandbox);
            return Files.TryGetValue(key, out var files) ? [.. files.Keys] : [];
        }
    }

    internal void StoreFile(SandboxEntry sandbox, string remotePath, byte[] content)
    {
        var key = new Key(sandbox.Workspace, sandbox.Name);
        if (!Files.TryGetValue(key, out var files))
        {
            Files[key] = files = [with(StringComparer.Ordinal)];
        }

        files[remotePath] = content;
    }

    internal void StoreDirectory(SandboxEntry sandbox, string remoteDirectory)
    {
        var key = new Key(sandbox.Workspace, sandbox.Name);
        if (!Directories.TryGetValue(key, out var directories))
        {
            Directories[key] = directories = [with(StringComparer.Ordinal)];
        }

        directories.Add(remoteDirectory.TrimEnd('/'));
    }
}

internal sealed class FakeFileClient(FakeGateway gateway, string workspace) : IFileClient
{
    public async ValueTask UploadAsync(string sandbox, string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (Directory.Exists(localPath))
        {
            throw new ArgumentException($"local path is a directory, not a file: {localPath}", nameof(localPath));
        }

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("local file not found", localPath);
        }

        _ = RemotePath.Split(remotePath);
        var content = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        Store(sandbox, remotePath, content, "Files.Upload", cancellationToken);
    }

    public async ValueTask UploadAsync(string sandbox, Stream content, string remotePath, UnixFileMode? mode = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(content);
        _ = RemotePath.Split(remotePath);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        Store(sandbox, remotePath, buffer.ToArray(), "Files.Upload", cancellationToken);
    }

    public async ValueTask UploadDirectoryAsync(string sandbox, string localDirectory, string remoteDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        RemotePath.ValidateDirectory(remoteDirectory);
        if (!Directory.Exists(localDirectory))
        {
            throw new DirectoryNotFoundException($"local directory not found: {localDirectory}");
        }

        var files = new List<(string Path, byte[] Content)>();
        var root = Path.GetFullPath(localDirectory);
        var remoteRoot = remoteDirectory.TrimEnd('/');
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files.Add(($"{remoteRoot}/{relative}", await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)));
        }

        gateway.Run("Files.UploadDirectory", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.RequireRunning(workspace, sandbox);
            gateway.StoreDirectory(entry, remoteRoot);
            foreach (var (path, content) in files)
            {
                gateway.StoreFile(entry, path, content);
            }
        });
    }

    public async ValueTask DownloadAsync(string sandbox, string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var content = Fetch(sandbox, remotePath, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        await File.WriteAllBytesAsync(localPath, content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DownloadAsync(string sandbox, string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(destination);
        var content = Fetch(sandbox, remotePath, cancellationToken);
        await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DownloadDirectoryAsync(string sandbox, string remoteDirectory, string localDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        RemotePath.ValidateDirectory(remoteDirectory);
        var remoteRoot = remoteDirectory.TrimEnd('/');
        var files = gateway.Run("Files.DownloadDirectory", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.RequireRunning(workspace, sandbox);
            var key = new Key(entry.Workspace, entry.Name);
            var matches = gateway.Files.TryGetValue(key, out var all)
                ? all.Where(f => f.Key.StartsWith(remoteRoot + "/", StringComparison.Ordinal)).Select(f => (f.Key, f.Value.ToArray())).ToList()
                : [];
            var known = gateway.Directories.TryGetValue(key, out var directories) && directories.Contains(remoteRoot);
            return matches.Count == 0 && !known
                ? throw new InvalidDataException($"remote command produced no directory for '{remoteDirectory}'")
                : matches;
        });

        var root = Path.GetFullPath(localDirectory);
        Directory.CreateDirectory(root);
        foreach (var (path, content) in files)
        {
            var relative = path[(remoteRoot.Length + 1)..];
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"unsafe tar entry name '{relative}'");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Store(string sandbox, string remotePath, byte[] content, string operation, CancellationToken cancellationToken) =>
        gateway.Run(operation, workspace, sandbox, cancellationToken, () => gateway.StoreFile(gateway.RequireRunning(workspace, sandbox), remotePath, content));

    private byte[] Fetch(string sandbox, string remotePath, CancellationToken cancellationToken)
    {
        _ = RemotePath.Split(remotePath);
        return gateway.Run("Files.Download", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.RequireRunning(workspace, sandbox);
            return gateway.Files.TryGetValue(new Key(entry.Workspace, entry.Name), out var files) && files.TryGetValue(remotePath, out var content)
                ? content.ToArray()
                : throw new InvalidDataException($"remote command produced no file for '{remotePath}'");
        });
    }
}
