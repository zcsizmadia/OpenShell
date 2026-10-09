// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Tests;

public class SettingValueTests
{
    [Test]
    public async Task Factories_and_implicit_conversions_create_typed_values()
    {
        SettingValue s = "text";
        SettingValue b = true;
        SettingValue i = 42L;

        await Assert.That(s).IsEqualTo(new StringSetting("text"));
        await Assert.That(b).IsEqualTo(new BoolSetting(true));
        await Assert.That(i).IsEqualTo(new IntSetting(42));
        await Assert.That(SettingValue.FromString("x")).IsEqualTo(new StringSetting("x"));
        await Assert.That(SettingValue.FromBool(false)).IsEqualTo(new BoolSetting(false));
        await Assert.That(SettingValue.FromInt(-1)).IsEqualTo(new IntSetting(-1));
        await Assert.That(() => SettingValue.FromString(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Bytes_are_copied_and_compared_by_content()
    {
        var source = new byte[] { 1, 2, 3 };
        var value = (BytesSetting)SettingValue.FromBytes(source);
        await Assert.That(value).IsNotNull();
        source[0] = 9;

        await Assert.That(value.Value[0]).IsEqualTo((byte)1);
        await Assert.That(value).IsEqualTo((BytesSetting)SettingValue.FromBytes(new byte[] { 1, 2, 3 }));
        await Assert.That(value.Equals((BytesSetting)SettingValue.FromBytes(new byte[] { 1, 2, 4 }))).IsFalse();
        await Assert.That(value.Equals(null)).IsFalse();
        await Assert.That(value.GetHashCode()).IsEqualTo(((BytesSetting)SettingValue.FromBytes(new byte[] { 1, 2, 3 })).GetHashCode());
    }

    [Test]
    public async Task Wire_roundtrip_preserves_every_kind()
    {
        SettingValue[] values = ["s", true, 7L, SettingValue.FromBytes(new byte[] { 5, 6 })];
        foreach (var value in values)
        {
            await Assert.That(SettingValue.FromWire(value.ToWire())).IsEqualTo(value);
        }
    }

    [Test]
    public async Task Empty_or_missing_wire_value_is_null()
    {
        await Assert.That(SettingValue.FromWire(new WireSandbox.SettingValue())).IsNull();
        await Assert.That(SettingValue.FromWire(null)).IsNull();
    }
}

public class ConfigClientTests
{
    private static WireSandbox.GetSandboxConfigResponse ConfigReply(string hash = "h1")
    {
        var reply = new WireSandbox.GetSandboxConfigResponse
        {
            Policy = new WireSandbox.SandboxPolicy { Version = 3 },
            Version = 3,
            PolicyHash = hash,
            ConfigRevision = 11,
            PolicySource = WireSandbox.PolicySource.Global,
            GlobalPolicyVersion = 2,
            ProviderEnvRevision = 5,
            PolicyValidationFailureMode = "fail_closed",
            Workspace = "ws",
        };
        reply.Settings["flag"] = new WireSandbox.EffectiveSetting { Value = new WireSandbox.SettingValue { BoolValue = true }, Scope = WireSandbox.SettingScope.Sandbox };
        reply.Settings["unset"] = new WireSandbox.EffectiveSetting { Scope = WireSandbox.SettingScope.Global };
        return reply;
    }

    [Test]
    public async Task GetSandbox_maps_policy_settings_and_revisions()
    {
        WireSandbox.GetSandboxConfigRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", r =>
        {
            seen = r;
            return ConfigReply();
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var config = await client.Config.GetSandboxAsync("sb");

        await Assert.That(seen!.Name).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(config.Policy!.Version).IsEqualTo(3u);
        await Assert.That(config.PolicyVersion).IsEqualTo(3u);
        await Assert.That(config.PolicyHash).IsEqualTo("h1");
        await Assert.That(config.ConfigRevision).IsEqualTo(11ul);
        await Assert.That(config.PolicySource).IsEqualTo(PolicySource.Global);
        await Assert.That(config.GlobalPolicyVersion).IsEqualTo(2u);
        await Assert.That(config.ProviderEnvRevision).IsEqualTo(5ul);
        await Assert.That(config.PolicyValidationFailureMode).IsEqualTo("fail_closed");
        await Assert.That(config.Workspace).IsEqualTo("ws");
        await Assert.That(config.Settings["flag"]).IsEqualTo(new EffectiveSetting(new BoolSetting(true), SettingScope.Sandbox));
        await Assert.That(config.Settings["unset"].Value).IsNull();
        await Assert.That(config.Settings["unset"].Scope).IsEqualTo(SettingScope.Global);
    }

    [Test]
    public async Task GetGateway_maps_settings_including_unset_keys()
    {
        var reply = new WireSandbox.GetGatewayConfigResponse { SettingsRevision = 8 };
        reply.Settings["a"] = new WireSandbox.SettingValue { StringValue = "x" };
        reply.Settings["b"] = new WireSandbox.SettingValue();
        var invoker = new FakeCallInvoker().Unary<WireSandbox.GetGatewayConfigRequest, WireSandbox.GetGatewayConfigResponse>("GetGatewayConfig", _ => reply);
        using var client = ClientTests.NewClient(invoker);

        var config = await client.Config.GetGatewayAsync();

        await Assert.That(config.SettingsRevision).IsEqualTo(8ul);
        await Assert.That(config.Settings["a"]).IsEqualTo(new StringSetting("x"));
        await Assert.That(config.Settings["b"]).IsNull();
    }

    [Test]
    public async Task SetPolicy_sends_sandbox_scoped_update_with_options()
    {
        Wire.UpdateConfigRequest? seen = null;
        var response = new Wire.UpdateConfigResponse { Version = 4, PolicyHash = "h2", SettingsRevision = 6, Deleted = false };
        response.Annotations["k"] = "v";
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", r =>
        {
            seen = r;
            return response;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var result = await client.Config.SetPolicyAsync(
            "sb",
            new WireSandbox.SandboxPolicy { Version = 9 },
            new SetPolicyOptions
            {
                ExpectedResourceVersion = 12,
                Annotations = new Dictionary<string, string> { ["by"] = "ci" },
                RequestId = "rid",
            });

        await Assert.That(seen!.Global).IsFalse();
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Policy.Version).IsEqualTo(9u);
        await Assert.That(seen.ExpectedResourceVersion).IsEqualTo(12ul);
        await Assert.That(seen.Annotations["by"]).IsEqualTo("ci");
        await Assert.That(seen.RequestId).IsEqualTo("rid");
        await Assert.That(result.Version).IsEqualTo(4u);
        await Assert.That(result.PolicyHash).IsEqualTo("h2");
        await Assert.That(result.SettingsRevision).IsEqualTo(6ul);
        await Assert.That(result.Annotations["k"]).IsEqualTo("v");
    }

    [Test]
    public async Task SetPolicy_wait_polls_until_hash_is_observed()
    {
        var time = new FakeTimeProvider();
        var hashes = new Queue<string>(["old", "old", "h2"]);
        var invoker = new FakeCallInvoker()
            .Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", _ => new Wire.UpdateConfigResponse { PolicyHash = "h2" })
            .Unary<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", _ => ConfigReply(hashes.Dequeue()));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Config.SetPolicyAsync("sb", new WireSandbox.SandboxPolicy(), new SetPolicyOptions { Wait = true }).AsTask();
        await AdvanceUntilDone(time, task);

        await Assert.That((await task).PolicyHash).IsEqualTo("h2");
        await Assert.That(invoker.CountOf("GetSandboxConfig")).IsEqualTo(3);
    }

    [Test]
    public async Task SetPolicy_wait_times_out_with_deadline_exceeded()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker()
            .Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", _ => new Wire.UpdateConfigResponse { PolicyHash = "h2" })
            .Unary<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", _ => ConfigReply("never"));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Config.SetPolicyAsync(
            "sb", new WireSandbox.SandboxPolicy(), new SetPolicyOptions { Wait = true, WaitTimeout = TimeSpan.FromSeconds(2) }).AsTask();
        await AdvanceUntilDone(time, task);

        var ex = await Assert.That(async () => await task).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
        await Assert.That(ex.Message).Contains("h2");
    }

    [Test]
    public async Task SetPolicy_wait_reports_deadline_that_fires_during_a_poll()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker()
            .Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", _ => new Wire.UpdateConfigResponse { PolicyHash = "h2" })
            .Unary<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", (_, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                throw new RpcException(new Status(StatusCode.Cancelled, "deadline"));
            });
        using var client = ClientTests.NewClient(invoker, time: time);

