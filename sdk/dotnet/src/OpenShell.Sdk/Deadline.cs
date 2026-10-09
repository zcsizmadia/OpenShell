// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>A cancellation token that fires on a timeout or when the caller cancels, with backoff sleeping.</summary>
internal sealed class Deadline : IDisposable
{
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _caller;

    public Deadline(TimeProvider time, TimeSpan timeout, CancellationToken caller)
    {
        _time = time;
        _caller = caller;
        _timeout = new CancellationTokenSource(timeout, time);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>True when the deadline, not the caller, ended the wait.</summary>
    public bool TimedOut => _timeout.IsCancellationRequested && !_caller.IsCancellationRequested;

    public static OpenShellException Expired(string message) => new(OpenShellErrorKind.DeadlineExceeded, message);

    /// <summary>Sleeps for <paramref name="delay"/>, then returns the next (doubled, capped) delay.</summary>
    public async ValueTask<TimeSpan> SleepAsync(TimeSpan delay, TimeSpan max, string timeoutMessage)
    {
        try
        {
            await Task.Delay(delay, _time, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (TimedOut)
        {
            throw Expired(timeoutMessage);
        }

        var next = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, max.Ticks));
        return next;
    }

    public void Dispose()
    {
        _cts.Dispose();
        _timeout.Dispose();
    }
}
