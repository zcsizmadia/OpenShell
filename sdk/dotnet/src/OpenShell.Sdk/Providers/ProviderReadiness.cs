// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>
/// Receipt-bound provider status validation and bounded waits. A wait pins the original desired
/// identity: it cannot succeed by following a later mutation, and its deadline covers RPC execution
/// as well as polling.
/// </summary>
internal static class ProviderReadinessLogic
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    public static OpenShellException InvalidStatus() =>
        new(OpenShellErrorKind.Internal, "gateway returned an invalid provider readiness status");

    public static OpenShellException InvalidReceipt() =>
        new(OpenShellErrorKind.InvalidArgument, "provider readiness receipt is incomplete");

    public static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaxWait)
        {
            throw new OpenShellException(
                OpenShellErrorKind.InvalidConfig, "provider wait timeout must be greater than zero and at most 3600 seconds");
        }
    }

    /// <summary>Represents saved intent before any runtime observation has been fetched.</summary>
    public static ProviderReadinessStatus PersistedStatus(ProviderMutationReceipt receipt) => new()
    {
        Receipt = receipt,
        State = ProviderReadinessState.Persisted,
        Reason = ProviderReadinessReason.WaitingForSupervisor,
    };

    /// <summary>
    /// Validates saved receipt identity without fetching a runtime observation. The mutation kind must agree
    /// with the presence of a provider identity.
    /// </summary>
    public static void ValidateReceipt(ProviderMutationReceipt receipt)
    {
        if (receipt.Desired is not { } desired || receipt.PersistedTime is not { } persisted
            || !IsCanonical(persisted)
            || receipt.ReceiptId.Length == 0
            || receipt.MutationId.Length == 0
            || desired.SandboxId.Length == 0
            || desired.Sandbox.Length == 0
            || receipt.Provider.Length == 0
            || receipt.Workspace.Length == 0
            || !Enum.IsDefined(receipt.Kind)
            || receipt.Kind == ProviderMutationKind.Unspecified)
        {
            throw InvalidReceipt();
        }

        _ = CompletionState(receipt);
    }

    private static bool IsCanonical(ExactTimestamp t) =>
        t.Seconds is >= -62_135_596_800 and <= 253_402_300_799 && t.Nanos is >= 0 and <= 999_999_999;

    public static ProviderReadinessState CompletionState(ProviderMutationReceipt receipt)
    {
        var attached = (receipt.Desired ?? throw InvalidReceipt()).ProviderId.Length != 0;
        return receipt.Kind switch
        {
            ProviderMutationKind.Attach or ProviderMutationKind.Update when attached => ProviderReadinessState.Ready,
            ProviderMutationKind.Detach when !attached => ProviderReadinessState.Revoked,
            ProviderMutationKind.Observe => attached ? ProviderReadinessState.Ready : ProviderReadinessState.Revoked,
            _ => throw InvalidReceipt(),
        };
    }

    public static void ValidateStatus(ProviderReadinessStatus status)
    {
        if (status.Receipt is not { } receipt
            || !Enum.IsDefined(status.State)
            || status.State == ProviderReadinessState.Unspecified
            || !Enum.IsDefined(status.Reason))
        {
            throw InvalidStatus();
        }

        try
        {
            ValidateReceipt(receipt);
        }
        catch (OpenShellException)
        {
            throw InvalidStatus();
        }

        if (status.State is ProviderReadinessState.Ready or ProviderReadinessState.Revoked)
        {
            if (status.State != CompletionState(receipt))
            {
                throw InvalidStatus();
            }

            // A status read has the same installation contract as a wait; labels alone never report completion.
            ValidateCompletion(status);
        }
    }

    public static void ValidateCompletion(ProviderReadinessStatus status)
    {
        var desired = status.Receipt?.Desired ?? throw InvalidStatus();
        var observed = status.Observed ?? throw InvalidStatus();
        if (desired.PolicyHash.Length == 0
            || status.NetworkInstanceId.Length == 0
            || observed.SessionId.Length == 0
            || observed.ProcessInstanceId.Length == 0
            || !observed.CredentialsInstalled
            || !observed.PolicyActive
            || !observed.LaunchEnvironmentInstalled
            || observed.Reason != ProviderReadinessReason.Unspecified
            || status.Reason != ProviderReadinessReason.Unspecified
            || observed.AttachmentEpoch != desired.AttachmentEpoch
            || observed.ProviderEnvRevision != desired.ProviderEnvRevision
            || observed.ConfigRevision != desired.ConfigRevision
            || observed.PolicyHash != desired.PolicyHash)
        {
            throw InvalidStatus();
        }
    }

    /// <summary>Checks that a response is bound to the request's selectors.</summary>
    public static void ValidateStatusForRequest(ProviderReadinessStatus status, string workspace, string sandbox, string provider, string receiptId)
    {
        ValidateStatus(status);
        var receipt = status.Receipt!;
        // A receipt id or a provider name must select the authority; every supplied selector must bind the response to it.
        if ((receiptId.Length == 0 && provider.Length == 0)
            || (receiptId.Length != 0 && receipt.ReceiptId != receiptId)
            || (provider.Length != 0 && receipt.Provider != provider)
            || receipt.Workspace != workspace
            || receipt.Desired!.Sandbox != sandbox)
        {
            throw InvalidStatus();
        }
    }

    /// <summary>
    /// Decides whether a status ends the wait. A status for a different authority is reported as
    /// <see cref="ProviderReadinessState.Superseded"/> against the original receipt.
    /// </summary>
    public static (ProviderWaitOutcome? Outcome, ProviderReadinessStatus Status) Disposition(
        ProviderMutationReceipt receipt, ProviderReadinessStatus status)
    {
        ValidateStatus(status);
        var observed = status.Receipt!;
        // The full receipt must remain fixed; a later snapshot cannot move this target.
        if (observed.Desired != receipt.Desired
            || observed.Provider != receipt.Provider
            || observed.Workspace != receipt.Workspace
            || (receipt.ReceiptId.Length != 0
                && (observed.ReceiptId != receipt.ReceiptId
                    || observed.MutationId != receipt.MutationId
                    || observed.Kind != receipt.Kind
                    || observed.PersistedTime != receipt.PersistedTime)))
        {
            return (ProviderWaitOutcome.Terminal, status with
            {
                Receipt = receipt,
                State = ProviderReadinessState.Superseded,
                Reason = ProviderReadinessReason.DesiredStateChanged,
            });
        }

        var revoked = CompletionState(receipt) == ProviderReadinessState.Revoked;
        switch (status.State)
        {
            case ProviderReadinessState.Ready when !revoked:
            case ProviderReadinessState.Revoked when revoked:
                ValidateCompletion(status);
                return (ProviderWaitOutcome.Complete, status);
            case ProviderReadinessState.Withheld:
            case ProviderReadinessState.Failed:
            case ProviderReadinessState.Superseded:
                return (ProviderWaitOutcome.Terminal, status);
            case ProviderReadinessState.Ready:
            case ProviderReadinessState.Revoked:
                throw InvalidStatus();
            default:
                return (null, status);
        }
    }

    public static async ValueTask<ProviderWaitResult> WaitAsync(
        ProviderMutationReceipt receipt,
        ProviderReadinessStatus initial,
        Deadline deadline,
        TimeSpan interval,
        TimeProvider time,
        Func<CancellationToken, ValueTask<ProviderReadinessStatus>> fetch,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(receipt);
        var (done, last) = Disposition(receipt, initial);
        if (done is { } immediate)
        {
            return new ProviderWaitResult(last, immediate);
        }

        while (true)
        {
            if (deadline.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ProviderWaitResult(last, ProviderWaitOutcome.TimedOut);
            }

            // The deadline also cancels an RPC that never responds; sleeping alone cannot bound a wait on an unavailable peer.
            ProviderReadinessStatus fetched;
            try
            {
                fetched = await fetch(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or OpenShellException { Kind: OpenShellErrorKind.DeadlineExceeded or OpenShellErrorKind.Cancelled }
                                      && deadline.TimedOut)
            {
                return new ProviderWaitResult(last, ProviderWaitOutcome.TimedOut);
            }

            (done, last) = Disposition(receipt, fetched);
            if (done is { } outcome)
            {
                return new ProviderWaitResult(last, outcome);
            }

            try
            {
                await Task.Delay(interval, time, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.TimedOut)
            {
                // The loop head reports the timeout with the last observed state.
            }
        }
    }

    // ---- wire mapping -------------------------------------------------------------------------------------

    public static ProviderMutationReceipt? ToDomain(Wire.ProviderMutationReceipt? r) =>
        r is null
            ? null
            : new ProviderMutationReceipt
            {
                ReceiptId = r.ReceiptId,
                MutationId = r.MutationId,
                Provider = r.Provider,
                Workspace = r.Workspace,
                Kind = (ProviderMutationKind)(int)r.Kind,
                Desired = r.Desired is null ? null : ToDomain(r.Desired),
                PersistedTime = r.PersistedTime is { } t ? new ExactTimestamp(t.Seconds, t.Nanos) : null,
            };

    private static ProviderDesiredIdentity ToDomain(Wire.ProviderDesiredIdentity d) => new()
    {
        SandboxId = d.SandboxId,
        Sandbox = d.Sandbox,
        AttachmentEpoch = d.AttachmentEpoch,
        ProviderId = d.ProviderId,
        ProviderResourceVersion = d.ProviderResourceVersion,
        ProviderEnvRevision = d.ProviderEnvRevision,
        ConfigRevision = d.ConfigRevision,
        PolicyHash = d.PolicyHash,
    };

    public static ProviderReadinessStatus ToDomain(Wire.ProviderReadinessStatus s)
    {
        try
        {
            return new ProviderReadinessStatus
            {
                Receipt = ToDomain(s.Receipt),
                State = (ProviderReadinessState)(int)s.State,
                Reason = (ProviderReadinessReason)(int)s.Reason,
                Observed = s.Observed is null ? null : ToDomain(s.Observed),
                NetworkInstanceId = s.NetworkInstanceId,
                ObservedTime = ProviderMapping.ToTime(s.ObservedTime),
                EvaluatedTime = ProviderMapping.ToTime(s.EvaluatedTime),
                Operation = s.Operation is null
                    ? null
                    : new ProviderOperation(
                        s.Operation.OperationId,
                        (ConfigUpdateOperationState)(int)s.Operation.State,
                        (ConfigApplyOutcome)(int)s.Operation.Outcome,
                        s.Operation.SanitizedError),
            };
        }
        catch (OpenShellException)
        {
            throw InvalidStatus();
        }
    }

    private static ProviderReadinessObservation ToDomain(Wire.ProviderReadinessObservation o) => new()
    {
        SessionId = o.SessionId,
        Sequence = o.Sequence,
        AttachmentEpoch = o.AttachmentEpoch,
        ProviderEnvRevision = o.ProviderEnvRevision,
        ConfigRevision = o.ConfigRevision,
        PolicyHash = o.PolicyHash,
        CredentialsInstalled = o.CredentialsInstalled,
        PolicyActive = o.PolicyActive,
        LaunchEnvironmentInstalled = o.LaunchEnvironmentInstalled,
        ProcessInstanceId = o.ProcessInstanceId,
        Reason = (ProviderReadinessReason)(int)o.Reason,
    };
}
