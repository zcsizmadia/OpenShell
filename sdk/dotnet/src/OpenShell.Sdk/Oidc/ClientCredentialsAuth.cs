// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Oidc;

/// <summary>
/// A renewable OAuth 2.0 client-credentials bearer provider. Tokens and secrets stay in memory;
/// renewal is single-flight, proactive (30 s before expiry) and reactive (after <c>Unauthenticated</c>).
/// </summary>
public sealed class ClientCredentialsAuth : IAuthProvider, IDisposable
{
    private readonly RefreshableToken _token;

    private ClientCredentialsAuth(RefreshableToken token)
    {
        _token = token;
    }

    /// <inheritdoc />
    public bool SendsCredentials => true;

    /// <summary>Creates a provider; configuration problems surface here, network problems on first use.</summary>
    public static ClientCredentialsAuth Create(OidcOptions options)
    {
        var resolved = OidcResolved.Create(options, requireSecret: true);
        var stale = new AccessToken(string.Empty, DateTimeOffset.UnixEpoch);
        var token = new RefreshableToken(
            stale,
            async ct =>
            {
                using var deadline = new Deadline(resolved.Time, options.Timeout, ct);
                try
                {
                    var tokens = await OidcFlows.ExchangeClientCredentialsAsync(resolved, requirePositiveExpiry: true, deadline.Token).ConfigureAwait(false);
                    return tokens.ToAccessToken();
                }
                catch (OperationCanceledException) when (deadline.TimedOut)
                {
                    throw new OidcException(OidcFailure.Timeout, "client credentials exchange timed out");
                }
            },
            leeway: TimeSpan.FromSeconds(30),
            timeProvider: resolved.Time);
        return new ClientCredentialsAuth(token);
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _token.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken) => _token.RefreshNowAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _token.Dispose();

    /// <inheritdoc />
    public override string ToString() => "OpenShell.Oidc.ClientCredentialsAuth";
}
