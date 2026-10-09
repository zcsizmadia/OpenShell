// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Testing;
using WireSandbox = Openshell.Sandbox.V1;
using WireV1 = Openshell.V1;

namespace OpenShell.Tests;

internal static class FakePolicyData
{
    public static WireSandbox.NetworkPolicyRule Rule(string name, string host = "example.com", uint port = 443)
    {
        var rule = new WireSandbox.NetworkPolicyRule { Name = name };
        rule.Endpoints.Add(new WireSandbox.NetworkEndpoint { Host = host, Port = port });
        return rule;
    }

    public static WireSandbox.SandboxPolicy Policy(params (string Name, WireSandbox.NetworkPolicyRule Rule)[] rules)
    {
        var policy = new WireSandbox.SandboxPolicy
        {
            Filesystem = new WireSandbox.FilesystemPolicy { IncludeWorkdir = true },
            Process = new WireSandbox.ProcessPolicy { RunAsUser = "sandbox" },
        };
        foreach (var (name, rule) in rules)
        {
            policy.NetworkPolicies[name] = rule;
        }

        return policy;
    }

    public static async Task<FakeOpenShellClient> ClientWithPolicySandboxAsync()
    {
        var client = FakeCoreTests.New();
        await client.Sandboxes.CreateAsync("a", new SandboxSpec { Image = "i", Policy = Policy() });
        return client;
    }
}

public class FakeConfigTests
{
    [Test]
    public async Task A_new_sandbox_has_a_first_policy_revision()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();

        var config = await client.Config.GetSandboxAsync("a");

