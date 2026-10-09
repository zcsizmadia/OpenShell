// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;

namespace OpenShell.Testing;

internal readonly record struct WatchBatch(List<WatchEvent> Events, long Position, bool Ended);

public sealed partial class FakeGateway
{
    /// <summary>Collects the stream events that arrived after <paramref name="position"/> without recording a call.</summary>
    internal WatchBatch Collect(string workspace, string sandbox, long position, WatchOptions options)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SettleLocked();
            if (!Sandboxes.TryGetValue(new Key(workspace, sandbox), out var entry))
            {
                return new WatchBatch([], position, Ended: true);
            }

            var events = new List<WatchEvent>();
            foreach (var record in entry.Records.Where(r => r.Sequence > position))
            {
                position = record.Sequence;
                if (ToEvent(record, options) is { } watchEvent)
                {
                    events.Add(watchEvent);
                }
            }

            return new WatchBatch(events, position, options.StopOnTerminal && IsTerminal(entry.Phase));
        }
    }

    /// <summary>The events a new stream replays before following: the current status plus the requested log and event tails.</summary>
    internal (List<WatchEvent> Events, long Position) Begin(string workspace, string sandbox, WatchOptions options)
    {
        var entry = FindSandbox(workspace, sandbox);
        var events = new List<WatchEvent>();
        if (options.FollowStatus)
        {
            events.Add(new SandboxUpdated(Project(entry), entry.Sequence.ToString(CultureInfo.InvariantCulture)));
        }

        var tail = new List<WatchRecord>();
        if (options.FollowLogs && options.LogTailLines > 0)
        {
            tail.AddRange(entry.Records.Where(r => r.Kind == WatchKind.Log && Passes(r, options)).TakeLast((int)options.LogTailLines));
        }

        if (options.FollowEvents && options.EventTail > 0)
        {
            tail.AddRange(entry.Records.Where(r => r.Kind == WatchKind.Event && Passes(r, options)).TakeLast((int)options.EventTail));
        }

        foreach (var record in tail.OrderBy(r => r.Sequence))
        {
            if (ToEvent(record, options) is { } watchEvent)
            {
                events.Add(watchEvent);
            }
        }

        return (events, entry.Sequence);
    }

    internal static WatchEvent? ToEvent(WatchRecord record, WatchOptions options)
    {
        if (record.Kind == WatchKind.Status)
        {
            return options.FollowStatus ? new SandboxUpdated(record.Sandbox!, record.Cursor) : null;
        }

        if (record.Kind == WatchKind.Log)
        {
            return options.FollowLogs && Passes(record, options) ? new LogReceived(record.Log!, record.Cursor) : null;
        }

        if (record.Kind == WatchKind.Event)
        {
            return options.FollowEvents && Passes(record, options) ? new PlatformEventReceived(record.Event!, record.Cursor) : null;
        }

        // Draft updates follow the status stream.
        return options.FollowStatus ? new DraftPolicyUpdated(record.Draft!) : null;
    }

    internal static bool Passes(WatchRecord record, WatchOptions options) =>
        Passes(record, options.Since, options.LogSources, options.LogMinLevel);

    internal static bool Passes(WatchRecord record, DateTimeOffset? since, IReadOnlyList<string>? sources, string? minLevel)
    {
        return !(since is { } floor && record.Time < floor)
            && (record.Log is not { } line
                || ((sources is not { Count: > 0 } || sources.Contains(line.Source, StringComparer.OrdinalIgnoreCase))
                    && (string.IsNullOrEmpty(minLevel) || Rank(line.Level) >= Rank(minLevel, strict: true))));
    }

    /// <summary>Orders log levels. A caller-supplied minimum must be known; an unknown level on a stored line counts as INFO.</summary>
    internal static int Rank(string level, bool strict = false) => level.ToUpperInvariant() switch
    {
        "TRACE" => 0,
        "DEBUG" => 1,
        "WARN" or "WARNING" => 3,
        "ERROR" => 4,
        "INFO" => 2,
        _ when strict => throw Invalid($"unknown log level '{level}'"),
        _ => 2,
    };
}

internal sealed class FakeWatchClient(FakeGateway gateway, string workspace) : ISandboxWatchClient
{
    public IAsyncEnumerable<WatchEvent> WatchAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(sandbox, options ?? new WatchOptions(), "Watch.Watch", cancellationToken);

    public IAsyncEnumerable<WatchEvent> WatchLogsAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(sandbox, (options ?? new WatchOptions()) with { FollowStatus = false, FollowLogs = true }, "Watch.WatchLogs", cancellationToken);

    public ValueTask<LogResult> GetLogsAsync(string sandbox, LogOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Watch.GetLogs", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var logs = entry.Records.Where(r => r.Kind == WatchKind.Log).ToArray();
            var lines = logs
                .Where(r => FakeGateway.Passes(r, options?.Since, options?.Sources, options?.MinLevel))
                .Select(r => r.Log!)
                .TakeLast(options is { Lines: > 0 } ? (int)options.Lines : 2000)
                .ToArray();
            return new LogResult(lines, (uint)logs.Length);
        }));
    }

    private async IAsyncEnumerable<WatchEvent> StreamAsync(
        string sandbox,
        WatchOptions options,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var (replay, position) = gateway.Run(operation, workspace, sandbox, cancellationToken, () =>
        {
            if (string.IsNullOrEmpty(options.ResumeAfterCursor))
            {
                return gateway.Begin(workspace, sandbox, options);
            }

            _ = gateway.FindSandbox(workspace, sandbox);
            return long.TryParse(options.ResumeAfterCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor)
                ? ([], cursor)
                : throw FakeGateway.Invalid("invalid resume cursor");
        });

        foreach (var watchEvent in replay)
        {
            yield return watchEvent;
        }

        while (true)
        {
            var changed = gateway.Changed;
            var batch = gateway.Collect(workspace, sandbox, position, options);
            position = batch.Position;
            foreach (var watchEvent in batch.Events)
            {
                yield return watchEvent;
            }

            if (batch.Ended)
            {
                yield break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
