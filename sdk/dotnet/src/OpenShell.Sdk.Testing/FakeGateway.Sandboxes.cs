// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    private const int MaxRecordsPerSandbox = 5000;

    /// <summary>
    /// Sets a sandbox phase directly and cancels any scheduled transition, so tests can reach states such as
    /// <see cref="SandboxPhase.Error"/> without waiting. Watchers see the change.
    /// </summary>
    /// <param name="sandbox">The sandbox name.</param>
    /// <param name="phase">The phase to pin.</param>
    /// <param name="workspace">The workspace; defaults to the gateway default workspace.</param>
    /// <param name="exitCode">The main-process exit code to report, when relevant.</param>
    public void SetPhase(string sandbox, SandboxPhase phase, string? workspace = null, int? exitCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            entry.Phase = phase;
            entry.NextPhase = null;
            entry.NextAt = null;
            entry.RemoveOnTransition = false;
            entry.ExitCode = exitCode;
            Touch(entry);
        }
    }

    /// <summary>Adds a log line to a sandbox. It reaches watchers and <see cref="ISandboxWatchClient.GetLogsAsync"/>. Returns its cursor.</summary>
    public string EmitLog(string sandbox, string message, string level = "INFO", string source = "sandbox", string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(message);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            var line = new LogLine(entry.Id, Time.GetUtcNow(), level, "fake", message, source, new Dictionary<string, string>());
            return AddRecord(entry, WatchKind.Log, log: line).Cursor;
        }
    }

    /// <summary>Adds a platform event to a sandbox watch stream. Returns its cursor.</summary>
    public string EmitEvent(string sandbox, string reason, string message, string type = "Normal", string source = "fake", string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(message);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            var ev = new PlatformEvent(Time.GetUtcNow(), source, type, reason, message, new Dictionary<string, string>());
            return AddRecord(entry, WatchKind.Event, platformEvent: ev).Cursor;
        }
    }

    internal SandboxEntry FindSandbox(string workspace, string name) =>
        Sandboxes.TryGetValue(new Key(workspace, name), out var entry) ? entry : throw NotFound("sandbox", name);

    internal static bool IsTerminal(SandboxPhase phase) => phase is SandboxPhase.Error or SandboxPhase.Completed;

    internal Sandbox Project(SandboxEntry e)
    {
        var urls = new Dictionary<string, string>();
        foreach (var service in Services.Values)
        {
            if (service.Workspace == e.Workspace && service.Sandbox == e.Name)
            {
                urls[service.Name] = ServiceUrl(service);
            }
        }

        var ready = e.Phase == SandboxPhase.Ready;
        return new Sandbox
        {
            Id = e.Id,
            Name = e.Name,
            Workspace = e.Workspace,
            Phase = e.Phase,
            Labels = new Dictionary<string, string>(e.Labels),
            Annotations = new Dictionary<string, string>(e.Annotations),
            ResourceVersion = e.ResourceVersion,
            CreatedAt = e.CreatedAt,
            DeletedAt = e.DeletedAt,
            ServiceUrls = urls,
            Conditions = [new SandboxCondition("Ready", ready ? "True" : "False", e.Phase.ToString(), "", e.CreatedAt)],
            ExitCode = e.ExitCode,
            CurrentPolicyVersion = e.PolicyVersion,
            RestartCount = (uint)e.RestartCount,
            Image = e.Spec.Template?.Image ?? e.Spec.Image,
        };
    }

    internal static string ServiceUrl(ServiceEntry service)
    {
        var suffix = service.Name.Length > 0 ? "/" + service.Name : "";
        return service.Domain
            ? $"https://{(service.Name.Length > 0 ? service.Name + "." : "")}{service.Sandbox}.fake.example.com"
            : $"https://fake.gateway.local/services/{service.Workspace}/{service.Sandbox}{suffix}";
    }

    /// <summary>Marks a mutation: bumps the resource version and notifies watchers with a fresh snapshot.</summary>
    internal void Touch(SandboxEntry entry)
    {
        entry.ResourceVersion = NextVersion();
        AddRecord(entry, WatchKind.Status, Project(entry));
    }

    internal WatchRecord AddRecord(
        SandboxEntry entry,
        WatchKind kind,
        Sandbox? sandbox = null,
        LogLine? log = null,
        PlatformEvent? platformEvent = null,
        DraftPolicyUpdate? draft = null)
    {
        var record = new WatchRecord
        {
            Sequence = ++entry.Sequence,
            Kind = kind,
            Sandbox = sandbox,
            Log = log,
            Event = platformEvent,
            Draft = draft,
            Time = Time.GetUtcNow(),
        };
        entry.Records.Add(record);
        if (entry.Records.Count > MaxRecordsPerSandbox)
        {
            entry.Records.RemoveAt(0);
        }

        Signal();
        return record;
    }

    /// <summary>Starts a timed transition. Reads settle it lazily; the timer wakes blocked watchers.</summary>
    internal void BeginTransition(SandboxEntry entry, SandboxPhase next, TimeSpan delay, bool remove = false)
    {
        entry.NextPhase = next;
        entry.NextAt = Time.GetUtcNow() + delay;
        entry.RemoveOnTransition = remove;
        Schedule(delay);
    }

    /// <summary>Applies every timed transition that has come due.</summary>
    internal void SettleLocked()
    {
        var now = Time.GetUtcNow();
        foreach (var entry in Sandboxes.Values.Where(e => e.NextAt is { } due && due <= now).ToArray())
        {
            entry.NextAt = null;
            if (entry.RemoveOnTransition)
            {
                RemoveSandboxLocked(entry);
                continue;
            }

            entry.Phase = entry.NextPhase!.Value;
            entry.NextPhase = null;
            Touch(entry);
        }
    }

    internal void RemoveSandboxLocked(SandboxEntry entry)
    {
        Sandboxes.Remove(new Key(entry.Workspace, entry.Name));
        foreach (var key in Services.Keys.Where(k => k.Workspace == entry.Workspace && k.Sandbox == entry.Name).ToArray())
        {
            Services.Remove(key);
        }

        Files.Remove(new Key(entry.Workspace, entry.Name));
        Directories.Remove(new Key(entry.Workspace, entry.Name));
        Signal();
    }
}
