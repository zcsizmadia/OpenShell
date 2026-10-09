// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>Supplies per-request authentication headers.</summary>
public interface IAuthProvider
{
    /// <summary>True when the provider attaches credentials, so transport safety checks apply.</summary>
    bool SendsCredentials { get; }

    /// <summary>Adds authentication headers to an outgoing request.</summary>
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>
    /// Called after the gateway answered <c>Unauthenticated</c>. Returns true when credentials
    /// were renewed and the call is worth retrying once.
    /// </summary>
    ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
