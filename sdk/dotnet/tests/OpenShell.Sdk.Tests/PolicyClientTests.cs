// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Tests;

public class PolicyClientTests
{
    private static Timestamp At(int days) => Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(days));

    [Test]
    public async Task GetDraft_maps_chunks_and_summary()
    {
        Wire.GetDraftPolicyRequest? seen = null;
        var chunk = new Wire.PolicyChunk
        {
            Id = "c1",
            Status = "pending",
            RuleName = "allow-pypi",
            ProposedRule = new WireSandbox.NetworkPolicyRule(),
            Rationale = "why",
            SecurityNotes = "careful",
            Confidence = 0.5f,
            DenialSummaryIds = { "d1", "d2" },
            CreatedTime = At(1),
            DecidedTime = At(2),
            Stage = "refined",
            SupersedesChunkId = "c0",
            HitCount = 7,
            FirstSeenTime = At(3),
            LastSeenTime = At(4),
            Binary = "/usr/bin/pip",
            ValidationResult = "ok",
            RejectionReason = "no",
            ApplicationError = "conflict",
            ReviewToken = "tok",
            CurrentEffectivePolicyHash = "a",
            CandidateEffectivePolicyHash = "b",
            CurrentEffectivePolicy = new WireSandbox.SandboxPolicy { Version = 1 },
            CandidateEffectivePolicy = new WireSandbox.SandboxPolicy { Version = 2 },
        };
        var reply = new Wire.GetDraftPolicyResponse { RollingSummary = "sum", DraftVersion = 5, LastAnalyzedTime = At(9) };
        reply.Chunks.Add(chunk);
        var invoker = new FakeCallInvoker().Unary<Wire.GetDraftPolicyRequest, Wire.GetDraftPolicyResponse>("GetDraftPolicy", r =>
        {
            seen = r;
            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var draft = await client.Policy.GetDraftAsync("sb", "pending");

        await Assert.That(seen!.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.StatusFilter).IsEqualTo("pending");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(draft.RollingSummary).IsEqualTo("sum");
        await Assert.That(draft.DraftVersion).IsEqualTo(5ul);
        await Assert.That(draft.LastAnalyzedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(9));
        var c = draft.Chunks.Single();
        await Assert.That(c.Id).IsEqualTo("c1");
        await Assert.That(c.Status).IsEqualTo("pending");
        await Assert.That(c.RuleName).IsEqualTo("allow-pypi");
        await Assert.That(c.ProposedRule).IsNotNull();
        await Assert.That(c.Rationale).IsEqualTo("why");
        await Assert.That(c.SecurityNotes).IsEqualTo("careful");
        await Assert.That(c.Confidence).IsEqualTo(0.5f);
        await Assert.That(c.DenialSummaryIds).IsEquivalentTo(new[] { "d1", "d2" });
        await Assert.That(c.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(c.DecidedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(2));
        await Assert.That(c.Stage).IsEqualTo("refined");
        await Assert.That(c.SupersedesChunkId).IsEqualTo("c0");
        await Assert.That(c.HitCount).IsEqualTo(7);
        await Assert.That(c.FirstSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(3));
        await Assert.That(c.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(4));
        await Assert.That(c.Binary).IsEqualTo("/usr/bin/pip");
        await Assert.That(c.ValidationResult).IsEqualTo("ok");
        await Assert.That(c.RejectionReason).IsEqualTo("no");
        await Assert.That(c.ApplicationError).IsEqualTo("conflict");
        await Assert.That(c.ReviewToken).IsEqualTo("tok");
        await Assert.That(c.CurrentEffectivePolicyHash).IsEqualTo("a");
        await Assert.That(c.CandidateEffectivePolicyHash).IsEqualTo("b");
        await Assert.That(c.CurrentEffectivePolicy!.Version).IsEqualTo(1u);
        await Assert.That(c.CandidateEffectivePolicy!.Version).IsEqualTo(2u);
    }

    [Test]
    public async Task GetDraft_without_filter_or_timestamps_has_safe_defaults()
    {
        Wire.GetDraftPolicyRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetDraftPolicyRequest, Wire.GetDraftPolicyResponse>("GetDraftPolicy", r =>
        {
            seen = r;
            var reply = new Wire.GetDraftPolicyResponse();
            reply.Chunks.Add(new Wire.PolicyChunk { Id = "c" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var draft = await client.Policy.GetDraftAsync("sb");

        await Assert.That(seen!.StatusFilter).IsEqualTo("");
        await Assert.That(draft.LastAnalyzedAt).IsNull();
        await Assert.That(draft.Chunks.Single().CreatedAt).IsNull();
        await Assert.That(draft.Chunks.Single().ProposedRule).IsNull();
    }

    [Test]
    public async Task Approve_sends_token_and_returns_new_version()
    {
        Wire.ApproveDraftChunkRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ApproveDraftChunkRequest, Wire.ApproveDraftChunkResponse>("ApproveDraftChunk", r =>
        {
            seen = r;
            return new Wire.ApproveDraftChunkResponse { PolicyVersion = 4, PolicyHash = "h" };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Policy.ApproveChunkAsync("sb", "c1", "tok", "rid");

        await Assert.That(seen!.ChunkId).IsEqualTo("c1");
        await Assert.That(seen.ReviewToken).IsEqualTo("tok");
        await Assert.That(seen.RequestId).IsEqualTo("rid");
        await Assert.That(result).IsEqualTo(new PolicyChangeResult(4, "h"));
    }

    [Test]
    public async Task Approve_maps_stale_review_token_to_conflict()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.ApproveDraftChunkRequest, Wire.ApproveDraftChunkResponse>(
            "ApproveDraftChunk", StatusCode.FailedPrecondition, "stale");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Policy.ApproveChunkAsync("sb", "c", "t")).Throws<OpenShellException>();
        await Assert.That(ex!.IsConflict).IsTrue();
    }

    [Test]
    public async Task Reject_sends_reason()
    {
        Wire.RejectDraftChunkRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RejectDraftChunkRequest, Wire.RejectDraftChunkResponse>("RejectDraftChunk", r =>
        {
            seen = r;
            return new Wire.RejectDraftChunkResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Policy.RejectChunkAsync("sb", "c1", "too broad", "rid");
        await client.Policy.RejectChunkAsync("sb", "c2");

        await Assert.That(invoker.Calls.Select(c => ((Wire.RejectDraftChunkRequest)c.Request).Reason)).IsEquivalentTo(new[] { "too broad", "" });
        await Assert.That(seen!.ChunkId).IsEqualTo("c2");
    }

    [Test]
    public async Task ApproveAll_sends_options_and_maps_counts()
    {
        Wire.ApproveAllDraftChunksRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ApproveAllDraftChunksRequest, Wire.ApproveAllDraftChunksResponse>("ApproveAllDraftChunks", r =>
        {
            seen = r;
            return new Wire.ApproveAllDraftChunksResponse { PolicyVersion = 6, PolicyHash = "h", ChunksApproved = 3, ChunksSkipped = 1 };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Policy.ApproveAllAsync("sb", new ApproveAllOptions
        {
            IncludeSecurityFlagged = true,
            Approvals = [new ChunkApproval("c1", "t1"), new ChunkApproval("c2", "t2")],
            RequestId = "rid",
        });
        await client.Policy.ApproveAllAsync("sb");

        var first = (Wire.ApproveAllDraftChunksRequest)invoker.Calls[0].Request;
        await Assert.That(first.IncludeSecurityFlagged).IsTrue();
        await Assert.That(first.Approvals.Select(a => a.ReviewToken)).IsEquivalentTo(new[] { "t1", "t2" });
        await Assert.That(first.RequestId).IsEqualTo("rid");
        await Assert.That(seen!.Approvals.Count).IsEqualTo(0);
        await Assert.That(result).IsEqualTo(new ApproveAllResult(6, "h", 3, 1));
    }

    [Test]
    public async Task Edit_undo_clear_and_history_roundtrip()
    {
        Wire.EditDraftChunkRequest? edit = null;
        var invoker = new FakeCallInvoker()
            .Unary<Wire.EditDraftChunkRequest, Wire.EditDraftChunkResponse>("EditDraftChunk", r =>
            {
                edit = r;
                return new Wire.EditDraftChunkResponse();
            })
            .Unary<Wire.UndoDraftChunkRequest, Wire.UndoDraftChunkResponse>("UndoDraftChunk", r => new Wire.UndoDraftChunkResponse { PolicyVersion = 8, PolicyHash = "u" })
            .Unary<Wire.ClearDraftChunksRequest, Wire.ClearDraftChunksResponse>("ClearDraftChunks", _ => new Wire.ClearDraftChunksResponse { ChunksCleared = 4 })
            .Unary<Wire.GetDraftHistoryRequest, Wire.GetDraftHistoryResponse>("GetDraftHistory", _ =>
            {
                var reply = new Wire.GetDraftHistoryResponse();
                reply.Entries.Add(new Wire.DraftHistoryEntry { EventTime = At(1), EventType = "approved", Description = "d", ChunkId = "c" });
                reply.Entries.Add(new Wire.DraftHistoryEntry { EventType = "cleared" });
                return reply;
            });
        using var client = ClientTests.NewClient(invoker);

        await client.Policy.EditChunkAsync("sb", "c1", new WireSandbox.NetworkPolicyRule { Name = "n" }, "rid");
        var undo = await client.Policy.UndoChunkAsync("sb", "c1", "rid2");
        var cleared = await client.Policy.ClearChunksAsync("sb", "rid3");
        var history = await client.Policy.GetDraftHistoryAsync("sb");

        await Assert.That(edit!.ProposedRule.Name).IsEqualTo("n");
        await Assert.That(edit.RequestId).IsEqualTo("rid");
        await Assert.That(undo).IsEqualTo(new PolicyChangeResult(8, "u"));
        await Assert.That(cleared).IsEqualTo(4u);
        await Assert.That(history.Count).IsEqualTo(2);
        await Assert.That(history[0]).IsEqualTo(new DraftHistoryEntry(DateTimeOffset.UnixEpoch.AddDays(1), "approved", "d", "c"));
        await Assert.That(history[1].EventTime).IsNull();
    }

    [Test]
    public async Task Mutations_without_a_request_id_send_an_empty_one()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.ApproveDraftChunkRequest, Wire.ApproveDraftChunkResponse>("ApproveDraftChunk", _ => new Wire.ApproveDraftChunkResponse())
            .Unary<Wire.EditDraftChunkRequest, Wire.EditDraftChunkResponse>("EditDraftChunk", _ => new Wire.EditDraftChunkResponse())
            .Unary<Wire.UndoDraftChunkRequest, Wire.UndoDraftChunkResponse>("UndoDraftChunk", _ => new Wire.UndoDraftChunkResponse())
            .Unary<Wire.ClearDraftChunksRequest, Wire.ClearDraftChunksResponse>("ClearDraftChunks", _ => new Wire.ClearDraftChunksResponse());
        using var client = ClientTests.NewClient(invoker);

        await client.Policy.ApproveChunkAsync("sb", "c", "t");
        await client.Policy.EditChunkAsync("sb", "c", new WireSandbox.NetworkPolicyRule());
        await client.Policy.UndoChunkAsync("sb", "c");
        await client.Policy.ClearChunksAsync("sb");

        await Assert.That(invoker.Calls.Count).IsEqualTo(4);
        await Assert.That(((Wire.ApproveDraftChunkRequest)invoker.Calls[0].Request).RequestId).IsEqualTo("");
        await Assert.That(((Wire.EditDraftChunkRequest)invoker.Calls[1].Request).RequestId).IsEqualTo("");
        await Assert.That(((Wire.UndoDraftChunkRequest)invoker.Calls[2].Request).RequestId).IsEqualTo("");
        await Assert.That(((Wire.ClearDraftChunksRequest)invoker.Calls[3].Request).RequestId).IsEqualTo("");
    }

    [Test]
    public async Task GetStatus_scopes_to_workspace_unless_global()
    {
        var reply = new Wire.GetSandboxPolicyStatusResponse
        {
            ActiveVersion = 3,
            Revision = new Wire.SandboxPolicyRevision
            {
                Version = 4,
                PolicyHash = "h",
                Status = Wire.PolicyStatus.Failed,
                LoadError = "bad",
                CreatedTime = At(1),
                LoadedTime = At(2),
                Policy = new WireSandbox.SandboxPolicy { Version = 4 },
                Provenance = { ["by"] = "ci" },
            },
        };
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxPolicyStatusRequest, Wire.GetSandboxPolicyStatusResponse>("GetSandboxPolicyStatus", _ => reply);
        using var client = ClientTests.NewClient(invoker, "ws");

        var local = await client.Policy.GetStatusAsync("sb", new PolicyStatusOptions { Version = 4 });
        await client.Policy.GetStatusAsync("sb", new PolicyStatusOptions { Global = true });

        var localRequest = (Wire.GetSandboxPolicyStatusRequest)invoker.Calls[0].Request;
        var globalRequest = (Wire.GetSandboxPolicyStatusRequest)invoker.Calls[1].Request;
        await Assert.That(localRequest.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(localRequest.Version).IsEqualTo(4u);
        await Assert.That(globalRequest.Global).IsTrue();
        await Assert.That(globalRequest.WorkspaceScope).IsNull();
        await Assert.That(local.ActiveVersion).IsEqualTo(3u);
        var rev = local.Revision!;
        await Assert.That(rev.Version).IsEqualTo(4u);
        await Assert.That(rev.PolicyHash).IsEqualTo("h");
        await Assert.That(rev.Status).IsEqualTo(PolicyLoadStatus.Failed);
        await Assert.That(rev.LoadError).IsEqualTo("bad");
        await Assert.That(rev.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(rev.LoadedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(2));
        await Assert.That(rev.Policy!.Version).IsEqualTo(4u);
        await Assert.That(rev.Provenance["by"]).IsEqualTo("ci");
    }

    [Test]
    public async Task GetStatus_without_revision_returns_null_revision()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxPolicyStatusRequest, Wire.GetSandboxPolicyStatusResponse>(
            "GetSandboxPolicyStatus", _ => new Wire.GetSandboxPolicyStatusResponse { ActiveVersion = 1 });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Policy.GetStatusAsync("sb");

        await Assert.That(result.Revision).IsNull();
        await Assert.That(result.ActiveVersion).IsEqualTo(1u);
    }

    [Test]
    public async Task ListRevisions_pages_lazily_and_scopes_global()
    {
        var requests = new List<Wire.ListSandboxPoliciesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxPoliciesRequest, Wire.ListSandboxPoliciesResponse>("ListSandboxPolicies", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListSandboxPoliciesResponse();
            reply.Revisions.Add(new Wire.SandboxPolicyRevision { Version = r.PageToken == "" ? 1u : 2u });
            if (r.PageToken == "")
            {
                reply.NextPageToken = "p2";
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var local = await client.Policy.ListRevisions("sb", new PolicyListOptions { PageSize = 1 }).ToListAsync();
        var global = await client.Policy.ListRevisions("sb", new PolicyListOptions { Global = true, PageToken = "resume" }).ToListAsync();
        var defaults = await client.Policy.ListRevisions("sb").ToListAsync();

        await Assert.That(local.Select(r => r.Version)).IsEquivalentTo(new[] { 1u, 2u });
        await Assert.That(requests[0].PageSize).IsEqualTo(1);
        await Assert.That(requests[0].WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
        await Assert.That(requests[2].Global).IsTrue();
        await Assert.That(requests[2].WorkspaceScope).IsNull();
        await Assert.That(requests[2].PageToken).IsEqualTo("resume");
        await Assert.That(global.Count).IsEqualTo(1);
        await Assert.That(defaults.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var p = client.Policy;

        await Assert.That(async () => await p.GetDraftAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await p.ApproveChunkAsync("", "c", "t")).Throws<ArgumentException>();
        await Assert.That(async () => await p.ApproveChunkAsync("s", "", "t")).Throws<ArgumentException>();
        await Assert.That(async () => await p.ApproveChunkAsync("s", "c", "")).Throws<ArgumentException>();
        await Assert.That(async () => await p.RejectChunkAsync("", "c")).Throws<ArgumentException>();
        await Assert.That(async () => await p.RejectChunkAsync("s", "")).Throws<ArgumentException>();
        await Assert.That(async () => await p.ApproveAllAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await p.EditChunkAsync("", "c", new WireSandbox.NetworkPolicyRule())).Throws<ArgumentException>();
        await Assert.That(async () => await p.EditChunkAsync("s", "", new WireSandbox.NetworkPolicyRule())).Throws<ArgumentException>();
        await Assert.That(async () => await p.EditChunkAsync("s", "c", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await p.UndoChunkAsync("", "c")).Throws<ArgumentException>();
        await Assert.That(async () => await p.UndoChunkAsync("s", "")).Throws<ArgumentException>();
        await Assert.That(async () => await p.ClearChunksAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await p.GetDraftHistoryAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await p.GetStatusAsync("")).Throws<ArgumentException>();
        await Assert.That(() => p.ListRevisions("")).Throws<ArgumentException>();
    }
}
