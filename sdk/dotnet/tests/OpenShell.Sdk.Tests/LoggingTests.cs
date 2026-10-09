// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>A logger factory that records every message instead of writing it anywhere.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

public class LoggingTests
{
    private static OpenShellClient NewClient(FakeCallInvoker invoker, CapturingLoggerFactory? logs, bool retry = false) =>
        OpenShellClient.ForTesting(
            new OpenShellClientOptions
            {
                Address = new Uri("http://localhost"),
                LoggerFactory = logs,
                Retry = new RetryPolicy { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero, Jitter = 0 },
            },
            invoker,
            useRetry: retry);

    [Test]
    public async Task Each_retry_is_logged_with_the_method_attempt_and_status()
    {
        var logs = new CapturingLoggerFactory();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts < 3 ? throw new RpcException(new Status(StatusCode.Unavailable, "blip")) : new Wire.HealthResponse());
        using var client = NewClient(invoker, logs, retry: true);

        await client.Health.CheckAsync();

        var entries = logs.Entries.ToArray();
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries[0].Category).IsEqualTo("OpenShell.Sdk");
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Information);
        await Assert.That(entries[0].Message).Contains("Retrying Health");
        await Assert.That(entries[0].Message).Contains("attempt 2 of 3");
        await Assert.That(entries[0].Message).Contains("Unavailable");
        await Assert.That(entries[1].Message).Contains("attempt 3 of 3");
    }

    [Test]
    public async Task A_call_that_succeeds_first_time_logs_nothing()
    {
        var logs = new CapturingLoggerFactory();
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse());
        using var client = NewClient(invoker, logs, retry: true);

        await client.Health.CheckAsync();

        await Assert.That(logs.Entries.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Without_a_logger_factory_retries_still_work()
    {
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts < 2 ? throw new RpcException(new Status(StatusCode.Unavailable, "blip")) : new Wire.HealthResponse());
        using var client = NewClient(invoker, logs: null, retry: true);

        await client.Health.CheckAsync();

        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task A_watch_reconnect_is_logged()
    {
        var logs = new CapturingLoggerFactory();
        var calls = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchSandboxRequest, Wire.SandboxStreamEvent>(
            "WatchSandbox", _ => ++calls == 1 ? DropImmediately() : []);
        using var client = NewClient(invoker, logs);

        await foreach (var _ in client.Watch.WatchAsync("sb"))
        {
        }

        await Assert.That(calls).IsEqualTo(2);
        var (_, level, message) = logs.Entries.Single();
        await Assert.That(level).IsEqualTo(LogLevel.Information);
        await Assert.That(message).Contains("reconnecting");
    }

    private static IEnumerable<Wire.SandboxStreamEvent> DropImmediately()
    {
        throw new RpcException(new Status(StatusCode.Unavailable, "dropped"));
#pragma warning disable CS0162 // The iterator needs a yield to be a lazy sequence; the failure surfaces on the first MoveNext.
        yield break;
#pragma warning restore CS0162
    }
}
