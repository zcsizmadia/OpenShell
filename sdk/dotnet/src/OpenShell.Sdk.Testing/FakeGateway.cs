// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using GrpcService = Openshell.V1.OpenShell;
using Wire = Openshell.V1;

namespace OpenShell.Testing;

/// <summary>
/// The in-memory state behind one or more <see cref="FakeOpenShellClient"/> views, plus the hooks tests use to
/// inspect calls, inject failures, script command results and drive time-dependent behaviour.
/// </summary>
/// <remarks>All members are thread safe.</remarks>
public sealed partial class FakeGateway : IDisposable
{
    private readonly List<FakeCall> _calls = [];
    private readonly List<Fault> _faults = [];
    private readonly List<ITimer> _timers = [];
    private readonly List<Func<FakeExecInvocation, FakeExecScript?>> _execHandlers = [];
    private readonly List<FakeInteractiveFrame> _interactive = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _counter;
    private ulong _version;
    private bool _disposed;

    /// <summary>Creates a gateway containing only the default workspace.</summary>
    public FakeGateway(FakeGatewayOptions? options = null)
    {
        Options = options ?? new FakeGatewayOptions();
        Time = Options.TimeProvider ?? TimeProvider.System;
        Health = new HealthResult(ServiceStatus.Healthy, Options.GatewayVersion);
        GatewayInfo = new GatewayInfo(ServiceStatus.Healthy, Options.GatewayVersion, [new ComputeDriverInfo("fake")], []);
        CurrentUser = new CurrentUser("fake-user", "Fake User", ["admin"], [], "fake");
        DuplexCore = new ClientCore(
            new OpenShellClientOptions { Address = new Uri("http://fake.invalid") },
            null,
            new GrpcService.OpenShellClient(new DuplexInvoker(this)));
        CreateWorkspaceLocked(Options.DefaultWorkspace, null);
        foreach (var profile in Options.BuiltinProfiles ?? [])
        {
            Profiles[new Key("", profile.Id)] = profile with { Source = profile.Source.Length > 0 ? profile.Source : "builtin", Scope = "platform" };
        }
    }

    /// <summary>The options the gateway was created with.</summary>
    public FakeGatewayOptions Options { get; }

    /// <summary>The clock in use.</summary>
    public TimeProvider Time { get; }

    /// <summary>Result of <see cref="IHealthClient.CheckAsync"/>.</summary>
    public HealthResult Health { get; set; }

    /// <summary>Result of <see cref="IHealthClient.GetGatewayInfoAsync"/>.</summary>
    public GatewayInfo GatewayInfo { get; set; }

    /// <summary>Result of <see cref="IHealthClient.GetCurrentUserAsync"/>.</summary>
    public CurrentUser CurrentUser { get; set; }

    /// <summary>A snapshot of every call recorded so far, oldest first.</summary>
    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (Sync)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The frames interactive exec sessions have sent so far, oldest first.</summary>
    public IReadOnlyList<FakeInteractiveFrame> InteractiveInput
    {
        get
        {
            lock (Sync)
            {
                return [.. _interactive];
            }
        }
    }

    internal object Sync { get; } = new();

    internal ClientCore DuplexCore { get; }

    internal Func<FakeForwardRequest, Stream, CancellationToken, Task> ForwardHandler { get; private set; } = EchoAsync;

    /// <summary>Forgets the recorded calls.</summary>
    public void ClearCalls()
    {
        lock (Sync)
        {
            _calls.Clear();
        }
    }

    /// <summary>
    /// Makes the next <paramref name="times"/> matching calls fail with <paramref name="error"/>.
    /// <paramref name="operation"/> is an exact name such as <c>Sandboxes.Create</c>, a prefix pattern such as
    /// <c>Sandboxes.*</c>, or <c>*</c> for every call. The failure is raised after the call is recorded.
    /// </summary>
    public void FailNext(string operation, OpenShellException error, int times = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        lock (Sync)
        {
            _faults.Add(new Fault(operation, error, times));
        }
    }

    /// <summary>Removes every pending injected failure.</summary>
    public void ClearFailures()
    {
        lock (Sync)
        {
            _faults.Clear();
        }
    }

