// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace OpenShell.Oidc;

/// <summary>The <c>oidc_token.json</c> cache shared with the OpenShell CLI.</summary>
internal static class OidcTokenFile
{
    public const string FileName = "oidc_token.json";
    private static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(10);

    /// <summary>Reads the cached tokens regardless of expiry; null when absent or without an access token.</summary>
    public static OidcTokens? ReadRaw(string directory)
    {
        var path = Path.Combine(directory, FileName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"cannot read {FileName}", e);
        }

        OidcTokenBundleDto? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize(text, SdkJsonContext.Default.OidcTokenBundleDto);
        }
        catch (JsonException e)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"invalid JSON in {FileName}", e);
        }

        if (bundle is null || string.IsNullOrEmpty(bundle.AccessToken))
        {
            return null;
        }

        DateTimeOffset? expiry = null;
        if (!string.IsNullOrEmpty(bundle.Expiry))
        {
            if (!DateTimeOffset.TryParse(bundle.Expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                throw new OidcException(OidcFailure.TokenPersist, $"invalid expiry format in {FileName}");
            }

            expiry = parsed;
        }

        return new OidcTokens(
            bundle.AccessToken,
            string.IsNullOrEmpty(bundle.RefreshToken) ? null : bundle.RefreshToken,
            "Bearer",
            expiry);
    }

    /// <summary>Reads the cached tokens, or null when absent or expired (with a 10 s leeway).</summary>
    public static OidcTokens? ReadValid(string directory, TimeProvider time)
    {
        var tokens = ReadRaw(directory);
        return tokens is { ExpiresAt: { } exp } && time.GetUtcNow() > exp - ExpiryLeeway ? null : tokens;
    }

    /// <summary>Writes the tokens atomically with owner-only permissions where the OS supports them.</summary>
    public static void Write(string directory, OidcTokens tokens, TimeProvider time)
    {
        var bundle = new OidcTokenBundleDto
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
        };
        if (tokens.ExpiresAt is { } expiry)
        {
            bundle.Expiry = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var remaining = expiry - time.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                bundle.ExpiresIn = (long)remaining.TotalSeconds;
            }
        }

        var path = Path.Combine(directory, FileName);
        var temp = Path.Combine(directory, $".oidc-token-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, bundle, SdkJsonContext.Default.OidcTokenBundleDto);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"failed to write {FileName}", e);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temporary file.
            }
        }
    }
}
