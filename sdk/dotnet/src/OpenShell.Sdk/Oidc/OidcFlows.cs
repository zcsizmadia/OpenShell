// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenShell.Oidc;

/// <summary>Protocol-level token exchanges. Errors never carry secrets, tokens or provider text.</summary>
internal static class OidcFlows
{
    public const string OutOfBandRedirect = "urn:ietf:wg:oauth:2.0:oob";

    public static async Task<OidcTokens> ExchangeClientCredentialsAsync(OidcResolved cfg, bool requirePositiveExpiry, CancellationToken ct)
    {
        var secret = cfg.ClientSecret;
        if (cfg.SecretProvider is { } provider)
        {
            try
            {
                secret = await provider(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Deliberately drop the cause: it may quote the secret store's response.
                throw new OidcException(OidcFailure.ClientCredentials, "client secret provider failed");
            }
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new OidcException(OidcFailure.ClientCredentials, "client secret must not be empty");
        }

        var metadata = await cfg.Discovery.DiscoverAsync(cfg.Issuer, ct).ConfigureAwait(false);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", cfg.ClientId),
            new("client_secret", secret),
        };
        if (cfg.ExplicitScopes.Count > 0)
        {
            fields.Add(new("scope", string.Join(' ', cfg.ExplicitScopes)));
        }

        if (cfg.Audience is not null)
        {
            fields.Add(new("audience", cfg.Audience));
        }

        return await PostTokenAsync(cfg, metadata.TokenEndpoint, fields, OidcFailure.ClientCredentials, requirePositiveExpiry, includeErrorCode: false, ct).ConfigureAwait(false);
    }

    public static async Task<OidcTokens> RefreshAsync(OidcResolved cfg, string refreshToken, CancellationToken ct)
    {
        var metadata = await cfg.Discovery.DiscoverAsync(cfg.Issuer, ct).ConfigureAwait(false);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", cfg.ClientId),
        };
        var secret = cfg.ClientSecret;
        if (cfg.SecretProvider is { } provider)
        {
            try
            {
                secret = await provider(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new OidcException(OidcFailure.Refresh, "client secret provider failed");
            }
        }

        if (!string.IsNullOrEmpty(secret))
        {
            fields.Add(new("client_secret", secret));
        }

        if (cfg.ExplicitScopes.Count > 0)
        {
            fields.Add(new("scope", string.Join(' ', cfg.ExplicitScopes)));
        }

        return await PostTokenAsync(cfg, metadata.TokenEndpoint, fields, OidcFailure.Refresh, requirePositiveExpiry: false, includeErrorCode: true, ct).ConfigureAwait(false);
    }

    public static Task<OidcTokens> ExchangeCodeAsync(
        OidcResolved cfg, string tokenEndpoint, string code, string redirectUri, string? verifier, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", cfg.ClientId),
            new("redirect_uri", redirectUri),
        };
        if (!string.IsNullOrEmpty(verifier))
        {
            fields.Add(new("code_verifier", verifier));
        }

