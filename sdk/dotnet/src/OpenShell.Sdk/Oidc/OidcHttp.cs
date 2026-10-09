// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenShell.Oidc;

/// <summary>Bounded HTTP and token-response handling shared by all flows.</summary>
internal static partial class OidcHttp
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365 * 100);

    /// <summary>Shared client that never follows redirects, so a hostile redirect cannot capture a form body.</summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        OidcFailure failure,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > OidcDiscovery.MaxResponseBytes)
                {
                    throw new OidcException(failure, $"{what} response is too large");
                }

                buffer.Write(chunk, 0, read);
            }

            return (response.StatusCode, buffer.ToArray());
        }
        catch (HttpRequestException e)
        {
            throw new OidcException(failure, $"{what} request failed", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OidcException(failure, $"{what} request timed out", e);
        }
    }

    public static FormUrlEncodedContent Form(IEnumerable<KeyValuePair<string, string>> fields) => new(fields);

    /// <summary>Parses a token endpoint response without ever echoing provider text into the error.</summary>
    public static OidcTokens ParseTokens(
        HttpStatusCode status,
        byte[] body,
        OidcFailure failure,
        bool requirePositiveExpiry,
        TimeProvider time,
        bool includeErrorCode = true)
    {
        using var doc = ParseObject(body, failure);
        var root = doc.RootElement;

        if (status != HttpStatusCode.OK || root.TryGetProperty("error", out _))
        {
            throw new OidcException(failure, $"provider rejected the exchange (HTTP {(int)status}){(includeErrorCode ? ErrorSuffix(root) : "")}");
        }

        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new OidcException(failure, "token response missing access_token");
        }

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var expiresIn))
        {
            if (expiresIn.ValueKind != JsonValueKind.Number
                || !expiresIn.TryGetDouble(out var seconds)
                || !double.IsFinite(seconds)
                || seconds <= 0)
            {
                throw new OidcException(failure, "token response requires a positive expires_in");
            }

            expiresAt = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(seconds, MaxLifetime.TotalSeconds));
        }
        else if (requirePositiveExpiry)
        {
            throw new OidcException(failure, "token response requires a positive expires_in");
        }

        return new OidcTokens(accessToken, GetString(root, "refresh_token"), GetString(root, "token_type"), expiresAt);
    }

    public static JsonDocument ParseObject(byte[] body, OidcFailure failure)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new OidcException(failure, "invalid response JSON");
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new OidcException(failure, "invalid response JSON");
        }

        return doc;
    }

    public static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The provider's RFC 6749 <c>error</c> code, when it looks like one; descriptions are dropped.</summary>
    public static string? ErrorCode(JsonElement root)
    {
        var code = GetString(root, "error");
        return code is not null && ErrorCodePattern().IsMatch(code) ? code : null;
    }

    private static string ErrorSuffix(JsonElement root) => ErrorCode(root) is { } code ? $": {code}" : "";

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex ErrorCodePattern();

}
