// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Sandbox watch streams and log retrieval.</summary>
public interface ISandboxWatchClient
{
    /// <summary>
    /// Streams sandbox status, logs, platform events and draft policy updates. The stream reconnects with
    /// exponential backoff when the gateway becomes unavailable and resumes from the last cursor it saw.
    /// A trimmed cursor is terminal and throws; start a new watch with an empty cursor to recover.
    /// </summary>
    IAsyncEnumerable<WatchEvent> WatchAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams only logs, platform events and warnings; status is never followed. Set
    /// <see cref="WatchOptions.FollowLogs"/> and <see cref="WatchOptions.FollowEvents"/> to choose what to receive.
    /// </summary>
    IAsyncEnumerable<WatchEvent> WatchLogsAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Fetches buffered log lines once.</summary>
    ValueTask<LogResult> GetLogsAsync(string sandbox, LogOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class SandboxWatchClient(ClientCore core, string workspace) : ISandboxWatchClient
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);

    public IAsyncEnumerable<WatchEvent> WatchAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return RunAsync(sandbox, options ?? new WatchOptions(), logsOnly: false, cancellationToken);
    }

    public IAsyncEnumerable<WatchEvent> WatchLogsAsync(string sandbox, WatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return RunAsync(sandbox, (options ?? new WatchOptions()) with { FollowStatus = false }, logsOnly: true, cancellationToken);
    }

    public async ValueTask<LogResult> GetLogsAsync(string sandbox, LogOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var request = new Wire.GetSandboxLogsRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Lines = options?.Lines ?? 0,
            MinLevel = options?.MinLevel ?? "",
        };
        if (options?.Since is { } since)
        {
            request.SinceTime = Timestamp.FromDateTimeOffset(since);
        }

        if (options?.Sources is { } sources)
        {
            request.Sources.AddRange(sources);
        }

        var r = await core.UnaryAsync(o => core.Grpc.GetSandboxLogsAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new LogResult(r.Logs.Select(ToDomain).ToArray(), r.BufferTotal);
    }

    private async IAsyncEnumerable<WatchEvent> RunAsync(
        string sandbox,
        WatchOptions options,
        bool logsOnly,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = options.ResumeAfterCursor ?? "";
        var backoff = InitialBackoff;
        var attempts = 0;
        while (true)
        {
            var request = BuildRequest(sandbox, options, cursor);
            var clean = false;
            await using var stream = core.StreamAsync(o => core.Grpc.WatchSandbox(request, o), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                Wire.SandboxStreamEvent current;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        clean = true;
                        break;
                    }

                    current = stream.Current;
                }
                catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.Unavailable && attempts < (options.MaxReconnectAttempts ?? int.MaxValue))
                {
                    attempts++;
                    break;
                }

                attempts = 0;
                backoff = InitialBackoff;
                if (Convert(current, logsOnly, ref cursor) is { } ev)
                {
                    yield return ev;
                }
            }

            if (clean)
            {
                yield break;
            }

            await Task.Delay(backoff, core.Time, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private Wire.WatchSandboxRequest BuildRequest(string sandbox, WatchOptions options, string cursor)
    {
        var request = new Wire.WatchSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            FollowStatus = options.FollowStatus,
            FollowLogs = options.FollowLogs,
            FollowEvents = options.FollowEvents,
            StopOnTerminal = options.StopOnTerminal,
            LogTailLines = options.LogTailLines,
            EventTail = options.EventTail,
            LogMinLevel = options.LogMinLevel ?? "",
            ResumeAfterCursor = cursor,
        };
        if (options.Since is { } since)
        {
            request.SinceTime = Timestamp.FromDateTimeOffset(since);
        }

        if (options.LogSources is { } sources)
        {
            request.LogSources.AddRange(sources);
        }

        return request;
    }

    private static WatchEvent? Convert(Wire.SandboxStreamEvent e, bool logsOnly, ref string cursor)
    {
        switch (e.PayloadCase)
        {
            case Wire.SandboxStreamEvent.PayloadOneofCase.Log:
                Advance(e.Cursor, ref cursor);
                return new LogReceived(ToDomain(e.Log), e.Cursor);
            case Wire.SandboxStreamEvent.PayloadOneofCase.Event:
                Advance(e.Cursor, ref cursor);
                return new PlatformEventReceived(ToDomain(e.Event), e.Cursor);
            case Wire.SandboxStreamEvent.PayloadOneofCase.Warning:
                return new WatchWarning(e.Warning.Message);
            case Wire.SandboxStreamEvent.PayloadOneofCase.Sandbox when !logsOnly:
                return new SandboxUpdated(SandboxMapping.ToDomain(e.Sandbox), e.Cursor);
            case Wire.SandboxStreamEvent.PayloadOneofCase.DraftPolicyUpdate when !logsOnly:
                var u = e.DraftPolicyUpdate;
                return new DraftPolicyUpdated(new DraftPolicyUpdate(u.DraftVersion, u.NewChunks, u.TotalPending, u.Summary));
            default:
                return null;
        }
    }

    // Cursors are opaque but ordered; only ever move forward.
    private static void Advance(string next, ref string cursor)
    {
        if (string.CompareOrdinal(next, cursor) > 0)
        {
            cursor = next;
        }
    }

    private static LogLine ToDomain(Wire.SandboxLogLine l) =>
        new(l.SandboxId, l.EventTime?.ToDateTimeOffset(), l.Level, l.Target, l.Message, l.Source, SandboxMapping.Copy(l.Fields));

    private static PlatformEvent ToDomain(Wire.PlatformEvent p) =>
        new(p.EventTime?.ToDateTimeOffset(), p.Source, p.Type, p.Reason, p.Message, SandboxMapping.Copy(p.Metadata));
}
