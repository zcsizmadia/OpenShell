// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace OpenShell.Testing;

internal readonly record struct SubKey(string Workspace, string Sandbox, string Name);

public sealed partial class FakeGateway
{
    internal Dictionary<string, WorkspaceEntry> Workspaces { get; } = [with(StringComparer.Ordinal)];

    internal Dictionary<Key, SandboxEntry> Sandboxes { get; } = [];

    internal Dictionary<Key, SandboxWorkloadTemplate> Templates { get; } = [];

    internal Dictionary<Key, ProviderEntry> ProviderStore { get; } = [];

    /// <summary>Provider profiles; an empty workspace in the key means platform scope.</summary>
    internal Dictionary<Key, ProviderProfile> Profiles { get; } = [];

    internal Dictionary<string, ReceiptEntry> Receipts { get; } = [with(StringComparer.Ordinal)];

    internal Dictionary<SubKey, ServiceEntry> Services { get; } = [];

    internal Dictionary<string, SshSessionEntry> SshSessions { get; } = [with(StringComparer.Ordinal)];

    internal Dictionary<SubKey, RefreshEntry> Refreshes { get; } = [];

    internal Dictionary<string, StagedRootfs> StagedSlots { get; } = [with(StringComparer.Ordinal)];

    internal Dictionary<Key, SortedDictionary<string, byte[]>> Files { get; } = [];

    internal Dictionary<Key, SortedSet<string>> Directories { get; } = [];

    internal Dictionary<string, string> Idempotency { get; } = [with(StringComparer.Ordinal)];

    internal List<FakeListener> Listeners { get; } = [];

    internal WorkspaceEntry RequireWorkspace(string name)
    {
        return Workspaces.TryGetValue(name, out var existing)
            ? existing
            : Options.AutoCreateWorkspaces
                ? CreateWorkspaceLocked(name, null)
                : throw new OpenShellException(OpenShellErrorKind.NotFound, $"workspace '{name}' not found");
    }

    internal WorkspaceEntry CreateWorkspaceLocked(string name, IReadOnlyDictionary<string, string>? labels)
    {
        var entry = new WorkspaceEntry
        {
            Id = NextId("ws"),
            Name = name,
            CreatedAt = Time.GetUtcNow(),
            ResourceVersion = NextVersion(),
        };
        if (labels is not null)
        {
            foreach (var (k, v) in labels)
            {
                entry.Labels[k] = v;
            }
        }

        Workspaces[name] = entry;
        return entry;
    }

    /// <summary>Resolves an idempotency key: returns the name already created for it, or null.</summary>
    internal string? ReplayName(string operation, string workspace, string? requestId)
    {
        return !string.IsNullOrEmpty(requestId) && Idempotency.TryGetValue($"{operation}|{workspace}|{requestId}", out var name) ? name : null;
    }

    internal void RememberRequest(string operation, string workspace, string? requestId, string name)
    {
        if (!string.IsNullOrEmpty(requestId))
        {
            Idempotency[$"{operation}|{workspace}|{requestId}"] = name;
        }
    }

    internal static OpenShellException NotFound(string kind, string name) =>
        new(OpenShellErrorKind.NotFound, $"{kind} '{name}' not found");

    internal static OpenShellException AlreadyExists(string kind, string name) =>
        new(OpenShellErrorKind.AlreadyExists, $"{kind} '{name}' already exists");

    internal static OpenShellException Invalid(string message) => new(OpenShellErrorKind.InvalidArgument, message);

    internal static OpenShellException Conflict(string message) => new(OpenShellErrorKind.Conflict, message);

    /// <summary>Deletes with the gateway's allow-missing rules.</summary>
    internal static DeletionResult DeleteResult(bool existed, DeleteOptions? options, string kind, string name, Action remove, string? id = null)
    {
        if (!existed)
        {
            return options is { AllowMissing: true }
                ? new DeletionResult(DeletionOutcome.AlreadyAbsent)
                : throw NotFound(kind, name);
        }

        remove();
        return new DeletionResult(DeletionOutcome.Completed, id);
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    internal static partial Regex DnsLabel();
}
