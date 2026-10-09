// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>A token with an optional absolute expiry.</summary>
public sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt = null);

/// <summary>Obtains a fresh <see cref="AccessToken"/>, for example through an OIDC refresh grant.</summary>
public delegate ValueTask<AccessToken> TokenRefresher(CancellationToken cancellationToken);

/// <summary>
/// Caches an access token and renews it single-flight, proactively before expiry and reactively
/// after the gateway rejects it.
/// </summary>
public sealed class RefreshableToken : IDisposable
{
    private readonly TokenRefresher _refresher;
    private readonly TimeSpan _leeway;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken _current;

    /// <summary>Creates a refreshable token starting from <paramref name="initial"/>.</summary>
    public RefreshableToken(AccessToken initial, TokenRefresher refresher, TimeSpan? leeway = null, TimeProvider? timeProvider = null)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _leeway = leeway ?? TimeSpan.FromSeconds(30);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns a valid token, refreshing first when it is expired or about to expire.</summary>
    public async ValueTask<string> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = Volatile.Read(ref _current);

        return IsStale(snapshot)
            ? await RefreshAsync(snapshot, force: false, cancellationToken).ConfigureAwait(false)
            : snapshot.Value;
    }

    /// <summary>
    /// Forces a refresh unless another caller already replaced the rejected token.
    /// Returns true when a different token is now in place.
    /// </summary>
    public async ValueTask<bool> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = Volatile.Read(ref _current);
        var after = await RefreshAsync(snapshot, force: true, cancellationToken).ConfigureAwait(false);
        return !string.Equals(snapshot.Value, after, StringComparison.Ordinal);
    }

    /// <summary>Exposes this token as an <see cref="IAuthProvider"/> that recovers from <c>Unauthenticated</c>.</summary>
    public IAuthProvider AsAuthProvider() =>
        new Auth.BearerAuthProvider(GetAsync, RefreshNowAsync);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool IsStale(AccessToken token) =>
        token.ExpiresAt is { } exp && exp - _leeway <= _time.GetUtcNow();

    private async ValueTask<string> RefreshAsync(AccessToken seen, bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = Volatile.Read(ref _current);

            // Another caller refreshed while this one waited for the gate.
            if (!ReferenceEquals(latest, seen) && !IsStale(latest))
            {
                return latest.Value;
            }

            if (!force && !IsStale(latest))
            {
                return latest.Value;
            }

            AccessToken next;
            try
            {
                next = await _refresher(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OpenShellException)
            {
                throw new OpenShellException(OpenShellErrorKind.Auth, "token refresh failed", e);
            }

            Volatile.Write(ref _current, next);
            return next.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