        return PostTokenAsync(cfg, tokenEndpoint, fields, OidcFailure.AuthCode, requirePositiveExpiry: false, includeErrorCode: true, ct);
    }

    public static async Task<OidcTokens> DeviceLoginAsync(OidcResolved cfg, CancellationToken ct)
    {
        var metadata = await cfg.Discovery.DiscoverAsync(cfg.Issuer, ct).ConfigureAwait(false);
        if (metadata.DeviceAuthorizationEndpoint is null)
        {
            throw new OidcException(OidcFailure.DeviceCode, "provider does not support device authorization (no device_authorization_endpoint in discovery)");
        }

        var device = await RequestDeviceCodeAsync(cfg, metadata.DeviceAuthorizationEndpoint, ct).ConfigureAwait(false);
        var display = cfg.Options.DisplayDeviceCode;
        if (display is not null)
        {
            display(device.VerificationUri!, device.UserCode!);
        }
        else
        {
            Console.Out.WriteLine($"To sign in, visit: {device.VerificationUri}");
            Console.Out.WriteLine($"Enter code: {device.UserCode}");
        }

        using var expiry = device.ExpiresIn > 0
            ? new Deadline(cfg.Time, TimeSpan.FromSeconds(device.ExpiresIn), ct)
            : null;
        var token = expiry?.Token ?? ct;
        var interval = device.Interval < 1 ? 5 : device.Interval;
        try
        {
            return await PollDeviceAsync(cfg, metadata.TokenEndpoint, device.DeviceCode!, interval, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry is { TimedOut: true })
        {
            throw new OidcException(OidcFailure.Timeout, "device code expired before sign-in completed");
        }
    }

    public static string CodeChallengeS256(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static Uri BuildAuthorizationUrl(string endpoint, string clientId, string redirectUri, string state, string? challenge, IReadOnlyList<string> scopes)
    {
        var query = new StringBuilder();
        Append(query, "response_type", "code");
        Append(query, "client_id", clientId);
        Append(query, "redirect_uri", redirectUri);
        Append(query, "state", state);
        Append(query, "scope", string.Join(' ', scopes));
        if (challenge is not null)
        {
            Append(query, "code_challenge", challenge);
            Append(query, "code_challenge_method", "S256");
        }

        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0 ? query.ToString() : existing + "&" + query;
        return builder.Uri;
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
        {
            sb.Append('&');
        }

        sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<OidcTokens> PostTokenAsync(
        OidcResolved cfg,
        string endpoint,
        List<KeyValuePair<string, string>> fields,
        OidcFailure failure,
        bool requirePositiveExpiry,
        bool includeErrorCode,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = OidcHttp.Form(fields) };
        var (status, body) = await OidcHttp.SendAsync(cfg.Http, request, failure, "token", ct).ConfigureAwait(false);
        return OidcHttp.ParseTokens(status, body, failure, requirePositiveExpiry, cfg.Time, includeErrorCode);
    }

    private static async Task<DeviceAuthorizationDto> RequestDeviceCodeAsync(OidcResolved cfg, string endpoint, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>> { new("client_id", cfg.ClientId) };
        var scopes = cfg.InteractiveScopes;
        if (scopes.Count > 0)
        {
            fields.Add(new("scope", string.Join(' ', scopes)));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = OidcHttp.Form(fields) };
        var (status, body) = await OidcHttp.SendAsync(cfg.Http, request, OidcFailure.DeviceCode, "device authorization", ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw new OidcException(OidcFailure.DeviceCode, $"device authorization endpoint returned HTTP {(int)status}");
        }

        DeviceAuthorizationDto? device;
        try
        {
            device = JsonSerializer.Deserialize(body, SdkJsonContext.Default.DeviceAuthorizationDto);
        }
        catch (JsonException)
        {
            throw new OidcException(OidcFailure.DeviceCode, "invalid device authorization response JSON");
        }

        return device switch
        {
            null or { DeviceCode: null or "" } or { UserCode: null or "" } =>
                throw new OidcException(OidcFailure.DeviceCode, "device authorization response missing device_code or user_code"),
            { VerificationUri: null or "" } =>
                throw new OidcException(OidcFailure.DeviceCode, "device authorization response missing verification_uri"),
            _ => device,
        };
    }

    private static async Task<OidcTokens> PollDeviceAsync(OidcResolved cfg, string tokenEndpoint, string deviceCode, long interval, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cfg.Time, ct).ConfigureAwait(false);

            var fields = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new("device_code", deviceCode),
                new("client_id", cfg.ClientId),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = OidcHttp.Form(fields) };
            var (status, body) = await OidcHttp.SendAsync(cfg.Http, request, OidcFailure.DeviceCode, "token", ct).ConfigureAwait(false);

            using var doc = OidcHttp.ParseObject(body, OidcFailure.DeviceCode);
            var error = OidcHttp.GetString(doc.RootElement, "error");
            switch (error)
            {
                case null:
                    return OidcHttp.ParseTokens(status, body, OidcFailure.DeviceCode, requirePositiveExpiry: false, cfg.Time);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new OidcException(OidcFailure.DeviceCode, "device code expired");
                case "access_denied":
                    throw new OidcException(OidcFailure.DeviceCode, "access denied by user");
                default:
                    var code = OidcHttp.ErrorCode(doc.RootElement);
                    throw new OidcException(OidcFailure.DeviceCode, code is null ? "device code exchange failed" : $"device code exchange failed: {code}");
            }
        }
    }

    public static string FormatPort(int port) => port.ToString(CultureInfo.InvariantCulture);
}
