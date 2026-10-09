// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

internal sealed class FakeHealthClient(FakeGateway gateway) : IHealthClient
{
    public ValueTask<HealthResult> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(gateway.Run("Health.Check", "", null, cancellationToken, () => gateway.Health));

    public ValueTask<GatewayInfo> GetGatewayInfoAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(gateway.Run("Health.GetGatewayInfo", "", null, cancellationToken, () => gateway.GatewayInfo));

    public ValueTask<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(gateway.Run("Health.GetCurrentUser", "", null, cancellationToken, () => gateway.CurrentUser));
}
