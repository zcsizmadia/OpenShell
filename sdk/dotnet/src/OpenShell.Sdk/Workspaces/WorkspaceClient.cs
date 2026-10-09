// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Lifecycle phase of a workspace. Values match the wire enum.</summary>
public enum WorkspacePhase
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Usable.</summary>
    Active = 1,
    /// <summary>Being deleted.</summary>
    Terminating = 2,
}

/// <summary>Role of a principal within a workspace. Values match the wire enum.</summary>
public enum WorkspaceRole
{
    /// <summary>Not specified; the gateway rejects it on add.</summary>
    Unspecified = 0,
    /// <summary>Regular member.</summary>
    User = 1,
    /// <summary>Workspace administrator.</summary>
    Admin = 2,
}

/// <summary>A workspace: the isolation boundary for sandboxes, providers and other resources.</summary>
public sealed record Workspace
{
    /// <summary>Immutable identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical name.</summary>
    public required string Name { get; init; }

    /// <summary>Lifecycle phase.</summary>
    public WorkspacePhase Phase { get; init; }

    /// <summary>User labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = Sandbox.EmptyMap;

    /// <summary>Optimistic-concurrency version.</summary>
    public ulong ResourceVersion { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>A principal's membership in a workspace.</summary>
public sealed record WorkspaceMember
{
    /// <summary>OIDC subject claim identifying the principal.</summary>
    public required string PrincipalSubject { get; init; }

    /// <summary>Assigned role.</summary>
    public WorkspaceRole Role { get; init; }

    /// <summary>Membership record identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Time the membership was created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Workspace and membership management.</summary>
public interface IWorkspaceClient
{
    /// <summary>Creates a workspace. The name must be a DNS-1123 label.</summary>
    ValueTask<Workspace> CreateAsync(string name, IReadOnlyDictionary<string, string>? labels = null, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a workspace by name.</summary>
    ValueTask<Workspace> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists workspaces lazily. <see cref="ListOptions.AllWorkspaces"/> does not apply to workspaces and is ignored.
    /// </summary>
    Pager<Workspace> List(ListOptions? options = null);

    /// <summary>Deletes a workspace. Inspect <see cref="DeletionResult.Outcome"/>.</summary>
    ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Adds a principal to a workspace with the given role.</summary>
    ValueTask<WorkspaceMember> AddMemberAsync(string workspace, string principalSubject, WorkspaceRole role, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a principal from a workspace.</summary>
    ValueTask<DeletionResult> RemoveMemberAsync(string workspace, string principalSubject, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the members of a workspace lazily.</summary>
    Pager<WorkspaceMember> ListMembers(string workspace, ListOptions? options = null);
}

internal sealed class WorkspaceClient(ClientCore core) : IWorkspaceClient
{
    public async ValueTask<Workspace> CreateAsync(string name, IReadOnlyDictionary<string, string>? labels = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.CreateWorkspaceRequest { Name = name, RequestId = requestId ?? "" };
        SandboxMapping.AddAll(request.Labels, labels);
        var response = await core.UnaryAsync(o => core.Grpc.CreateWorkspaceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Workspace);
    }

    public async ValueTask<Workspace> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.GetWorkspaceRequest { Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetWorkspaceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Workspace);
    }

    public Pager<Workspace> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<Workspace>(
            async (token, ct) =>
            {
                var request = new Wire.ListWorkspacesRequest
                {
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                    LabelSelector = options.LabelSelector ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListWorkspacesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<Workspace>(
                    response.Workspaces.Select(ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.DeleteWorkspaceRequest
        {
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DeleteWorkspaceAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DeletionResult((DeletionOutcome)(int)response.Outcome);
    }

    public async ValueTask<WorkspaceMember> AddMemberAsync(string workspace, string principalSubject, WorkspaceRole role, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalSubject);
        var request = new Wire.AddWorkspaceMemberRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            PrincipalSubject = principalSubject,
            Role = (Wire.WorkspaceRole)(int)role,
            RequestId = requestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.AddWorkspaceMemberAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Member);
    }

    public async ValueTask<DeletionResult> RemoveMemberAsync(string workspace, string principalSubject, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalSubject);
        var request = new Wire.RemoveWorkspaceMemberRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            PrincipalSubject = principalSubject,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.RemoveWorkspaceMemberAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DeletionResult((DeletionOutcome)(int)response.Outcome);
    }

    public Pager<WorkspaceMember> ListMembers(string workspace, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        options ??= new ListOptions();
        return new Pager<WorkspaceMember>(
            async (token, ct) =>
            {
                var request = new Wire.ListWorkspaceMembersRequest
                {
                    WorkspaceScope = ClientCore.Scope(workspace),
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListWorkspaceMembersAsync(request, o), ct).ConfigureAwait(false);
                return new Page<WorkspaceMember>(
                    response.Members.Select(ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    private static Workspace ToDomain(Openshell.Datamodel.V1.Workspace w) => new()
    {
        Id = w.Metadata?.Id ?? "",
        Name = w.Metadata?.Name ?? "",
        Phase = w.Status is null ? WorkspacePhase.Unspecified : (WorkspacePhase)(int)w.Status.Phase,
        Labels = SandboxMapping.Copy(w.Metadata?.Labels),
        ResourceVersion = w.Metadata?.ResourceVersion ?? 0,
        CreatedAt = w.Metadata?.CreatedTime?.ToDateTimeOffset(),
    };

    private static WorkspaceMember ToDomain(Wire.WorkspaceMember m) => new()
    {
        PrincipalSubject = m.PrincipalSubject,
        Role = (WorkspaceRole)(int)m.Role,
        Id = m.Metadata?.Id is { Length: > 0 } id ? id : null,
        CreatedAt = m.Metadata?.CreatedTime?.ToDateTimeOffset(),
    };
}
