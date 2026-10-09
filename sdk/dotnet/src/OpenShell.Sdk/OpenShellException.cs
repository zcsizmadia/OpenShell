// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;

namespace OpenShell;

/// <summary>Classifies an <see cref="OpenShellException"/>.</summary>
public enum OpenShellErrorKind
{
    /// <summary>The client was configured inconsistently or unsafely.</summary>
    InvalidConfig,
    /// <summary>The TLS handshake or certificate validation failed.</summary>
    Tls,
    /// <summary>The gateway could not be reached.</summary>
    Connect,
    /// <summary>Credentials could not be obtained or refreshed.</summary>
    Auth,
    /// <summary>The resource does not exist.</summary>
    NotFound,
    /// <summary>The resource already exists.</summary>
    AlreadyExists,
    /// <summary>The caller is authenticated but not allowed.</summary>
    PermissionDenied,
    /// <summary>Credentials are missing or invalid.</summary>
    Unauthenticated,
    /// <summary>A request argument was rejected.</summary>
    InvalidArgument,
    /// <summary>The gateway is temporarily unavailable.</summary>
    Unavailable,
    /// <summary>The call or a wait exceeded its deadline.</summary>
    DeadlineExceeded,
    /// <summary>The call was cancelled.</summary>
    Cancelled,
    /// <summary>The gateway does not implement the operation.</summary>
    Unimplemented,
    /// <summary>Optimistic concurrency or an invalid state transition.</summary>
    Conflict,
    /// <summary>The gateway reported an internal error.</summary>
    Internal,
    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>The error type thrown by every SDK operation.</summary>
public class OpenShellException : Exception
{
    /// <summary>Creates an exception of the given kind.</summary>
    public OpenShellException(OpenShellErrorKind kind, string message, Exception? innerException = null)
        : this(kind, message, innerException, ErrorDetails.Empty)
    {
    }

    private OpenShellException(OpenShellErrorKind kind, string message, Exception? innerException, ErrorDetails details)
        : base(message, innerException)
    {
        Kind = kind;
        ErrorInfo = details.Info;
        FieldViolations = details.FieldViolations;
        RetryDelay = details.RetryDelay;
    }

    /// <summary>The error classification.</summary>
    public OpenShellErrorKind Kind { get; }

    /// <summary>The server-provided reason, domain, and metadata, when the gateway sent them.</summary>
    public ErrorInfo? ErrorInfo { get; }

    /// <summary>The request fields the gateway rejected; empty when it sent none.</summary>
    public IReadOnlyList<FieldViolation> FieldViolations { get; }

    /// <summary>
    /// The delay the gateway suggested before trying again. It says nothing about whether repeating a
    /// mutation is safe; use a request id for that.
    /// </summary>
    public TimeSpan? RetryDelay { get; }

    /// <summary>The underlying gRPC status, when the error came from the gateway.</summary>
    public StatusCode? GrpcStatusCode => (InnerException as RpcException)?.StatusCode;

    /// <summary>True for <see cref="OpenShellErrorKind.NotFound"/>.</summary>
    public bool IsNotFound => Kind is OpenShellErrorKind.NotFound;

    /// <summary>True for <see cref="OpenShellErrorKind.AlreadyExists"/>.</summary>
    public bool IsAlreadyExists => Kind is OpenShellErrorKind.AlreadyExists;

    /// <summary>True for <see cref="OpenShellErrorKind.Conflict"/>.</summary>
    public bool IsConflict => Kind is OpenShellErrorKind.Conflict;

    /// <summary>True for errors that may succeed on retry.</summary>
    public bool IsTransient => Kind is OpenShellErrorKind.Unavailable or OpenShellErrorKind.DeadlineExceeded;

    internal static OpenShellException FromRpc(RpcException e)
    {
        var kind = e.StatusCode switch
        {
            StatusCode.NotFound => OpenShellErrorKind.NotFound,
            StatusCode.AlreadyExists => OpenShellErrorKind.AlreadyExists,
            StatusCode.PermissionDenied => OpenShellErrorKind.PermissionDenied,
            StatusCode.Unauthenticated => OpenShellErrorKind.Unauthenticated,
            StatusCode.InvalidArgument or StatusCode.OutOfRange => OpenShellErrorKind.InvalidArgument,
            StatusCode.Unavailable => OpenShellErrorKind.Unavailable,
            StatusCode.DeadlineExceeded => OpenShellErrorKind.DeadlineExceeded,
            StatusCode.Cancelled => OpenShellErrorKind.Cancelled,
            StatusCode.Unimplemented => OpenShellErrorKind.Unimplemented,
            StatusCode.Aborted or StatusCode.FailedPrecondition => OpenShellErrorKind.Conflict,
            StatusCode.Internal or StatusCode.DataLoss => OpenShellErrorKind.Internal,
            _ => OpenShellErrorKind.Unknown,
        };
        return new OpenShellException(kind, e.Status.Detail is { Length: > 0 } d ? d : e.Message, e, ErrorDetails.From(e));
    }
}
