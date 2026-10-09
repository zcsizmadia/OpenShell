// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class RootfsClientTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openshell-rootfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FakeCallInvoker Gateway(string uploadPath, ulong maxBytes, Action<Wire.BeginRootfsTarStagingRequest>? onRequest = null) =>
        new FakeCallInvoker().Unary<Wire.BeginRootfsTarStagingRequest, Wire.BeginRootfsTarStagingResponse>("BeginRootfsTarStaging", r =>
        {
            onRequest?.Invoke(r);
            return new Wire.BeginRootfsTarStagingResponse
            {
                StagingToken = "tok-1",
                UploadPath = uploadPath,
                MaxBytes = maxBytes,
                ExpirationTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddHours(1)),
            };
        });

    [Test]
    public async Task BeginStaging_sends_scope_name_and_size_and_maps_slot()
    {
        Wire.BeginRootfsTarStagingRequest? seen = null;
        using var client = ClientTests.NewClient(Gateway("/stage/a.tar", 1024, r => seen = r), "ws");

        var slot = await client.Rootfs.BeginStagingAsync("a.tar", 99);

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.FileName).IsEqualTo("a.tar");
        await Assert.That(seen.SizeBytes).IsEqualTo(99ul);
        await Assert.That(slot.StagingToken).IsEqualTo("tok-1");
        await Assert.That(slot.UploadPath).IsEqualTo("/stage/a.tar");
        await Assert.That(slot.MaxBytes).IsEqualTo(1024ul);
        await Assert.That(slot.ExpiresAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(1));
    }

    [Test]
    public async Task BeginStaging_without_expiry_has_null_expiry()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.BeginRootfsTarStagingRequest, Wire.BeginRootfsTarStagingResponse>(
            "BeginRootfsTarStaging", _ => new Wire.BeginRootfsTarStagingResponse { StagingToken = "t" });
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Rootfs.BeginStagingAsync("a.tar", 1)).ExpiresAt).IsNull();
    }

    [Test]
    public async Task Stage_copies_archive_to_upload_path_and_returns_token()
    {
        var dir = TempDir();
        try
        {
            var source = Path.Combine(dir, "root.tar");
            var target = Path.Combine(dir, "staged.tar");
            var payload = new byte[300_000];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(source, payload);
            Wire.BeginRootfsTarStagingRequest? seen = null;
            using var client = ClientTests.NewClient(Gateway(target, 0, r => seen = r));

            var token = await client.Rootfs.StageAsync(source);

            await Assert.That(token).IsEqualTo("tok-1");
            await Assert.That(seen!.FileName).IsEqualTo("root.tar");
            await Assert.That(seen.SizeBytes).IsEqualTo((ulong)payload.Length);
            await Assert.That(await File.ReadAllBytesAsync(target)).IsEquivalentTo(payload);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Stage_enforces_the_byte_limit_while_copying()
    {
        var dir = TempDir();
        try
        {
            var source = Path.Combine(dir, "big.tar");
            await File.WriteAllBytesAsync(source, new byte[1000]);
            using var client = ClientTests.NewClient(Gateway(Path.Combine(dir, "out.tar"), 100));

            var ex = await Assert.That(async () => await client.Rootfs.StageAsync(source)).Throws<OpenShellException>();

            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
            await Assert.That(ex.Message).Contains("exceeds");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Stage_validates_arguments_and_missing_file()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Rootfs.StageAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Rootfs.StageAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.tar"))).Throws<FileNotFoundException>();
        await Assert.That(async () => await client.Rootfs.BeginStagingAsync("", 1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task DriverConfig_creates_vm_section_with_token()
    {
        var json = JsonNode.Parse(RootfsDriverConfig.WithStagingToken("tok"))!;

        await Assert.That((string?)json["vm"]!["rootfs_tar_staging_token"]).IsEqualTo("tok");
    }

    [Test]
    public async Task DriverConfig_preserves_existing_settings_and_other_drivers()
    {
        var merged = RootfsDriverConfig.WithStagingToken("tok", """{"vm":{"cpus":4},"docker":{"x":1}}""");
        var json = JsonNode.Parse(merged)!;

        await Assert.That((int?)json["vm"]!["cpus"]).IsEqualTo(4);
        await Assert.That((int?)json["docker"]!["x"]).IsEqualTo(1);
        await Assert.That((string?)json["vm"]!["rootfs_tar_staging_token"]).IsEqualTo("tok");
    }

    [Test]
    public async Task DriverConfig_supports_a_custom_driver_key_and_null_section()
    {
        var json = JsonNode.Parse(RootfsDriverConfig.WithStagingToken("tok", """{"custom":null}""", "custom"))!;

        await Assert.That((string?)json["custom"]!["rootfs_tar_staging_token"]).IsEqualTo("tok");
    }

    [Test]
    public async Task DriverConfig_refuses_to_replace_an_existing_token()
    {
        var ex = await Assert.That(() => RootfsDriverConfig.WithStagingToken("new", """{"vm":{"rootfs_tar_staging_token":"stolen"}}"""))
            .Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("already sets");
    }

    [Test]
    [Arguments("[1,2]")]
    [Arguments("""{"vm":"not-an-object"}""")]
    public async Task DriverConfig_rejects_wrong_shapes(string baseJson)
    {
        var ex = await Assert.That(() => RootfsDriverConfig.WithStagingToken("tok", baseJson)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task DriverConfig_validates_arguments()
    {
        await Assert.That(() => RootfsDriverConfig.WithStagingToken("")).Throws<ArgumentException>();
        await Assert.That(() => RootfsDriverConfig.WithStagingToken("t", null, " ")).Throws<ArgumentException>();
    }
}
