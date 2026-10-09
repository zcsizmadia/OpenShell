// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GoogleRpc = Google.Rpc;

namespace OpenShell;

/// <summary>A request field the gateway rejected.</summary>
/// <param name="Field">The field path, for example <c>spec.template.image</c>.</param>
/// <param name="Description">Why the field was rejected.</param>
public sealed record FieldViolation(string Field, string Description);

/// <summary>A stable error reason scoped to the service that produced it.</summary>
/// <param name="Reason">A machine-readable reason, for example <c>SANDBOX_NOT_READY</c>.</param>
/// <param name="Domain">The producing service, for example <c>openshell.nvidia.com</c>.</param>
/// <param name="Metadata">Extra key-value context for the reason.</param>
public sealed record ErrorInfo(string Reason, string Domain, IReadOnlyDictionary<string, string> Metadata);

/// <summary>The structured <c>google.rpc</c> details a gateway attached to a failed call.</summary>
internal sealed record ErrorDetails(ErrorInfo? Info, IReadOnlyList<FieldViolation> FieldViolations, TimeSpan? RetryDelay)
{
    // google.protobuf.Duration is limited to roughly 10,000 years; anything beyond is a malformed value.
    private const long MaxRetrySeconds = 315_576_000_000;

    public static ErrorDetails Empty { get; } = new(null, [], null);

    /// <summary>
    /// Decodes details from the <c>grpc-status-details-bin</c> trailer. Details are ignored unless the envelope
    /// agrees with the actual call status, so a mismatched or hostile trailer cannot rewrite the error.
    /// </summary>
    public static ErrorDetails From(RpcException exception)
    {
        GoogleRpc.Status? envelope;
        try
        {
            envelope = exception.GetRpcStatus();
        }
        catch (InvalidProtocolBufferException)
        {
            return Empty;
        }

        if (envelope is null
            || envelope.Code != (int)exception.StatusCode
            || !string.Equals(envelope.Message, exception.Status.Detail, StringComparison.Ordinal))
        {
            return Empty;
        }

        ErrorInfo? info = null;
        List<FieldViolation>? violations = null;
        TimeSpan? retry = null;
        foreach (var detail in envelope.Details)
        {
            try
            {
                if (detail.Is(GoogleRpc.BadRequest.Descriptor))
                {
                    violations ??= [];
                    violations.AddRange(detail.Unpack<GoogleRpc.BadRequest>().FieldViolations.Select(v => new FieldViolation(v.Field, v.Description)));
                }
                else if (info is null && detail.Is(GoogleRpc.ErrorInfo.Descriptor))
                {
                    var value = detail.Unpack<GoogleRpc.ErrorInfo>();
                    info = new ErrorInfo(value.Reason, value.Domain, new Dictionary<string, string>(value.Metadata));
                }
                else if (retry is null && detail.Is(GoogleRpc.RetryInfo.Descriptor))
                {
                    retry = ValidDelay(detail.Unpack<GoogleRpc.RetryInfo>().RetryDelay);
                }
            }
            catch (InvalidProtocolBufferException)
            {
                // A malformed detail is skipped; it never replaces the original error.
            }
        }

        return info is null && violations is null && retry is null ? Empty : new ErrorDetails(info, violations ?? [], retry);
    }

    private static TimeSpan? ValidDelay(Duration? delay) =>
        delay is { Seconds: >= 0 and <= MaxRetrySeconds, Nanos: >= 0 and < 1_000_000_000 }
            ? TimeSpan.FromTicks((delay.Seconds * TimeSpan.TicksPerSecond) + (delay.Nanos / 100))
            : null;
}
