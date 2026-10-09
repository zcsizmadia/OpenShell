// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace OpenShell.Oidc;

/// <summary>Opens the authorization URL in the user's browser.</summary>
internal static class OidcBrowser
{
    /// <summary>The executable and arguments that open <paramref name="url"/> on the given OS.</summary>
    public static (string FileName, string[] Arguments) CommandFor(string os, string url) => os switch
    {
        "macos" => ("open", [url]),
        "windows" => ("rundll32", ["url.dll,FileProtocolHandler", url]),
        _ => ("xdg-open", [url]),
    };

    public static string CurrentOs() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Launches the system browser. Only http and https URLs are ever opened.</summary>
    [ExcludeFromCodeCoverage(Justification = "Spawns an OS process; the command line is covered by CommandFor tests.")]
    public static ValueTask OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(url);
        var (fileName, arguments) = CommandFor(CurrentOs(), url.AbsoluteUri);
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("browser did not start");
        return ValueTask.CompletedTask;
    }

    public static void Require(Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("refusing to open a non-HTTP URL");
        }
    }
}
