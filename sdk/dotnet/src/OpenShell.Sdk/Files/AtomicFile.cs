// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>Moves a fully written temporary file over its destination.</summary>
internal static class AtomicFile
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int MaxAttempts = 10;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/>. On Windows, antivirus and the search
    /// indexer briefly open files that were just written, which makes the replace fail with access-denied or a
    /// sharing violation; those are retried for about a second before the last error is thrown.
    /// </summary>
    public static void Replace(string source, string destination)
    {
        var delay = TimeSpan.FromMilliseconds(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < MaxAttempts && IsTransientOnWindows(e))
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
            }
        }
    }

    private static bool IsTransientOnWindows(Exception e) =>
        OperatingSystem.IsWindows()
        && (e is UnauthorizedAccessException
            || (e is IOException io && io.HResult is ErrorSharingViolation or ErrorLockViolation));
}