        await Assert.That(config.PolicyVersion).IsEqualTo(1u);
        await Assert.That(config.PolicyHash.Length).IsEqualTo(64);
        await Assert.That(config.PolicySource).IsEqualTo(PolicySource.Sandbox);
        await Assert.That(config.GlobalPolicyVersion).IsEqualTo(0u);
        await Assert.That(config.Settings.Count).IsEqualTo(0);
        await Assert.That(config.Workspace).IsEqualTo("default");
        await Assert.That(config.PolicyValidationFailureMode).IsEqualTo("fail_closed");
        await Assert.That(config.Policy!.Filesystem.IncludeWorkdir).IsTrue();
        await Assert.That(config.ProviderEnvRevision).IsGreaterThan(0ul);
    }

    [Test]
    public async Task Network_policy_changes_create_new_versions()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var before = await client.Config.GetSandboxAsync("a");

        var result = await client.Config.SetPolicyAsync(
            "a",
            FakePolicyData.Policy(("web", FakePolicyData.Rule("web"))),
            new SetPolicyOptions { Annotations = new Dictionary<string, string> { ["why"] = "test" }, Wait = true, RequestId = "r" });
        var after = await client.Config.GetSandboxAsync("a");

        await Assert.That(result.Version).IsEqualTo(2u);
        await Assert.That(result.PolicyHash).IsNotEqualTo(before.PolicyHash);
        await Assert.That(result.Annotations["why"]).IsEqualTo("test");
        await Assert.That(after.Policy!.NetworkPolicies.ContainsKey("web")).IsTrue();
        await Assert.That(after.ConfigRevision).IsGreaterThan(before.ConfigRevision);
        await Assert.That((await client.Sandboxes.GetAsync("a")).CurrentPolicyVersion).IsEqualTo(2u);
        await Assert.That((await client.Sandboxes.GetAsync("a")).Annotations["why"]).IsEqualTo("test");
    }

    [Test]
    public async Task Static_policy_fields_cannot_change_after_creation()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var changed = FakePolicyData.Policy();
        changed.Filesystem.IncludeWorkdir = false;
        var landlock = FakePolicyData.Policy();
        landlock.Landlock = new WireSandbox.LandlockPolicy { Compatibility = "hard_requirement" };
        var process = FakePolicyData.Policy();
        process.Process.RunAsUser = "root";

        foreach (var policy in new[] { changed, landlock, process })
        {
            var ex = await Assert.That(async () => await client.Config.SetPolicyAsync("a", policy)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        }
    }

    [Test]
    public async Task A_sandbox_created_without_a_policy_compares_against_empty_static_fields()
    {
        using var client = FakeCoreTests.New();
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());

        await client.Config.SetPolicyAsync("a", new WireSandbox.SandboxPolicy());
        var ex = await Assert.That(async () => await client.Config.SetPolicyAsync("a", FakePolicyData.Policy())).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Expected_resource_versions_are_enforced()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var current = (await client.Sandboxes.GetAsync("a")).ResourceVersion;

        var stale = await Assert.That(async () => await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(), new SetPolicyOptions { ExpectedResourceVersion = current + 100 })).Throws<OpenShellException>();
        await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(), new SetPolicyOptions { ExpectedResourceVersion = current });

        await Assert.That(stale!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Merge_operations_edit_network_rules()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var add = new WireV1.PolicyMergeOperation { AddRule = new WireV1.AddNetworkRule { RuleName = "web", Rule = FakePolicyData.Rule("web") } };
        var extra = FakePolicyData.Rule("web", "other.com", 80);
        extra.Endpoints.Add(new WireSandbox.NetworkEndpoint { Host = "example.com", Port = 443 });
        var addTwo = new WireV1.PolicyMergeOperation { AddRule = new WireV1.AddNetworkRule { RuleName = "web", Rule = extra } };
        var removeEndpoint = new WireV1.PolicyMergeOperation { RemoveEndpoint = new WireV1.RemoveNetworkEndpoint { RuleName = "web", Host = "other.com", Port = 80 } };
        var removeRule = new WireV1.PolicyMergeOperation { RemoveRule = new WireV1.RemoveNetworkRule { RuleName = "web" } };

        await client.Config.MergePolicyAsync("a", [add]);
        await client.Config.MergePolicyAsync("a", [addTwo]);
        await client.Config.MergePolicyAsync("a", [removeEndpoint]);
        var withEndpoint = (await client.Config.GetSandboxAsync("a")).Policy!.NetworkPolicies["web"].Endpoints.Single();
        await client.Config.MergePolicyAsync("a", [removeRule]);

        await Assert.That(withEndpoint.Host).IsEqualTo("example.com");
        await Assert.That((await client.Config.GetSandboxAsync("a")).Policy!.NetworkPolicies.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Merge_operation_errors_are_typed()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        await client.Config.MergePolicyAsync("a", [new WireV1.PolicyMergeOperation { AddRule = new WireV1.AddNetworkRule { RuleName = "web", Rule = FakePolicyData.Rule("web") } }]);

        var noRule = await Assert.That(async () => await client.Config.MergePolicyAsync("a", [new WireV1.PolicyMergeOperation { RemoveRule = new WireV1.RemoveNetworkRule { RuleName = "x" } }])).Throws<OpenShellException>();
        var noRuleForEndpoint = await Assert.That(async () => await client.Config.MergePolicyAsync("a", [new WireV1.PolicyMergeOperation { RemoveEndpoint = new WireV1.RemoveNetworkEndpoint { RuleName = "x", Host = "h", Port = 1 } }])).Throws<OpenShellException>();
        var noEndpoint = await Assert.That(async () => await client.Config.MergePolicyAsync("a", [new WireV1.PolicyMergeOperation { RemoveEndpoint = new WireV1.RemoveNetworkEndpoint { RuleName = "web", Host = "h", Port = 1 } }])).Throws<OpenShellException>();
        var unsupported = await Assert.That(async () => await client.Config.MergePolicyAsync("a", [new WireV1.PolicyMergeOperation { AddDenyRules = new WireV1.AddDenyRules() }])).Throws<OpenShellException>();

        await Assert.That(noRule!.IsNotFound).IsTrue();
        await Assert.That(noRuleForEndpoint!.IsNotFound).IsTrue();
        await Assert.That(noEndpoint!.IsNotFound).IsTrue();
        await Assert.That(unsupported!.Kind).IsEqualTo(OpenShellErrorKind.Unimplemented);
    }

    [Test]
    public async Task Settings_resolve_with_global_taking_precedence()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();

        var set = await client.Config.SetSettingAsync("a", "log_level", "debug");
        await client.Config.SetSettingAsync("a", "flag", true);
        await client.Config.SetSettingAsync("a", "count", 7L);
        await client.Config.SetSettingAsync("a", "blob", SettingValue.FromBytes(new byte[] { 1, 2 }));
        await client.Config.SetGlobalSettingAsync("log_level", "info");
        var config = await client.Config.GetSandboxAsync("a");

        await Assert.That(set.SettingsRevision).IsGreaterThan(0ul);
        await Assert.That(config.Settings["log_level"]).IsEqualTo(new EffectiveSetting("info", SettingScope.Global));
        await Assert.That(config.Settings["flag"].Scope).IsEqualTo(SettingScope.Sandbox);
        await Assert.That(config.Settings["count"].Value).IsEqualTo(SettingValue.FromInt(7));
        await Assert.That(config.Settings["blob"].Value).IsEqualTo(SettingValue.FromBytes(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task Global_policy_applies_to_every_sandbox()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var global = FakePolicyData.Policy(("g", FakePolicyData.Rule("g")));

        var first = await client.Config.SetGlobalPolicyAsync(global, "r1");
        var second = await client.Config.SetGlobalPolicyAsync(global);
        var config = await client.Config.GetSandboxAsync("a");

        await Assert.That(first.Version).IsEqualTo(1u);
        await Assert.That(second.Version).IsEqualTo(2u);
        await Assert.That(config.PolicySource).IsEqualTo(PolicySource.Global);
        await Assert.That(config.GlobalPolicyVersion).IsEqualTo(2u);
        await Assert.That(config.Policy!.NetworkPolicies.ContainsKey("g")).IsTrue();
    }

    [Test]
    public async Task Global_settings_can_be_set_read_and_deleted()
    {
        using var client = FakeCoreTests.New();

        var set = await client.Config.SetGlobalSettingAsync("k", "v");
        var read = await client.Config.GetGatewayAsync();
        var deleted = await client.Config.DeleteGlobalSettingAsync("k");
        var again = await client.Config.DeleteGlobalSettingAsync("k");

        await Assert.That(read.Settings["k"]).IsEqualTo(SettingValue.FromString("v"));
        await Assert.That(read.SettingsRevision).IsEqualTo(set.SettingsRevision);
        await Assert.That(deleted.Deleted).IsTrue();
        await Assert.That(again.Deleted).IsFalse();
        await Assert.That((await client.Config.GetGatewayAsync()).Settings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Update_requests_are_validated()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var policy = FakePolicyData.Policy();
        var op = new WireV1.PolicyMergeOperation { RemoveRule = new WireV1.RemoveNetworkRule { RuleName = "x" } };

        async Task<OpenShellException> Reject(ConfigUpdate update)
        {
            return (await Assert.That(async () => await client.Config.UpdateAsync(update)).Throws<OpenShellException>())!;
        }

        await Assert.That((await Reject(new ConfigUpdate { Sandbox = "a" })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Sandbox = "a", Policy = policy, SettingKey = "k" })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Policy = policy })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Global = true, Sandbox = "a", Policy = policy })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Global = true, MergeOperations = [op] })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Sandbox = "a", SettingKey = "k", DeleteSetting = true })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Sandbox = "a", SettingKey = "k" })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Global = true, SettingKey = "k" })).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(new ConfigUpdate { Sandbox = "nope", Policy = policy })).IsNotFound).IsTrue();
        await Assert.That(async () => await client.Config.UpdateAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Helper_arguments_are_validated()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var policy = FakePolicyData.Policy();

        await Assert.That(async () => await client.Config.GetSandboxAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.GetSandboxAsync("nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Config.SetPolicyAsync(" ", policy)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetPolicyAsync("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.MergePolicyAsync(" ", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.MergePolicyAsync("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.SetSettingAsync(" ", "k", "v")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetSettingAsync("a", " ", "v")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetSettingAsync("a", "k", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.SetGlobalPolicyAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.SetGlobalSettingAsync(" ", "v")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Config.SetGlobalSettingAsync("k", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Config.DeleteGlobalSettingAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Policy_load_status_can_be_scripted()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(("web", FakePolicyData.Rule("web"))));

        client.Gateway.SetPolicyLoadStatus("a", 2, PolicyLoadStatus.Failed, "bad rule");
        var failed = await client.Policy.GetStatusAsync("a", new PolicyStatusOptions { Version = 2 });
        client.Gateway.SetPolicyLoadStatus("a", 2, PolicyLoadStatus.Loaded);
        var loaded = await client.Policy.GetStatusAsync("a");

        await Assert.That(failed.Revision!.Status).IsEqualTo(PolicyLoadStatus.Failed);
        await Assert.That(failed.Revision.LoadError).IsEqualTo("bad rule");
        await Assert.That(failed.Revision.LoadedAt).IsNull();
        await Assert.That(failed.ActiveVersion).IsEqualTo(1u);
        await Assert.That(loaded.ActiveVersion).IsEqualTo(2u);
        await Assert.That(() => client.Gateway.SetPolicyLoadStatus(" ", 1, PolicyLoadStatus.Failed)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.SetPolicyLoadStatus("nope", 1, PolicyLoadStatus.Failed)).Throws<OpenShellException>();
        await Assert.That(() => client.Gateway.SetPolicyLoadStatus("a", 99, PolicyLoadStatus.Failed)).Throws<OpenShellException>();
    }
}

public class FakePolicyTests
{
    private static async Task<(FakeOpenShellClient Client, string ChunkId)> WithChunkAsync(string notes = "")
    {
        var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var id = client.Gateway.ProposeChunk("a", "web", FakePolicyData.Rule("web"), "needed", notes, "/usr/bin/curl");
        return (client, id);
    }

    [Test]
    public async Task Drafts_list_chunks_with_review_tokens_and_candidate_policies()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;

        var draft = await client.Policy.GetDraftAsync("a");
        var pending = await client.Policy.GetDraftAsync("a", "PENDING");
        var approved = await client.Policy.GetDraftAsync("a", "approved");
        var chunk = draft.Chunks.Single();

        await Assert.That(draft.DraftVersion).IsGreaterThan(0ul);
        await Assert.That(chunk.Id).IsEqualTo(id);
        await Assert.That(chunk.Status).IsEqualTo("pending");
        await Assert.That(chunk.RuleName).IsEqualTo("web");
        await Assert.That(chunk.Binary).IsEqualTo("/usr/bin/curl");
        await Assert.That(chunk.Rationale).IsEqualTo("needed");
        await Assert.That(chunk.ReviewToken.Length).IsGreaterThan(0);
        await Assert.That(chunk.CandidateEffectivePolicy!.NetworkPolicies.ContainsKey("web")).IsTrue();
        await Assert.That(chunk.CurrentEffectivePolicyHash).IsNotEqualTo(chunk.CandidateEffectivePolicyHash);
        await Assert.That(pending.Chunks.Count).IsEqualTo(1);
        await Assert.That(approved.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Approving_a_chunk_applies_its_rule_once()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;
        var token = (await client.Policy.GetDraftAsync("a")).Chunks.Single().ReviewToken;

        var change = await client.Policy.ApproveChunkAsync("a", id, token);
        var config = await client.Config.GetSandboxAsync("a");
        var again = await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", id, token)).Throws<OpenShellException>();

        await Assert.That(change.PolicyVersion).IsEqualTo(2u);
        await Assert.That(config.Policy!.NetworkPolicies.ContainsKey("web")).IsTrue();
        await Assert.That(again!.IsConflict).IsTrue();
        await Assert.That((await client.Policy.GetDraftHistoryAsync("a")).Single().EventType).IsEqualTo("approved");
    }

    [Test]
    public async Task A_stale_review_token_is_rejected()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;
        var stale = (await client.Policy.GetDraftAsync("a")).Chunks.Single().ReviewToken;
        await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(("other", FakePolicyData.Rule("other"))));

        var ex = await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", id, stale)).Throws<OpenShellException>();

        await Assert.That(ex!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Rejecting_records_the_reason_and_blocks_further_decisions()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;

        await client.Policy.RejectChunkAsync("a", id, "too broad");
        var chunk = (await client.Policy.GetDraftAsync("a")).Chunks.Single();
        var again = await Assert.That(async () => await client.Policy.RejectChunkAsync("a", id)).Throws<OpenShellException>();

        await Assert.That(chunk.Status).IsEqualTo("rejected");
        await Assert.That(chunk.RejectionReason).IsEqualTo("too broad");
        await Assert.That(chunk.DecidedAt).IsNotNull();
        await Assert.That(again!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Editing_replaces_the_rule_and_invalidates_the_old_token()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;
        var oldToken = (await client.Policy.GetDraftAsync("a")).Chunks.Single().ReviewToken;

        await client.Policy.EditChunkAsync("a", id, FakePolicyData.Rule("web", "narrow.example.com", 8443));
        var edited = (await client.Policy.GetDraftAsync("a")).Chunks.Single();
        var stale = await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", id, oldToken)).Throws<OpenShellException>();

        await Assert.That(edited.ProposedRule!.Endpoints.Single().Host).IsEqualTo("narrow.example.com");
        await Assert.That(edited.ReviewToken).IsNotEqualTo(oldToken);
        await Assert.That(stale!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Undo_reverts_an_approved_chunk_to_pending()
    {
        var (client, id) = await WithChunkAsync();
        using var _ = client;
        await client.Policy.ApproveChunkAsync("a", id, (await client.Policy.GetDraftAsync("a")).Chunks.Single().ReviewToken);

        var undone = await client.Policy.UndoChunkAsync("a", id);
        var chunk = (await client.Policy.GetDraftAsync("a")).Chunks.Single();
        var notApproved = await Assert.That(async () => await client.Policy.UndoChunkAsync("a", id)).Throws<OpenShellException>();

        await Assert.That(undone.PolicyVersion).IsEqualTo(3u);
        await Assert.That(chunk.Status).IsEqualTo("pending");
        await Assert.That(chunk.DecidedAt).IsNull();
        await Assert.That((await client.Config.GetSandboxAsync("a")).Policy!.NetworkPolicies.Count).IsEqualTo(0);
        await Assert.That(notApproved!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Clearing_removes_every_chunk_and_logs_it()
    {
        var (client, _) = await WithChunkAsync();
        using var client2 = client;
        client.Gateway.ProposeChunk("a", "second", FakePolicyData.Rule("second"));

        var cleared = await client.Policy.ClearChunksAsync("a");
        var history = await client.Policy.GetDraftHistoryAsync("a");

        await Assert.That(cleared).IsEqualTo(2u);
        await Assert.That((await client.Policy.GetDraftAsync("a")).Chunks.Count).IsEqualTo(0);
        await Assert.That(history.Single().EventType).IsEqualTo("cleared");
    }

    [Test]
    public async Task Approve_all_skips_security_flagged_chunks_unless_asked()
    {
        var (client, _) = await WithChunkAsync();
        using var _ = client;
        client.Gateway.ProposeChunk("a", "risky", FakePolicyData.Rule("risky"), securityNotes: "wide open");

        var safeOnly = await client.Policy.ApproveAllAsync("a");
        var rest = await client.Policy.ApproveAllAsync("a", new ApproveAllOptions { IncludeSecurityFlagged = true });
        var nothing = await client.Policy.ApproveAllAsync("a");

        await Assert.That(safeOnly.ChunksApproved).IsEqualTo(1u);
        await Assert.That(safeOnly.ChunksSkipped).IsEqualTo(1u);
        await Assert.That(rest.ChunksApproved).IsEqualTo(1u);
        await Assert.That(rest.PolicyVersion).IsGreaterThan(safeOnly.PolicyVersion);
        await Assert.That(nothing.ChunksApproved).IsEqualTo(0u);
        await Assert.That(nothing.PolicyHash.Length).IsEqualTo(64);
    }

    [Test]
    public async Task Approve_all_with_explicit_approvals_requires_matching_tokens()
    {
        var (client, firstId) = await WithChunkAsync();
        using var _ = client;
        var secondId = client.Gateway.ProposeChunk("a", "second", FakePolicyData.Rule("second"));
        var chunks = (await client.Policy.GetDraftAsync("a")).Chunks;
        var firstToken = chunks.Single(c => c.Id == firstId).ReviewToken;

        var result = await client.Policy.ApproveAllAsync(
            "a",
            new ApproveAllOptions { Approvals = [new ChunkApproval(firstId, firstToken), new ChunkApproval(secondId, "wrong-token")] });

        await Assert.That(result.ChunksApproved).IsEqualTo(1u);
        await Assert.That(result.ChunksSkipped).IsEqualTo(1u);
        await Assert.That((await client.Policy.GetDraftAsync("a", "pending")).Chunks.Single().Id).IsEqualTo(secondId);
    }

    [Test]
    public async Task Unknown_chunks_and_sandboxes_are_not_found()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();

        await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", "nope", "t")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.RejectChunkAsync("a", "nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.EditChunkAsync("a", "nope", FakePolicyData.Rule("r"))).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.UndoChunkAsync("a", "nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.GetDraftAsync("nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.ClearChunksAsync("nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.GetDraftHistoryAsync("nope")).Throws<OpenShellException>();
        await Assert.That(async () => await client.Policy.ApproveAllAsync("nope")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Policy_arguments_are_validated()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        var rule = FakePolicyData.Rule("r");

        await Assert.That(async () => await client.Policy.GetDraftAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.ApproveChunkAsync(" ", "c", "t")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", " ", "t")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.ApproveChunkAsync("a", "c", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.RejectChunkAsync(" ", "c")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.RejectChunkAsync("a", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.ApproveAllAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.EditChunkAsync(" ", "c", rule)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.EditChunkAsync("a", " ", rule)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.EditChunkAsync("a", "c", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Policy.UndoChunkAsync(" ", "c")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.UndoChunkAsync("a", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.ClearChunksAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.GetDraftHistoryAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Policy.GetStatusAsync(" ")).Throws<ArgumentException>();
        await Assert.That(() => client.Policy.ListRevisions(" ")).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.ProposeChunk(" ", "r", rule)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.ProposeChunk("a", " ", rule)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.ProposeChunk("a", "r", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Gateway.ProposeChunk("nope", "r", rule)).Throws<OpenShellException>();
    }

    [Test]
    public async Task Status_and_revisions_describe_the_policy_history()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();
        await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(("web", FakePolicyData.Rule("web"))), new SetPolicyOptions { Annotations = new Dictionary<string, string> { ["by"] = "test" } });
        await client.Config.SetPolicyAsync("a", FakePolicyData.Policy(("db", FakePolicyData.Rule("db"))));

        var latest = await client.Policy.GetStatusAsync("a");
        var second = await client.Policy.GetStatusAsync("a", new PolicyStatusOptions { Version = 2 });
        var revisions = await client.Policy.ListRevisions("a", new PolicyListOptions { PageSize = 2 }).ToListAsync();
        var missing = await Assert.That(async () => await client.Policy.GetStatusAsync("a", new PolicyStatusOptions { Version = 9 })).Throws<OpenShellException>();

        await Assert.That(latest.Revision!.Version).IsEqualTo(3u);
        await Assert.That(latest.ActiveVersion).IsEqualTo(3u);
        await Assert.That(latest.Revision.Policy).IsNotNull();
        await Assert.That(second.Revision!.Provenance["by"]).IsEqualTo("test");
        await Assert.That(revisions.Select(r => r.Version)).IsEquivalentTo(new[] { 3u, 2u, 1u });
        await Assert.That(revisions[0].Policy).IsNull();
        await Assert.That(missing!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Global_revisions_are_reported_separately()
    {
        using var client = await FakePolicyData.ClientWithPolicySandboxAsync();

        var empty = await client.Policy.GetStatusAsync("a", new PolicyStatusOptions { Global = true });
        await client.Config.SetGlobalPolicyAsync(FakePolicyData.Policy(("g", FakePolicyData.Rule("g"))));
        var status = await client.Policy.GetStatusAsync("a", new PolicyStatusOptions { Global = true });
        var listed = await client.Policy.ListRevisions("a", new PolicyListOptions { Global = true }).ToListAsync();

        await Assert.That(empty.Revision).IsNull();
        await Assert.That(empty.ActiveVersion).IsEqualTo(0u);
        await Assert.That(status.Revision!.Version).IsEqualTo(1u);
        await Assert.That(listed.Count).IsEqualTo(1);
    }
}
