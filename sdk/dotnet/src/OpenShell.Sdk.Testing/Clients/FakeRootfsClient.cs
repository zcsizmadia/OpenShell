// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    /// <summary>The bytes staged under a rootfs staging token, or null when the token is unknown or empty.</summary>
    public byte[]? GetStagedRootfs(string stagingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingToken);
        lock (Sync)
        {
            return StagedSlots.TryGetValue(stagingToken, out var slot) ? slot.Content?.ToArray() : null;
        }
    }
}

internal sealed class FakeRootfsClient(FakeGateway gateway, string workspace) : IRootfsClient
{
    public ValueTask<RootfsStagingSlot> BeginStagingAsync(string fileName, ulong sizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return ValueTask.FromResult(gateway.Run("Rootfs.BeginStaging", workspace, fileName, cancellationToken, () => Begin(fileName, sizeBytes)));
    }

    public async ValueTask<string> StageAsync(string tarPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tarPath);
        var info = new FileInfo(tarPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("rootfs tar not found", tarPath);
        }

        var slot = await BeginStagingAsync(info.Name, (ulong)info.Length, cancellationToken).ConfigureAwait(false);
        var content = await ReadLimitedAsync(tarPath, slot.MaxBytes, cancellationToken).ConfigureAwait(false);
        lock (gateway.Sync)
        {
            gateway.StagedSlots[slot.StagingToken].Content = content;
        }

        return slot.StagingToken;
    }

    /// <summary>Reads while counting, so an archive that grows after the size check still cannot exceed the limit. A limit of 0 disables it.</summary>
    internal static async ValueTask<byte[]> ReadLimitedAsync(string path, ulong limit, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (limit > 0 && (ulong)output.Length + (ulong)read > limit)
            {
                throw new OpenShellException(
                    OpenShellErrorKind.InvalidArgument, $"{Path.GetFileName(path)} exceeds the {limit.ToString(CultureInfo.InvariantCulture)} byte staging limit");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private RootfsStagingSlot Begin(string fileName, ulong sizeBytes)
    {
        gateway.RequireWorkspace(workspace);
        if (fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal) || fileName is "." or ".." || fileName.Contains('\0', StringComparison.Ordinal))
        {
            throw FakeGateway.Invalid("rootfs tar file name must be a base name without path separators or traversal");
        }

        var max = gateway.Options.RootfsMaxBytes;
        if (sizeBytes > max)
        {
            throw FakeGateway.Invalid($"rootfs tar of {sizeBytes.ToString(CultureInfo.InvariantCulture)} bytes exceeds the {max.ToString(CultureInfo.InvariantCulture)} byte driver limit");
        }

        var token = gateway.NextId("rootfs");
        gateway.StagedSlots[token] = new StagedRootfs { Token = token, FileName = fileName };
        return new RootfsStagingSlot(token, $"/fake/staging/{token}/{fileName}", max, gateway.Time.GetUtcNow() + TimeSpan.FromHours(1));
    }
}
