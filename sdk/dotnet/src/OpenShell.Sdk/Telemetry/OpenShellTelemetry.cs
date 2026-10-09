// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenShell;

/// <summary>
/// Names of the SDK's diagnostics sources. Nothing is recorded until a listener subscribes, so the instrumentation
/// costs nothing by default. With OpenTelemetry, call <c>AddSource(OpenShellTelemetry.ActivitySourceName)</c> on the
/// tracer provider and <c>AddMeter(OpenShellTelemetry.MeterName)</c> on the meter provider.
/// </summary>
/// <remarks>
/// Spans and metrics follow the OpenTelemetry gRPC client conventions: span name <c>{service}/{method}</c>, kind
/// client, and the attributes <c>rpc.system</c>, <c>rpc.service</c>, <c>rpc.method</c> and
/// <c>rpc.grpc.status_code</c>. The histogram <c>rpc.client.duration</c> is recorded in seconds. Request and
/// response payloads, metadata, and credentials are never recorded.
/// </remarks>
public static class OpenShellTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> that emits one client span per RPC.</summary>
    public const string ActivitySourceName = "OpenShell.Sdk";

    /// <summary>The name of the <see cref="Meter"/> that emits RPC client metrics.</summary>
    public const string MeterName = "OpenShell.Sdk";

    private static readonly string? Version = typeof(OpenShellTelemetry).Assembly.GetName().Version?.ToString();

    internal static ActivitySource Source { get; } = new(ActivitySourceName, Version);

    internal static Histogram<double> Duration { get; } = new Meter(MeterName, Version).CreateHistogram<double>(
        "rpc.client.duration",
        unit: "s",
        description: "Duration of outgoing OpenShell gateway RPCs.");
}
