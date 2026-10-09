// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace OpenShell;

/// <summary>Factory methods for the built-in <see cref="IAuthProvider"/> implementations.</summary>
public static partial class Auth
{
    /// <summary>No credentials.</summary>
    public static IAuthProvider None { get; } = new NoAuthProvider();

    /// <summary>A fixed OIDC bearer token.</summary>
    public static IAuthProvider StaticToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new BearerAuthProvider(_ => ValueTask.FromResult(token), null);
    }

    /// <summary>A bearer token fetched per request, for example from a cache that renews itself.</summary>
    public static IAuthProvider Bearer(Func<CancellationToken, ValueTask<string>> tokenSource) =>
        new BearerAuthProvider(tokenSource ?? throw new ArgumentNullException(nameof(tokenSource)), null);

    /// <summary>Adds Cloudflare Access credentials; any inner provider is applied first.</summary>
    public static IAuthProvider CloudflareAccess(string edgeToken, IAuthProvider? inner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeToken);

        return EdgeTokenPattern().IsMatch(edgeToken)
            ? new CloudflareAuthProvider(edgeToken, inner)
            : throw new OpenShellException(
                OpenShellErrorKind.InvalidConfig, "edge token must contain only cookie-safe JWT characters");
    }

    /// <summary>Adds fixed headers on top of another provider.</summary>
    public static IAuthProvider WithHeaders(IAuthProvider inner, IReadOnlyDictionary<string, string> headers) =>
        new HeaderAuthProvider(
            inner ?? throw new ArgumentNullException(nameof(inner)),
            headers ?? throw new ArgumentNullException(nameof(headers)));

    [GeneratedRegex("^[A-Za-z0-9._~-]+$")]
    private static partial Regex EdgeTokenPattern();

    private sealed class NoAuthProvider : IAuthProvider
    {
        public bool SendsCredentials => false;

        public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    internal sealed class BearerAuthProvider(
        Func<CancellationToken, ValueTask<string>> tokenSource,
        Func<CancellationToken, ValueTask<bool>>? recover) : IAuthProvider
    {
        public bool SendsCredentials => true;

        public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await tokenSource(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken) =>
            recover is null ? ValueTask.FromResult(false) : recover(cancellationToken);
    }

    private sealed class CloudflareAuthProvider(string edgeToken, IAuthProvider? inner) : IAuthProvider
    {
        public bool SendsCredentials => true;

        public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (inner is not null)
            {
                await inner.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            }

            request.Headers.Remove("cf-access-jwt-assertion");
            request.Headers.Add("cf-access-jwt-assertion", edgeToken);
            request.Headers.Remove("cookie");
            request.Headers.Add("cookie", $"CF_Authorization={edgeToken}");
        }

        public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken) =>
            inner?.TryRecoverAsync(cancellationToken) ?? ValueTask.FromResult(false);
    }

    private sealed class HeaderAuthProvider(IAuthProvider inner, IReadOnlyDictionary<string, string> headers) : IAuthProvider
    {
        public bool SendsCredentials => inner.SendsCredentials;

        public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await inner.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken) => inner.TryRecoverAsync(cancellationToken);
    }
}
