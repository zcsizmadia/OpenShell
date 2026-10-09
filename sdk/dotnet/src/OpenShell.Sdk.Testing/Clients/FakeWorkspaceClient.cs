// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

internal sealed class FakeWorkspaceClient(FakeGateway gateway) : IWorkspaceClient
{
    public ValueTask<Workspace> CreateAsync(string name, IReadOnlyDictionary<string, string>? labels = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Workspaces.Create", "", name, cancellationToken, () =>
        {
            if (gateway.ReplayName("workspace", "", requestId) is { } replay && gateway.Workspaces.TryGetValue(replay, out var original))
            {
                return ToDomain(original);
            }

            if (name.Length > 63 || !FakeGateway.DnsLabel().IsMatch(name))
            {
                throw FakeGateway.Invalid($"workspace name '{name}' must be a DNS-1123 label");
            }

            if (gateway.Workspaces.ContainsKey(name))
            {
                throw FakeGateway.AlreadyExists("workspace", name);
            }

            var entry = gateway.CreateWorkspaceLocked(name, labels);
            gateway.RememberRequest("workspace", "", requestId, name);
            return ToDomain(entry);
        }));
    }

    public ValueTask<Workspace> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Workspaces.Get", "", name, cancellationToken, () => ToDomain(Find(name))));
    }

    public Pager<Workspace> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return FakePaging.Create(
            gateway,
            "Workspaces.List",
            "",
            null,
            options.PageSize,
            options.PageToken,
            () => gateway.Workspaces.Values.OrderBy(w => w.Name, StringComparer.Ordinal).Select(ToDomain).ToList());
    }

    public ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(gateway.Run("Workspaces.Delete", "", name, cancellationToken, () =>
        {
            gateway.Workspaces.TryGetValue(name, out var entry);
            return FakeGateway.DeleteResult(entry is not null, options, "workspace", name, () => Remove(entry!), entry?.Id);
        }));
    }

    public ValueTask<WorkspaceMember> AddMemberAsync(string workspace, string principalSubject, WorkspaceRole role, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalSubject);
        return ValueTask.FromResult(gateway.Run("Workspaces.AddMember", "", workspace, cancellationToken, () =>
        {
            var entry = Find(workspace);
            if (role == WorkspaceRole.Unspecified)
            {
                throw FakeGateway.Invalid("a workspace role is required");
            }

            var member = new WorkspaceMember
            {
                PrincipalSubject = principalSubject,
                Role = role,
                Id = entry.Members.TryGetValue(principalSubject, out var existing) ? existing.Id : gateway.NextId("member"),
                CreatedAt = existing?.CreatedAt ?? gateway.Time.GetUtcNow(),
            };
            entry.Members[principalSubject] = member;
            entry.ResourceVersion = gateway.NextVersion();
            return member;
        }));
    }

    public ValueTask<DeletionResult> RemoveMemberAsync(string workspace, string principalSubject, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalSubject);
        return ValueTask.FromResult(gateway.Run("Workspaces.RemoveMember", "", workspace, cancellationToken, () =>
        {
            var entry = Find(workspace);
            var existed = entry.Members.TryGetValue(principalSubject, out var member);
            return FakeGateway.DeleteResult(existed, options, "member", principalSubject, () => entry.Members.Remove(principalSubject), member?.Id);
        }));
    }

    public Pager<WorkspaceMember> ListMembers(string workspace, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        options ??= new ListOptions();
        return FakePaging.Create(
            gateway,
            "Workspaces.ListMembers",
            "",
            workspace,
            options.PageSize,
            options.PageToken,
            () => Find(workspace).Members.Values.OrderBy(m => m.PrincipalSubject, StringComparer.Ordinal).ToList());
    }

    private static Workspace ToDomain(WorkspaceEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Phase = entry.Phase,
        Labels = new Dictionary<string, string>(entry.Labels),
        ResourceVersion = entry.ResourceVersion,
        CreatedAt = entry.CreatedAt,
    };

    private WorkspaceEntry Find(string name) =>
        gateway.Workspaces.TryGetValue(name, out var entry) ? entry : throw FakeGateway.NotFound("workspace", name);

    /// <summary>Deleting a workspace removes everything it contains.</summary>
    private void Remove(WorkspaceEntry entry)
    {
        foreach (var sandbox in gateway.Sandboxes.Values.Where(s => s.Workspace == entry.Name).ToArray())
        {
            gateway.RemoveSandboxLocked(sandbox);
        }

        foreach (var key in gateway.Templates.Keys.Where(k => k.Workspace == entry.Name).ToArray())
        {
            gateway.Templates.Remove(key);
        }

        foreach (var key in gateway.ProviderStore.Keys.Where(k => k.Workspace == entry.Name).ToArray())
        {
            gateway.ProviderStore.Remove(key);
        }

        foreach (var key in gateway.Profiles.Keys.Where(k => k.Workspace == entry.Name).ToArray())
        {
            gateway.Profiles.Remove(key);
        }

        gateway.Workspaces.Remove(entry.Name);
    }
}