    /// <summary>
    /// Registers a handler that scripts command results. Handlers registered later run first; the first one that
    /// returns a script wins. Commands nobody scripts exit with code 0 and no output.
    /// </summary>
    public void OnExec(Func<FakeExecInvocation, FakeExecScript?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Sync)
        {
            _execHandlers.Insert(0, handler);
        }
    }

    /// <summary>Removes every registered exec handler.</summary>
    public void ClearExecHandlers()
    {
        lock (Sync)
        {
            _execHandlers.Clear();
        }
    }

    /// <summary>
    /// Replaces the handler for TCP and SSH relays. It receives the sandbox side of the connection: reading the
    /// stream yields what the client sent, and writing sends data to the client. The default handler echoes.
    /// </summary>
    public void OnForward(Func<FakeForwardRequest, Stream, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Sync)
        {
            ForwardHandler = handler;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer[] timers;
        lock (Sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers];
            _timers.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        DuplexCore.Dispose();
        Signal();
    }

    internal FakeExecScript ResolveExec(Wire.ExecSandboxRequest request)
    {
        var options = new ExecOptions
        {
            WorkingDirectory = request.Workdir.Length > 0 ? request.Workdir : null,
            Environment = request.Environment.Count > 0 ? new Dictionary<string, string>(request.Environment) : null,
            Timeout = request.ExecutionTimeout?.ToTimeSpan(),
            Stdin = request.Stdin.ToByteArray(),
            Tty = request.Tty,
            NoLoginShell = request.NoLoginShell,
            RequestId = request.RequestId.Length > 0 ? request.RequestId : null,
        };
        return ResolveExec(new FakeExecInvocation(request.WorkspaceScope.Workspace, request.Sandbox, [.. request.Command], options));
    }

    internal FakeExecScript ResolveExec(FakeExecInvocation invocation)
    {
        Func<FakeExecInvocation, FakeExecScript?>[] handlers;
        lock (Sync)
        {
            handlers = [.. _execHandlers];
        }

        foreach (var handler in handlers)
        {
            if (handler(invocation) is { } script)
            {
                return script;
            }
        }

        return new FakeExecScript(0);
    }

    internal void RecordInteractive(FakeInteractiveFrame frame)
    {
        lock (Sync)
        {
            _interactive.Add(frame);
        }
    }

#pragma warning disable CA1068 // The body is last so call sites read as `Run(op, ws, target, ct, () => ...)`.
    /// <summary>Runs <paramref name="body"/> as one operation: records it, applies failures, settles timed transitions.</summary>
    internal T Run<T>(string operation, string workspace, string? target, CancellationToken cancellationToken, Func<T> body)
    {
        lock (Sync)
        {
            Begin(operation, workspace, target, cancellationToken);
            SettleLocked();
            return body();
        }
    }

    internal void Run(string operation, string workspace, string? target, CancellationToken cancellationToken, Action body) =>
        Run<object?>(operation, workspace, target, cancellationToken, () =>
        {
            body();
            return null;
        });
#pragma warning restore CA1068

    /// <summary>Records an operation without running a body, for streams that check state lazily.</summary>
    internal void Begin(string operation, string workspace, string? target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(new FakeCall(_calls.Count + 1, operation, workspace, target));
        for (var i = 0; i < _faults.Count; i++)
        {
            var fault = _faults[i];
            if (!Matches(fault.Operation, operation))
            {
                continue;
            }

            if (--fault.Remaining <= 0)
            {
                _faults.RemoveAt(i);
            }

            throw new OpenShellException(fault.Error.Kind, fault.Error.Message, fault.Error.InnerException);
        }
    }

    internal string NextId(string prefix) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}-{++_counter:D6}");

    internal ulong NextVersion() => ++_version;

    internal Task Changed
    {
        get
        {
            lock (Sync)
            {
                return _changed.Task;
            }
        }
    }

    internal void Signal()
    {
        TaskCompletionSource previous;
        lock (Sync)
        {
            previous = _changed;
            _changed = NewSignal();
        }

        previous.TrySetResult();
    }

    internal void Schedule(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        // Disposal stops every timer first, and settling after a racing callback is harmless, so no disposed check.
        var timer = Time.CreateTimer(
            _ =>
            {
                lock (Sync)
                {
                    SettleLocked();
                }
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
        lock (Sync)
        {
            _timers.Add(timer);
        }
    }

    internal static bool Matches(string pattern, string operation) =>
        pattern == "*"
        || string.Equals(pattern, operation, StringComparison.Ordinal)
        || (pattern.EndsWith(".*", StringComparison.Ordinal) && operation.StartsWith(pattern[..^1], StringComparison.Ordinal));

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task EchoAsync(FakeForwardRequest request, Stream remote, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await remote.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Fault(string operation, OpenShellException error, int remaining)
    {
        public string Operation { get; } = operation;

        public OpenShellException Error { get; } = error;

        public int Remaining { get; set; } = remaining;
    }
}
