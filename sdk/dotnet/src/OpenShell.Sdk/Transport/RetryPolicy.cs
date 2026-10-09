// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;

namespace OpenShell;

/// <summary>
/// Retries transient gateway failures with exponential backoff and jitter.
/// Only unary calls are retried here; streaming operations such as watches reconnect on their own.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>No retries.</summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>A sensible default: up to 4 attempts for idempotent reads on <c>Unavailable</c>.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Total attempts including the first. 1 disables retries.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Upper bound for any single delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Growth factor between delays.</summary>
    public double BackoffMultiplier { get; init; } = 2;

    /// <summary>Fraction (0 to 1) of each delay that is randomized, to avoid synchronized retries.</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>Status codes worth retrying. Defaults to <c>Unavailable</c> only.</summary>
    public IReadOnlySet<StatusCode> RetryableStatusCodes { get; init; } = new HashSet<StatusCode> { StatusCode.Unavailable };

    /// <summary>
    /// Decides whether an RPC is safe to repeat, given its name (for example <c>GetSandbox</c>).
    /// The default repeats reads: <c>Get*</c>, <c>List*</c> and <c>Health</c>. Writes are never repeated by default
    /// because a lost response could duplicate a side effect; supply request ids and widen this predicate to opt in.
    /// </summary>
    public Func<string, bool> IsIdempotent { get; init; } = DefaultIsIdempotent;

    internal static bool DefaultIsIdempotent(string rpcName) =>
        rpcName == "Health"
        || rpcName.StartsWith("Get", StringComparison.Ordinal)
        || rpcName.StartsWith("List", StringComparison.Ordinal);

    internal bool IsEnabled => MaxAttempts > 1;

    internal TimeSpan DelayBefore(int retryNumber, Func<double> nextRandom)
    {
        var factor = Math.Pow(BackoffMultiplier, retryNumber - 1);
        var ticks = Math.Min(InitialBackoff.Ticks * factor, MaxBackoff.Ticks);
        var spread = ticks * Math.Clamp(Jitter, 0, 1);
        return TimeSpan.FromTicks((long)Math.Max(0, ticks - spread + (nextRandom() * 2 * spread)));
    }
}

/// <summary>Wraps a <see cref="CallInvoker"/> so idempotent unary calls survive transient failures.</summary>
internal sealed class RetryCallInvoker(CallInvoker inner, RetryPolicy policy, TimeProvider time, Func<double>? random = null) : CallInvoker
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        if (!policy.IsEnabled || !policy.IsIdempotent(method.Name))
        {
            return inner.AsyncUnaryCall(method, host, options, request);
        }

        var state = new Attempts<TResponse>();
        var response = RunAsync(method, host, options, request, state);
        return new AsyncUnaryCall<TResponse>(
            response,
            state.Headers.Task,
            () => state.Current!.GetStatus(),
            () => state.Current!.GetTrailers(),
            () => state.Current?.Dispose());
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        inner.AsyncServerStreamingCall(method, host, options, request);

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        inner.BlockingUnaryCall(method, host, options, request);

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        inner.AsyncClientStreamingCall(method, host, options);

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        inner.AsyncDuplexStreamingCall(method, host, options);

    private async Task<TResponse> RunAsync<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request, Attempts<TResponse> state)
        where TRequest : class
        where TResponse : class
    {
        for (var attempt = 1; ; attempt++)
        {
            state.Current?.Dispose();
            var call = inner.AsyncUnaryCall(method, host, options, request);
            state.Current = call;
            try
            {
                var response = await call.ResponseAsync.ConfigureAwait(false);
                state.Headers.TrySetResult(await call.ResponseHeadersAsync.ConfigureAwait(false));
                return response;
            }
            catch (RpcException e) when (CanRetry(e, attempt, options, out var delay))
            {
                await Task.Delay(delay, time, options.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                state.Headers.TrySetException(e);
                throw;
            }
        }
    }

    private bool CanRetry(RpcException e, int attempt, CallOptions options, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (attempt >= policy.MaxAttempts
            || !policy.RetryableStatusCodes.Contains(e.StatusCode)
            || options.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        delay = policy.DelayBefore(attempt, _random);

        // Never retry past the caller's own deadline.
        return options.Deadline is not { } deadline || time.GetUtcNow().UtcDateTime + delay < deadline;
    }

    private sealed class Attempts<TResponse>
    {
        public AsyncUnaryCall<TResponse>? Current { get; set; }

        public TaskCompletionSource<Metadata> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
