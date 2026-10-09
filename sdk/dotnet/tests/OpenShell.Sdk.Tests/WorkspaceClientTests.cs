// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Datamodel = Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class WorkspaceClientTests
{
    private static Datamodel.Workspace WireWorkspace(string name, Datamodel.WorkspacePhase phase = Datamodel.WorkspacePhase.Active) =>
        new()
        {
            Metadata = new Datamodel.ObjectMeta { Id = "ws-" + name, Name = name },
            Status = new Datamodel.WorkspaceStatus { Phase = phase },
        };

    [Test]
    public async Task Create_sends_name_labels_and_request_id_without_scope_and_maps_response()
    {
        Wire.CreateWorkspaceRequest? seen = null;
        var reply = new Wire.CreateWorkspaceResponse { Workspace = WireWorkspace("team") };
        reply.Workspace.Metadata.Labels["env"] = "dev";
        reply.Workspace.Metadata.ResourceVersion = 3;
        reply.Workspace.Metadata.CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1));
        var invoker = new FakeCallInvoker().Unary<Wire.CreateWorkspaceRequest, Wire.CreateWorkspaceResponse>("CreateWorkspace", r =>
        {
            seen = r;
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var ws = await client.Workspaces.CreateAsync("team", new Dictionary<string, string> { ["env"] = "dev" }, "req");

        await Assert.That(seen!.Name).IsEqualTo("team");
        await Assert.That(seen.Labels["env"]).IsEqualTo("dev");
        await Assert.That(seen.RequestId).IsEqualTo("req");
        await Assert.That(ws.Id).IsEqualTo("ws-team");
        await Assert.That(ws.Name).IsEqualTo("team");
        await Assert.That(ws.Phase).IsEqualTo(WorkspacePhase.Active);
        await Assert.That(ws.Labels["env"]).IsEqualTo("dev");
        await Assert.That(ws.ResourceVersion).IsEqualTo(3ul);
        await Assert.That(ws.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
    }

    [Test]
    public async Task Workspace_with_missing_metadata_has_safe_defaults()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetWorkspaceRequest, Wire.GetWorkspaceResponse>(
            "GetWorkspace", _ => new Wire.GetWorkspaceResponse { Workspace = new Datamodel.Workspace() });
        using var client = ClientTests.NewClient(invoker);

        var ws = await client.Workspaces.GetAsync("x");

        await Assert.That(ws.Name).IsEqualTo("");
        await Assert.That(ws.Phase).IsEqualTo(WorkspacePhase.Unspecified);
        await Assert.That(ws.Labels.Count).IsEqualTo(0);
        await Assert.That(ws.CreatedAt).IsNull();
    }

    [Test]
    public async Task Get_sends_name()
    {
        Wire.GetWorkspaceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetWorkspaceRequest, Wire.GetWorkspaceResponse>("GetWorkspace", r =>
        {
            seen = r;
            return new Wire.GetWorkspaceResponse { Workspace = WireWorkspace("a", Datamodel.WorkspacePhase.Terminating) };
        });
        using var client = ClientTests.NewClient(invoker);

        var ws = await client.Workspaces.GetAsync("a");

        await Assert.That(seen!.Name).IsEqualTo("a");
        await Assert.That(ws.Phase).IsEqualTo(WorkspacePhase.Terminating);
    }

    [Test]
    public async Task List_pages_and_applies_label_selector()
    {
        var requests = new List<Wire.ListWorkspacesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListWorkspacesRequest, Wire.ListWorkspacesResponse>("ListWorkspaces", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListWorkspacesResponse();
            if (r.PageToken == "")
            {
                reply.Workspaces.Add(WireWorkspace("a"));
                reply.NextPageToken = "p2";
            }
            else
            {
                reply.Workspaces.Add(WireWorkspace("b"));
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Workspaces.List(new ListOptions { PageSize = 5, LabelSelector = "k=v" }).ToListAsync();

        await Assert.That(all.Select(w => w.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].PageSize).IsEqualTo(5);
        await Assert.That(requests[0].LabelSelector).IsEqualTo("k=v");
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task List_with_default_options_works()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.ListWorkspacesRequest, Wire.ListWorkspacesResponse>(
            "ListWorkspaces", _ => new Wire.ListWorkspacesResponse());
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Workspaces.List().ToListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_returns_typed_outcome_without_id()
    {
        Wire.DeleteWorkspaceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteWorkspaceRequest, Wire.DeleteWorkspaceResponse>("DeleteWorkspace", r =>
        {
            seen = r;
            return new Wire.DeleteWorkspaceResponse { Outcome = Wire.DeletionOutcome.Accepted };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Workspaces.DeleteAsync("a", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.Accepted);
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.ResourceId).IsNull();
        await Assert.That(seen!.Name).IsEqualTo("a");
        await Assert.That(seen.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
    }

    [Test]
    public async Task Delete_defaults_when_no_options()
    {
        Wire.DeleteWorkspaceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteWorkspaceRequest, Wire.DeleteWorkspaceResponse>("DeleteWorkspace", r =>
        {
            seen = r;
            return new Wire.DeleteWorkspaceResponse { Outcome = Wire.DeletionOutcome.Completed };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Workspaces.DeleteAsync("a");

        await Assert.That(seen!.AllowMissing).IsFalse();
        await Assert.That(seen.RequestId).IsEqualTo("");
    }

    [Test]
    public async Task AddMember_uses_workspace_scope_and_maps_member()
    {
        Wire.AddWorkspaceMemberRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.AddWorkspaceMemberRequest, Wire.AddWorkspaceMemberResponse>("AddWorkspaceMember", r =>
        {
            seen = r;
            return new Wire.AddWorkspaceMemberResponse
            {
                Member = new Wire.WorkspaceMember
                {
                    Metadata = new Datamodel.ObjectMeta { Id = "m1", CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch) },
                    PrincipalSubject = "alice",
                    Role = Wire.WorkspaceRole.Admin,
                },
            };
        });
        using var client = ClientTests.NewClient(invoker);

        var member = await client.Workspaces.AddMemberAsync("team", "alice", WorkspaceRole.Admin, "rid");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("team");
        await Assert.That(seen.PrincipalSubject).IsEqualTo("alice");
        await Assert.That(seen.Role).IsEqualTo(Wire.WorkspaceRole.Admin);
        await Assert.That(seen.RequestId).IsEqualTo("rid");
        await Assert.That(member.PrincipalSubject).IsEqualTo("alice");
        await Assert.That(member.Role).IsEqualTo(WorkspaceRole.Admin);
        await Assert.That(member.Id).IsEqualTo("m1");
        await Assert.That(member.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    [Test]
    public async Task Member_without_metadata_has_null_id()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.AddWorkspaceMemberRequest, Wire.AddWorkspaceMemberResponse>(
            "AddWorkspaceMember",
            _ => new Wire.AddWorkspaceMemberResponse { Member = new Wire.WorkspaceMember { PrincipalSubject = "b", Role = Wire.WorkspaceRole.User } });
        using var client = ClientTests.NewClient(invoker);

        var member = await client.Workspaces.AddMemberAsync("team", "b", WorkspaceRole.User);

        await Assert.That(member.Id).IsNull();
        await Assert.That(member.CreatedAt).IsNull();
        await Assert.That(member.Role).IsEqualTo(WorkspaceRole.User);
    }

    [Test]
    public async Task RemoveMember_sends_scope_subject_and_options()
    {
        Wire.RemoveWorkspaceMemberRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RemoveWorkspaceMemberRequest, Wire.RemoveWorkspaceMemberResponse>("RemoveWorkspaceMember", r =>
        {
            seen = r;
            return new Wire.RemoveWorkspaceMemberResponse { Outcome = Wire.DeletionOutcome.AlreadyAbsent };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Workspaces.RemoveMemberAsync("team", "alice", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("team");
        await Assert.That(seen.PrincipalSubject).IsEqualTo("alice");
        await Assert.That(seen.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
    }

    [Test]
    public async Task RemoveMember_defaults_when_no_options()
    {
        Wire.RemoveWorkspaceMemberRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RemoveWorkspaceMemberRequest, Wire.RemoveWorkspaceMemberResponse>("RemoveWorkspaceMember", r =>
        {
            seen = r;
            return new Wire.RemoveWorkspaceMemberResponse { Outcome = Wire.DeletionOutcome.Completed };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Workspaces.RemoveMemberAsync("team", "alice");

        await Assert.That(seen!.AllowMissing).IsFalse();
    }

    [Test]
    public async Task ListMembers_pages()
    {
        var requests = new List<Wire.ListWorkspaceMembersRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListWorkspaceMembersRequest, Wire.ListWorkspaceMembersResponse>("ListWorkspaceMembers", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListWorkspaceMembersResponse();
            reply.Members.Add(new Wire.WorkspaceMember { PrincipalSubject = r.PageToken == "" ? "a" : "b" });
            if (r.PageToken == "")
            {
                reply.NextPageToken = "p2";
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Workspaces.ListMembers("team", new ListOptions { PageSize = 2 }).ToListAsync();

        await Assert.That(all.Select(m => m.PrincipalSubject)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].WorkspaceScope.Workspace).IsEqualTo("team");
        await Assert.That(requests[0].PageSize).IsEqualTo(2);
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task ListMembers_with_default_options_works()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.ListWorkspaceMembersRequest, Wire.ListWorkspaceMembersResponse>(
            "ListWorkspaceMembers", _ => new Wire.ListWorkspaceMembersResponse());
        using var client = ClientTests.NewClient(invoker);

        await Assert.That((await client.Workspaces.ListMembers("team").ToListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var ws = client.Workspaces;

        await Assert.That(async () => await ws.CreateAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await ws.GetAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await ws.DeleteAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await ws.AddMemberAsync("", "s", WorkspaceRole.User)).Throws<ArgumentException>();
        await Assert.That(async () => await ws.AddMemberAsync("w", "", WorkspaceRole.User)).Throws<ArgumentException>();
        await Assert.That(async () => await ws.RemoveMemberAsync("", "s")).Throws<ArgumentException>();
        await Assert.That(async () => await ws.RemoveMemberAsync("w", "")).Throws<ArgumentException>();
        await Assert.That(() => ws.ListMembers("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Gateway_errors_are_mapped()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetWorkspaceRequest, Wire.GetWorkspaceResponse>("GetWorkspace", StatusCode.NotFound, "nope");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Workspaces.GetAsync("a")).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Workspaces_are_shared_across_scoped_views()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetWorkspaceRequest, Wire.GetWorkspaceResponse>(
            "GetWorkspace", _ => new Wire.GetWorkspaceResponse { Workspace = WireWorkspace("a") });
        using var client = ClientTests.NewClient(invoker);

        await client.InWorkspace("other").Workspaces.GetAsync("a");

        await Assert.That(invoker.CountOf("GetWorkspace")).IsEqualTo(1);
    }
}
