// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>A gateway-owned slot for a local rootfs tar archive.</summary>
/// <param name="StagingToken">Opaque single-use token; the first create that presents it consumes it.</param>
/// <param name="UploadPath">Absolute path on the gateway host the archive must be written to.</param>
/// <param name="MaxBytes">Maximum accepted archive size; 0 means the gateway did not report a limit.</param>
/// <param name="ExpiresAt">When the gateway reclaims the slot.</param>
public sealed record RootfsStagingSlot(string StagingToken, string UploadPath, ulong MaxBytes, DateTimeOffset? ExpiresAt);

/// <summary>
/// Stages a local rootfs tar archive for compute drivers that boot from one (for example the VM driver).
/// Only a client that shares the gateway's filesystem can complete the upload, so this is a local-gateway workflow.
/// </summary>
public interface IRootfsClient
{
    /// <summary>
    /// Allocates a staging slot. The gateway validates <paramref name="fileName"/> (a base name, no separators)
    /// and rejects archives over the driver limit before allocating anything.
    /// </summary>
    ValueTask<RootfsStagingSlot> BeginStagingAsync(string fileName, ulong sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates a slot, copies <paramref name="tarPath"/> into it while enforcing the gateway's byte limit,
    /// and returns the token to pass to <see cref="RootfsDriverConfig.WithStagingToken"/>.
    /// </summary>
    ValueTask<string> StageAsync(string tarPath, CancellationToken cancellationToken = default);
}

/// <summary>Builds the <c>template.driver_config</c> JSON that references a staged rootfs archive.</summary>
public static class RootfsDriverConfig
{
    /// <summary>The driver key that receives the token. Only <c>template.driver_config.&lt;driver&gt;</c> reaches the driver.</summary>
    public const string VmDriver = "vm";

    /// <summary>The driver-config field naming the staging slot.</summary>
    public const string TokenField = "rootfs_tar_staging_token";

    /// <summary>
    /// Merges the token into <c>driver_config.&lt;driver&gt;</c>, keeping any settings already in <paramref name="baseJson"/>.
    /// Throws when the caller already set a token, so a stolen or stale one is never silently replaced.
    /// </summary>
    public static string WithStagingToken(string stagingToken, string? baseJson = null, string driver = VmDriver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);

        JsonObject root = string.IsNullOrWhiteSpace(baseJson)
            ? []
            : JsonNode.Parse(baseJson) as JsonObject
                ?? throw new OpenShellException(OpenShellErrorKind.InvalidArgument, "driver config must be a JSON object");

        if (!root.TryGetPropertyValue(driver, out var section) || section is null)
        {
            section = new JsonObject();
            root[driver] = section;
        }

        var driverConfig = section as JsonObject
            ?? throw new OpenShellException(OpenShellErrorKind.InvalidArgument, $"driver config '{driver}' must be an object");
        if (driverConfig.ContainsKey(TokenField))
        {
            throw new OpenShellException(
                OpenShellErrorKind.InvalidArgument, $"driver config already sets {driver}.{TokenField}; remove it or drop the staged rootfs");
        }

        driverConfig[TokenField] = stagingToken;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

internal sealed class RootfsClient(ClientCore core, string workspace) : IRootfsClient
{
    private const int CopyBufferSize = 80 * 1024;

    public async ValueTask<RootfsStagingSlot> BeginStagingAsync(string fileName, ulong sizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var request = new Wire.BeginRootfsTarStagingRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            FileName = fileName,
            SizeBytes = sizeBytes,
        };
        var r = await core.UnaryAsync(o => core.Grpc.BeginRootfsTarStagingAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new RootfsStagingSlot(r.StagingToken, r.UploadPath, r.MaxBytes, r.ExpirationTime?.ToDateTimeOffset());
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
        await CopyWithLimitAsync(tarPath, slot.UploadPath, slot.MaxBytes, cancellationToken).ConfigureAwait(false);
        return slot.StagingToken;
    }

    /// <summary>Copies while counting, so an archive that grows after the size check still cannot exceed the limit.</summary>
    internal static async ValueTask CopyWithLimitAsync(string source, string destination, ulong limit, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        var buffer = new byte[CopyBufferSize];
        ulong total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += (ulong)read;
            if (limit > 0 && total > limit)
            {
                throw new OpenShellException(
                    OpenShellErrorKind.InvalidArgument, $"{Path.GetFileName(source)} exceeds the {limit} byte staging limit");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
