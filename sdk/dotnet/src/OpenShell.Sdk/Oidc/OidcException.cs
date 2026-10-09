// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Oidc;

/// <summary>Identifies which OIDC step failed.</summary>
public enum OidcFailure
{
    /// <summary>Required OIDC settings are missing or malformed.</summary>
    Config,

    /// <summary>Provider discovery failed or returned an untrusted document.</summary>
    Discovery,

    /// <summary>The authorization-code exchange failed.</summary>
    AuthCode,

    /// <summary>The device-code flow failed.</summary>
    DeviceCode,

    /// <summary>The client-credentials exchange failed.</summary>
    ClientCredentials,

    /// <summary>The refresh-token exchange failed.</summary>
    Refresh,

    /// <summary>The interactive login did not finish in time.</summary>
    Timeout,

    /// <summary>The loopback callback server failed.</summary>
    CallbackServer,

    /// <summary>The on-disk token cache could not be read or written.</summary>
    TokenPersist,
}

/// <summary>
/// An OIDC failure. Messages never contain client secrets, tokens, or provider response bodies.
/// </summary>
public sealed class OidcException : OpenShellException
{
    /// <summary>Creates an OIDC exception.</summary>
    public OidcException(OidcFailure failure, string message, Exception? innerException = null)
        : base(KindFor(failure), message, innerException)
    {
        Failure = failure;
    }

    /// <summary>The step that failed.</summary>
    public OidcFailure Failure { get; }

    private static OpenShellErrorKind KindFor(OidcFailure failure) => failure switch
    {
        OidcFailure.Config => OpenShellErrorKind.InvalidConfig,
        OidcFailure.Timeout => OpenShellErrorKind.DeadlineExceeded,
        OidcFailure.Discovery => OpenShellErrorKind.Auth,
        _ => OpenShellErrorKind.Auth,
    };
}
