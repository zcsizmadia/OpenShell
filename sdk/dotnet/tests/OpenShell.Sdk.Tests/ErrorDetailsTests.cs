// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;
using GoogleRpc = Google.Rpc;

namespace OpenShell.Tests;

public class ErrorDetailsTests
{
    private static RpcException WithDetails(StatusCode code, string message, params IMessage[] details)
    {
        var envelope = new GoogleRpc.Status { Code = (int)code, Message = message };
        foreach (var detail in details)
        {
            envelope.Details.Add(Any.Pack(detail));
        }

        return new RpcException(new Status(code, message), new Metadata { { "grpc-status-details-bin", envelope.ToByteArray() } });
    }

    private static GoogleRpc.ErrorInfo Info(string reason = "SANDBOX_NOT_READY") =>
        new() { Reason = reason, Domain = "openshell.nvidia.com", Metadata = { ["sandbox"] = "dev" } };

    [Test]
    public async Task Error_info_is_decoded_with_reason_domain_and_metadata()
    {
        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.FailedPrecondition, "not ready", Info()));

        await Assert.That(ex.ErrorInfo!.Reason).IsEqualTo("SANDBOX_NOT_READY");
        await Assert.That(ex.ErrorInfo.Domain).IsEqualTo("openshell.nvidia.com");
        await Assert.That(ex.ErrorInfo.Metadata["sandbox"]).IsEqualTo("dev");
        await Assert.That(ex.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(ex.Message).IsEqualTo("not ready");
    }

    [Test]
    public async Task Field_violations_from_several_bad_requests_are_collected()
    {
        var first = new GoogleRpc.BadRequest { FieldViolations = { new GoogleRpc.BadRequest.Types.FieldViolation { Field = "name", Description = "too long" } } };
        var second = new GoogleRpc.BadRequest { FieldViolations = { new GoogleRpc.BadRequest.Types.FieldViolation { Field = "spec.image", Description = "required" } } };

        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.InvalidArgument, "bad", first, second));

        await Assert.That(ex.FieldViolations).IsEquivalentTo(new[] { new FieldViolation("name", "too long"), new FieldViolation("spec.image", "required") });
        await Assert.That(ex.ErrorInfo).IsNull();
        await Assert.That(ex.RetryDelay).IsNull();
    }

    [Test]
    public async Task Retry_delay_is_converted_with_nanosecond_precision()
    {
        var retry = new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = 2, Nanos = 500_000_000 } };

        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.Unavailable, "busy", retry));

        await Assert.That(ex.RetryDelay).IsEqualTo(TimeSpan.FromMilliseconds(2500));
    }

    [Test]
    [Arguments(-1L, 0)]
    [Arguments(315_576_000_001L, 0)]
    [Arguments(1L, -5)]
    [Arguments(1L, 1_000_000_000)]
    public async Task Out_of_range_retry_delays_are_ignored(long seconds, int nanos)
    {
        var retry = new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = seconds, Nanos = nanos } };

        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.Unavailable, "busy", retry));

        await Assert.That(ex.RetryDelay).IsNull();
    }

    [Test]
    public async Task Retry_info_without_a_delay_is_ignored()
    {
        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.Unavailable, "busy", new GoogleRpc.RetryInfo()));

        await Assert.That(ex.RetryDelay).IsNull();
    }

    [Test]
    public async Task First_error_info_and_first_retry_info_win()
    {
        var one = new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = 1 } };
        var two = new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = 9 } };

        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.Unavailable, "busy", Info("FIRST"), Info("SECOND"), one, two));

        await Assert.That(ex.ErrorInfo!.Reason).IsEqualTo("FIRST");
        await Assert.That(ex.RetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Unknown_detail_types_are_ignored()
    {
        var ex = OpenShellException.FromRpc(WithDetails(StatusCode.Internal, "oops", new Google.Protobuf.WellKnownTypes.StringValue { Value = "x" }));

        await Assert.That(ex.ErrorInfo).IsNull();
        await Assert.That(ex.FieldViolations.Count).IsEqualTo(0);
        await Assert.That(ex.RetryDelay).IsNull();
    }

    [Test]
    public async Task Envelope_with_a_different_code_or_message_is_ignored()
    {
        var envelope = new GoogleRpc.Status { Code = (int)StatusCode.NotFound, Message = "other", Details = { Any.Pack(Info()) } };
        var wrongCode = new RpcException(new Status(StatusCode.Internal, "other"), new Metadata { { "grpc-status-details-bin", envelope.ToByteArray() } });
        var wrongMessage = new RpcException(new Status(StatusCode.NotFound, "different"), new Metadata { { "grpc-status-details-bin", envelope.ToByteArray() } });

        await Assert.That(OpenShellException.FromRpc(wrongCode).ErrorInfo).IsNull();
        await Assert.That(OpenShellException.FromRpc(wrongMessage).ErrorInfo).IsNull();
    }

    [Test]
    public async Task Malformed_trailer_keeps_the_original_error_intact()
    {
        var garbage = new RpcException(new Status(StatusCode.NotFound, "gone"), new Metadata { { "grpc-status-details-bin", new byte[] { 0xFF, 0xFF, 0xFF, 0x01 } } });

        var ex = OpenShellException.FromRpc(garbage);

        await Assert.That(ex.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await Assert.That(ex.Message).IsEqualTo("gone");
        await Assert.That(ex.ErrorInfo).IsNull();
        await Assert.That(ex.FieldViolations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_detail_payload_is_skipped_but_valid_ones_survive()
    {
        var envelope = new GoogleRpc.Status
        {
            Code = (int)StatusCode.NotFound,
            Message = "gone",
            Details =
            {
                new Any { TypeUrl = "type.googleapis.com/google.rpc.ErrorInfo", Value = ByteString.CopyFrom(0xFF, 0xFF, 0xFF, 0x01) },
                Any.Pack(new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = 3 } }),
            },
        };
        var rpc = new RpcException(new Status(StatusCode.NotFound, "gone"), new Metadata { { "grpc-status-details-bin", envelope.ToByteArray() } });

        var ex = OpenShellException.FromRpc(rpc);

        await Assert.That(ex.ErrorInfo).IsNull();
        await Assert.That(ex.RetryDelay).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Plain_errors_and_sdk_created_errors_have_empty_details()
    {
        var fromRpc = OpenShellException.FromRpc(new RpcException(new Status(StatusCode.NotFound, "gone")));
        var local = new OpenShellException(OpenShellErrorKind.InvalidConfig, "bad");

        await Assert.That(fromRpc.ErrorInfo).IsNull();
        await Assert.That(fromRpc.FieldViolations.Count).IsEqualTo(0);
        await Assert.That(fromRpc.RetryDelay).IsNull();
        await Assert.That(local.ErrorInfo).IsNull();
        await Assert.That(local.RetryDelay).IsNull();
    }

    [Test]
    public async Task Details_flow_through_a_failed_client_call()
    {
        var failure = WithDetails(StatusCode.InvalidArgument, "invalid name", new GoogleRpc.BadRequest
        {
            FieldViolations = { new GoogleRpc.BadRequest.Types.FieldViolation { Field = "name", Description = "invalid name" } },
        });
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => throw failure);
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.GetAsync("BAD NAME")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(ex.FieldViolations.Single().Field).IsEqualTo("name");
    }
}
