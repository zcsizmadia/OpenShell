// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>A log line from the gateway or the sandbox supervisor.</summary>
/// <param name="SandboxId">The sandbox id.</param>
/// <param name="Time">When the line was produced.</param>
/// <param name="Level">Log level, for example <c>INFO</c>.</param>
/// <param name="Target">The emitting target.</param>
/// <param name="Message">The message.</param>
/// <param name="Source">The source, for example <c>gateway</c> or <c>sandbox</c>.</param>
/// <param name="Fields">Structured fields.</param>
public sealed record LogLine(
    string SandboxId,
    DateTimeOffset? Time,
    string Level,
    string Target,
    string Message,
    string Source,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>A platform event such as a Kubernetes or container runtime event.</summary>
/// <param name="Time">When the event occurred.</param>
/// <param name="Source">The event source, for example <c>kubernetes</c> or <c>docker</c>.</param>
/// <param name="Type">The type or severity, for example <c>Normal</c> or <c>Warning</c>.</param>
/// <param name="Reason">A short reason code.</param>
/// <param name="Message">A human-readable message.</param>
/// <param name="Metadata">Extra key-value metadata.</param>
public sealed record PlatformEvent(
    DateTimeOffset? Time,
    string Source,
    string Type,
    string Reason,
    string Message,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Notification that a sandbox draft policy changed.</summary>
/// <param name="DraftVersion">Current draft version.</param>
/// <param name="NewChunks">Chunks added by this update.</param>
/// <param name="TotalPending">Chunks awaiting approval.</param>
/// <param name="Summary">What changed.</param>
public sealed record DraftPolicyUpdate(ulong DraftVersion, uint NewChunks, uint TotalPending, string Summary);

/// <summary>An event on a sandbox watch stream.</summary>
public abstract record WatchEvent
{
    private protected WatchEvent(string cursor)
    {
        Cursor = cursor;
    }

    /// <summary>
    /// Opaque resume token. Pass it as <see cref="WatchOptions.ResumeAfterCursor"/> to continue after this event.
    /// Empty for events that are not resumable. Do not construct or parse it.
    /// </summary>
    public string Cursor { get; }
}

/// <summary>The sandbox changed.</summary>
public sealed record SandboxUpdated : WatchEvent
{
    /// <summary>Creates the event.</summary>
    public SandboxUpdated(Sandbox sandbox, string cursor)
        : base(cursor)
    {
        Sandbox = sandbox;
    }

    /// <summary>The sandbox after the change.</summary>
    public Sandbox Sandbox { get; }
}

/// <summary>A log line arrived.</summary>
public sealed record LogReceived : WatchEvent
{
    /// <summary>Creates the event.</summary>
    public LogReceived(LogLine line, string cursor)
        : base(cursor)
    {
        Line = line;
    }

    /// <summary>The log line.</summary>
    public LogLine Line { get; }
}

/// <summary>A platform event arrived.</summary>
public sealed record PlatformEventReceived : WatchEvent
{
    /// <summary>Creates the event.</summary>
    public PlatformEventReceived(PlatformEvent platformEvent, string cursor)
        : base(cursor)
    {
        Event = platformEvent;
    }

    /// <summary>The platform event.</summary>
    public PlatformEvent Event { get; }
}

/// <summary>A recoverable loss on the stream, such as dropped lines. The stream continues.</summary>
public sealed record WatchWarning : WatchEvent
{
    /// <summary>Creates the event.</summary>
    public WatchWarning(string message)
        : base("")
    {
        Message = message;
    }

    /// <summary>The warning text.</summary>
    public string Message { get; }
}

/// <summary>The draft policy changed.</summary>
public sealed record DraftPolicyUpdated : WatchEvent
{
    /// <summary>Creates the event.</summary>
    public DraftPolicyUpdated(DraftPolicyUpdate update)
        : base("")
    {
        Update = update;
    }

    /// <summary>The update.</summary>
    public DraftPolicyUpdate Update { get; }
}

/// <summary>Options for <see cref="ISandboxWatchClient.WatchAsync"/> and <see cref="ISandboxWatchClient.WatchLogsAsync"/>.</summary>
public sealed record WatchOptions
{
    /// <summary>Stream sandbox status changes. Defaults to true for <c>WatchAsync</c>; <c>WatchLogsAsync</c> forces false.</summary>
    public bool FollowStatus { get; init; } = true;

    /// <summary>Stream log lines.</summary>
    public bool FollowLogs { get; init; }

    /// <summary>Stream platform events.</summary>
    public bool FollowEvents { get; init; }

    /// <summary>End the stream when the sandbox reaches a terminal phase.</summary>
    public bool StopOnTerminal { get; init; }

    /// <summary>Number of trailing log lines to replay first.</summary>
    public uint LogTailLines { get; init; }

    /// <summary>Number of trailing platform events to replay first.</summary>
    public uint EventTail { get; init; }

    /// <summary>Only include records at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only include these log sources, for example <c>gateway</c> or <c>sandbox</c>.</summary>
    public IReadOnlyList<string>? LogSources { get; init; }

    /// <summary>Minimum log level, for example <c>WARN</c>.</summary>
    public string? LogMinLevel { get; init; }

    /// <summary>Cursor from a previous event on the same sandbox; empty starts from the tail.</summary>
    public string? ResumeAfterCursor { get; init; }

    /// <summary>
    /// Maximum consecutive reconnect attempts after the connection drops. Null retries until cancelled.
    /// The counter resets whenever an event arrives.
    /// </summary>
    public int? MaxReconnectAttempts { get; init; }
}

/// <summary>Options for <see cref="ISandboxWatchClient.GetLogsAsync"/>.</summary>
public sealed record LogOptions
{
    /// <summary>Maximum lines to return; zero uses the gateway default (2000).</summary>
    public uint Lines { get; init; }

    /// <summary>Only include logs at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only include these sources.</summary>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>Minimum level, for example <c>WARN</c>.</summary>
    public string? MinLevel { get; init; }
}

/// <summary>A one-shot log fetch.</summary>
/// <param name="Lines">Log lines in chronological order.</param>
/// <param name="BufferTotal">Total lines in the gateway buffer for the sandbox.</param>
public sealed record LogResult(IReadOnlyList<LogLine> Lines, uint BufferTotal);
