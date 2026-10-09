// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace OpenShell.Tests.Support;

/// <summary>A temporary directory removed on disposal.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshell-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Make(params string[] parts)
    {
        var dir = Combine(parts);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>The shared OAuth client-credentials conformance fixture used by every SDK.</summary>
internal sealed class ConformanceFixture
{
    private ConformanceFixture(JsonElement root) => Root = root;

    public JsonElement Root { get; }

    public static ConformanceFixture Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sdk", "conformance", "oauth-client-credentials.json");
            if (File.Exists(candidate))
            {
                return new ConformanceFixture(JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone());
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("sdk/conformance/oauth-client-credentials.json not found above " + AppContext.BaseDirectory);
    }

    public string Str(string section, string name) => Root.GetProperty(section).GetProperty(name).GetString()!;

    public string[] Strings(string section, string name) =>
        Root.GetProperty(section).GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    public int Int(string section, string name) => Root.GetProperty(section).GetProperty(name).GetInt32();

    public int[] Ints(string section, string name) =>
        Root.GetProperty(section).GetProperty(name).EnumerateArray().Select(e => e.GetInt32()).ToArray();

    /// <summary>The raw JSON text of each <c>invalid_expires_in</c> entry, as it appears in the wire response.</summary>
    public string[] InvalidExpiresInRaw() =>
        Root.GetProperty("expiry").GetProperty("invalid_expires_in").EnumerateArray().Select(e => e.GetRawText()).ToArray();
}

internal static class TestPorts
{
    /// <summary>Reserves then releases an ephemeral loopback port.</summary>
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal static class TimeExtensions
{
    /// <summary>Advances a fake clock in small steps until the task finishes, yielding between steps.</summary>
    public static async Task AdvanceUntilDone(this FakeTimeProvider time, Task task, TimeSpan? step = null, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
    }
}
