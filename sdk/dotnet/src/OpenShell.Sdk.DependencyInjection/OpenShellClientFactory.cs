// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenShell.DependencyInjection;

/// <summary>Creates and caches one <see cref="IOpenShellClient"/> per configured gateway name.</summary>
public interface IOpenShellClientFactory
{
    /// <summary>
    /// Returns the client for <paramref name="name"/>, creating it on first use. Pass <see langword="null"/> for the
    /// default (unnamed) registration. Clients are shared and owned by the factory; do not dispose them.
    /// </summary>
    IOpenShellClient Create(string? name = null);
}

internal sealed class OpenShellClientFactory(IOptionsMonitor<OpenShellOptions> options, ILoggerFactory? loggerFactory = null)
    : IOpenShellClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<OpenShellClient>> _clients = new(StringComparer.Ordinal);
    private int _disposed;

    public IOpenShellClient Create(string? name = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = name ?? Options.DefaultName;
        return _clients.GetOrAdd(key, static (k, state) => new Lazy<OpenShellClient>(() => state.Build(k)), this).Value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var client in _clients.Values.Where(l => l.IsValueCreated))
        {
            client.Value.Dispose();
        }

        _clients.Clear();
    }

    private OpenShellClient Build(string name) =>
        new(options.Get(name).ToClientOptions(loggerFactory));
}