        var ex = await Assert.That(async () => await client.Config.SetPolicyAsync(
            "sb", new WireSandbox.SandboxPolicy(), new SetPolicyOptions { Wait = true, WaitTimeout = TimeSpan.FromSeconds(5) })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
    }

    [Test]
    public async Task SetPolicy_wait_propagates_gateway_errors()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", _ => new Wire.UpdateConfigResponse { PolicyHash = "h2" })
            .Throws<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", StatusCode.NotFound, "gone");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Config.SetPolicyAsync("sb", new WireSandbox.SandboxPolicy(), new SetPolicyOptions { Wait = true }))
            .Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task SetPolicy_wait_honors_caller_cancellation()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", _ => new Wire.UpdateConfigResponse { PolicyHash = "h2" })
            .Unary<WireSandbox.GetSandboxConfigRequest, WireSandbox.GetSandboxConfigResponse>("GetSandboxConfig", _ => ConfigReply("never"));
        using var client = ClientTests.NewClient(invoker, time: new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var task = client.Config.SetPolicyAsync("sb", new WireSandbox.SandboxPolicy(), new SetPolicyOptions { Wait = true }, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task MergePolicy_sends_operations_and_validates()
    {
        Wire.UpdateConfigRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", r =>
        {
            seen = r;
            return new Wire.UpdateConfigResponse { PolicyHash = "h" };
        });
        using var client = ClientTests.NewClient(invoker);
        var op = new Wire.PolicyMergeOperation { RemoveRule = new Wire.RemoveNetworkRule { RuleName = "r" } };

        await client.Config.MergePolicyAsync("sb", [op]);

        await Assert.That(seen!.MergeOperations.Single().RemoveRule.RuleName).IsEqualTo("r");
        await Assert.That(async () => await client.Config.MergePolicyAsync("sb", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.MergePolicyAsync("sb", null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SetSetting_upserts_at_sandbox_scope()
    {
        Wire.UpdateConfigRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", r =>
        {
            seen = r;
            return new Wire.UpdateConfigResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Config.SetSettingAsync("sb", "level", "debug");

        await Assert.That(seen!.Global).IsFalse();
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.SettingKey).IsEqualTo("level");
        await Assert.That(seen.SettingValue.StringValue).IsEqualTo("debug");
        await Assert.That(seen.DeleteSetting).IsFalse();
        await Assert.That(async () => await client.Config.SetSettingAsync("sb", " ", "x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetSettingAsync("sb", "k", null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Global_updates_omit_workspace_and_sandbox()
    {
        var seen = new List<Wire.UpdateConfigRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", r =>
        {
            seen.Add(r);
            return new Wire.UpdateConfigResponse { Deleted = r.DeleteSetting };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Config.SetGlobalPolicyAsync(new WireSandbox.SandboxPolicy { Version = 2 }, "rid");
        await client.Config.SetGlobalSettingAsync("k", 5L);
        var deleted = await client.Config.DeleteGlobalSettingAsync("k");

        await Assert.That(seen.All(r => r.Global && r.Sandbox == "" && r.WorkspaceScope is null)).IsTrue();
        await Assert.That(seen[0].Policy.Version).IsEqualTo(2u);
        await Assert.That(seen[0].RequestId).IsEqualTo("rid");
        await Assert.That(seen[1].SettingValue.IntValue).IsEqualTo(5L);
        await Assert.That(seen[2].DeleteSetting).IsTrue();
        await Assert.That(deleted.Deleted).IsTrue();
        await Assert.That(async () => await client.Config.SetGlobalPolicyAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.SetGlobalSettingAsync("", "x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetGlobalSettingAsync("k", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.DeleteGlobalSettingAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Update_validates_scope_and_setting_arguments()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Config.UpdateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.UpdateAsync(new ConfigUpdate { Global = true, Sandbox = "sb" })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.UpdateAsync(new ConfigUpdate())).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.UpdateAsync(new ConfigUpdate { Global = true, SettingValue = "x" })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.UpdateAsync(new ConfigUpdate { Global = true, DeleteSetting = true })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.GetSandboxAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetPolicyAsync("sb", null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Update_passes_every_field_and_maps_gateway_conflicts()
    {
        Wire.UpdateConfigRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", r =>
        {
            seen = r;
            throw new RpcException(new Status(StatusCode.Aborted, "version mismatch"));
        });
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Config.UpdateAsync(new ConfigUpdate
        {
            Sandbox = "sb",
            SettingKey = "k",
            SettingValue = SettingValue.FromBytes(new byte[] { 1 }),
            ExpectedResourceVersion = 3,
            Annotations = new Dictionary<string, string> { ["a"] = "b" },
            RequestId = "r",
            MergeOperations = [new Wire.PolicyMergeOperation()],
            Policy = new WireSandbox.SandboxPolicy(),
        })).Throws<OpenShellException>();

        await Assert.That(ex!.IsConflict).IsTrue();
        await Assert.That(seen!.SettingValue.BytesValue.ToByteArray()).IsEquivalentTo(new byte[] { 1 });
        await Assert.That(seen.MergeOperations.Count).IsEqualTo(1);
    }

    private static async Task AdvanceUntilDone(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }
}
