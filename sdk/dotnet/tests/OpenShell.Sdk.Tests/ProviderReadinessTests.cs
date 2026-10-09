// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ProviderReadinessLogicTests
{
    private static ProviderMutationReceipt Receipt(Wire.ProviderMutationKind kind = Wire.ProviderMutationKind.Attach) =>
        ProviderFixtures.Domain(kind);

    [Test]
    [Arguments(0L, 1, true)]
    [Arguments(0L, 0, true)]
    [Arguments(-1L, 999_999_999, true)]
    [Arguments(1L, -1, false)]
    [Arguments(1L, 1_000_000_000, false)]
    [Arguments(253_402_300_800L, 0, false)]
    [Arguments(-62_135_596_801L, 0, false)]
    public async Task Receipt_requires_a_canonical_persistence_time(long seconds, int nanos, bool valid)
    {
        var receipt = Receipt() with { PersistedTime = new ExactTimestamp(seconds, nanos) };

        var ok = true;
        try
        {
            ProviderReadinessLogic.ValidateReceipt(receipt);
        }
        catch (OpenShellException)
        {
            ok = false;
        }

        await Assert.That(ok).IsEqualTo(valid);
    }

    [Test]
    public async Task Receipt_without_persistence_time_is_invalid()
    {
        var ex = await Assert.That(() => ProviderReadinessLogic.ValidateReceipt(Receipt() with { PersistedTime = null })).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Receipt_requires_every_identity_field()
    {
        var good = Receipt();
        var variants = new[]
        {
            good with { Desired = null },
            good with { ReceiptId = "" },
            good with { MutationId = "" },
            good with { Provider = "" },
            good with { Workspace = "" },
            good with { Kind = ProviderMutationKind.Unspecified },
            good with { Kind = (ProviderMutationKind)99 },
            good with { Desired = good.Desired! with { SandboxId = "" } },
            good with { Desired = good.Desired! with { Sandbox = "" } },
            // Attach needs a provider identity; detach must not have one.
            good with { Desired = good.Desired! with { ProviderId = "" } },
            Receipt(Wire.ProviderMutationKind.Detach) with { Desired = Receipt(Wire.ProviderMutationKind.Detach).Desired! with { ProviderId = "still-attached" } },
        };

        foreach (var receipt in variants)
        {
            await Assert.That(() => ProviderReadinessLogic.ValidateReceipt(receipt)).Throws<OpenShellException>();
        }
    }

    [Test]
    public async Task Completion_state_follows_kind_and_provider_identity()
    {
        var observeAttached = Receipt(Wire.ProviderMutationKind.Observe);
        var observeDetached = observeAttached with { Desired = observeAttached.Desired! with { ProviderId = "" } };

        await Assert.That(ProviderReadinessLogic.CompletionState(Receipt())).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(ProviderReadinessLogic.CompletionState(Receipt(Wire.ProviderMutationKind.Update))).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(ProviderReadinessLogic.CompletionState(Receipt(Wire.ProviderMutationKind.Detach))).IsEqualTo(ProviderReadinessState.Revoked);
        await Assert.That(ProviderReadinessLogic.CompletionState(observeAttached)).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(ProviderReadinessLogic.CompletionState(observeDetached)).IsEqualTo(ProviderReadinessState.Revoked);
        await Assert.That(() => ProviderReadinessLogic.CompletionState(Receipt() with { Desired = null })).Throws<OpenShellException>();
    }

    [Test]
    public async Task Status_requires_receipt_known_state_and_reason()
    {
        var receipt = Receipt();
        var good = ProviderReadinessLogic.PersistedStatus(receipt);
        ProviderReadinessLogic.ValidateStatus(good);

        var variants = new[]
        {
            good with { Receipt = null },
            good with { State = ProviderReadinessState.Unspecified },
            good with { State = (ProviderReadinessState)99 },
            good with { Reason = (ProviderReadinessReason)99 },
            good with { Receipt = receipt with { ReceiptId = "" } },
        };

        foreach (var status in variants)
        {
            var ex = await Assert.That(() => ProviderReadinessLogic.ValidateStatus(status)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Internal);
        }
    }

    [Test]
    public async Task Ready_label_without_installation_evidence_is_invalid()
    {
        var wire = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);
        ProviderReadinessLogic.ValidateStatus(ProviderReadinessLogic.ToDomain(wire));

        var mutations = new List<Action<Wire.ProviderReadinessStatus>>
        {
            s => s.Observed = null,
            s => s.Observed.LaunchEnvironmentInstalled = false,
            s => s.Observed.CredentialsInstalled = false,
            s => s.Observed.PolicyActive = false,
            s => s.Observed.SessionId = "",
            s => s.Observed.ProcessInstanceId = "",
            s => s.NetworkInstanceId = "",
            s => s.Receipt.Desired.PolicyHash = "",
            s => s.Observed.Reason = Wire.ProviderReadinessReason.WaitingForProcess,
            s => s.Reason = Wire.ProviderReadinessReason.WaitingForProcess,
            s => s.Observed.AttachmentEpoch = "other",
            s => s.Observed.ProviderEnvRevision = 1,
            s => s.Observed.ConfigRevision = 1,
            s => s.Observed.PolicyHash = "other",
        };
        foreach (var mutate in mutations)
        {
            var broken = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);
            mutate(broken);
            await Assert.That(() => ProviderReadinessLogic.ValidateStatus(ProviderReadinessLogic.ToDomain(broken))).Throws<OpenShellException>();
        }
    }

    [Test]
    public async Task Ready_or_revoked_must_match_the_receipt_completion_state()
    {
        var attach = ProviderFixtures.Receipt();
        var detach = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Detach);

        await Assert.That(() => ProviderReadinessLogic.ValidateStatus(
            ProviderFixtures.DomainStatus(attach, Wire.ProviderReadinessState.Revoked))).Throws<OpenShellException>();
        await Assert.That(() => ProviderReadinessLogic.ValidateStatus(
            ProviderFixtures.DomainStatus(detach, Wire.ProviderReadinessState.Ready))).Throws<OpenShellException>();
        ProviderReadinessLogic.ValidateStatus(ProviderFixtures.DomainStatus(detach, Wire.ProviderReadinessState.Revoked));
    }

    [Test]
    public async Task Observe_receipt_without_provider_accepts_revoked()
    {
        var observe = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Observe);
        observe.Desired.ProviderId = "";

        ProviderReadinessLogic.ValidateStatus(ProviderFixtures.DomainStatus(observe, Wire.ProviderReadinessState.Revoked));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Initial_empty_epoch_still_requires_a_policy_identity()
    {
        var observe = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Observe);
        observe.Desired.AttachmentEpoch = "";
        var status = ProviderFixtures.DomainStatus(observe, Wire.ProviderReadinessState.Ready);
        var receipt = status.Receipt!;
        await Assert.That(ProviderReadinessLogic.Disposition(receipt, status).Outcome).IsEqualTo(ProviderWaitOutcome.Complete);

        observe.Desired.PolicyHash = "";
        await Assert.That(() => ProviderReadinessLogic.ValidateStatus(ProviderFixtures.DomainStatus(observe, Wire.ProviderReadinessState.Ready)))
            .Throws<OpenShellException>();
    }

    [Test]
    public async Task Different_authority_is_superseded_even_with_a_smaller_fingerprint()
    {
        var original = ProviderFixtures.Domain(Wire.ProviderMutationKind.Update);
        var newer = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Update);
        newer.Desired.ProviderEnvRevision = 1;
        var status = ProviderFixtures.DomainStatus(newer, Wire.ProviderReadinessState.Ready);

        var (outcome, result) = ProviderReadinessLogic.Disposition(original, status);

        await Assert.That(outcome).IsEqualTo(ProviderWaitOutcome.Terminal);
        await Assert.That(result.State).IsEqualTo(ProviderReadinessState.Superseded);
        await Assert.That(result.Reason).IsEqualTo(ProviderReadinessReason.DesiredStateChanged);
        await Assert.That(result.Receipt).IsEqualTo(original);
    }

    [Test]
    public async Task Changed_persistence_nanoseconds_supersede_the_original_receipt()
    {
        var original = ProviderFixtures.Domain();
        var changed = ProviderFixtures.Receipt();
        changed.PersistedTime.Nanos += 1;
        var status = ProviderFixtures.DomainStatus(changed, Wire.ProviderReadinessState.Ready);

        var (outcome, result) = ProviderReadinessLogic.Disposition(original, status);

        await Assert.That(outcome).IsEqualTo(ProviderWaitOutcome.Terminal);
        await Assert.That(result.State).IsEqualTo(ProviderReadinessState.Superseded);
    }

    [Test]
    public async Task Other_receipt_identity_fields_also_supersede()
    {
        var original = ProviderFixtures.Domain();
        var edits = new List<Action<Wire.ProviderMutationReceipt>>
        {
            r => r.ReceiptId = "other",
            r => r.MutationId = "other",
            r => r.Provider = "other",
            r => r.Workspace = "other",
            r => r.Kind = Wire.ProviderMutationKind.Update,
        };
        foreach (var edit in edits)
        {
            var other = ProviderFixtures.Receipt();
            edit(other);
            var status = ProviderFixtures.DomainStatus(other, Wire.ProviderReadinessState.Pending);

            await Assert.That(ProviderReadinessLogic.Disposition(original, status).Outcome).IsEqualTo(ProviderWaitOutcome.Terminal);
        }
    }

    [Test]
    public async Task Observation_receipts_without_an_id_match_on_authority_only()
    {
        var observe = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Observe);
        observe.ReceiptId = "";
        var requested = ProviderReadinessLogic.ToDomain(observe)!;
        var answered = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Observe);
        answered.ReceiptId = "assigned";

        // A reconstructed status carries a freshly assigned receipt id; ValidateReceipt rejects an empty id,
        // so only the answered receipt is validated. The wait still binds to the requested authority.
        var status = ProviderFixtures.DomainStatus(answered, Wire.ProviderReadinessState.Ready);
        var (outcome, _) = ProviderReadinessLogic.Disposition(requested, status);

        await Assert.That(outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Detach_requires_revocation_and_installation_failure_is_terminal()
    {
        var detach = ProviderFixtures.Receipt(Wire.ProviderMutationKind.Detach);
        var receipt = ProviderReadinessLogic.ToDomain(detach)!;

        // A ready status cannot be valid for a detach receipt.
        await Assert.That(() => ProviderReadinessLogic.Disposition(
            receipt, ProviderFixtures.DomainStatus(detach, Wire.ProviderReadinessState.Ready))).Throws<OpenShellException>();
        await Assert.That(ProviderReadinessLogic.Disposition(
            receipt, ProviderFixtures.DomainStatus(detach, Wire.ProviderReadinessState.Revoked)).Outcome).IsEqualTo(ProviderWaitOutcome.Complete);

        foreach (var state in new[] { Wire.ProviderReadinessState.Failed, Wire.ProviderReadinessState.Withheld, Wire.ProviderReadinessState.Superseded })
        {
            var failed = ProviderFixtures.Pending(detach);
            failed.State = state;
            failed.Reason = Wire.ProviderReadinessReason.CredentialInstallFailed;
            await Assert.That(ProviderReadinessLogic.Disposition(receipt, ProviderReadinessLogic.ToDomain(failed)).Outcome)
                .IsEqualTo(ProviderWaitOutcome.Terminal);
        }
    }

    [Test]
    public async Task Persisted_and_pending_states_keep_waiting()
    {
        var receipt = ProviderFixtures.Domain();

        await Assert.That(ProviderReadinessLogic.Disposition(receipt, ProviderReadinessLogic.PersistedStatus(receipt)).Outcome).IsNull();
        await Assert.That(ProviderReadinessLogic.Disposition(receipt, ProviderReadinessLogic.ToDomain(ProviderFixtures.Pending(ProviderFixtures.Receipt()))).Outcome).IsNull();
    }

    [Test]
    public async Task Revoked_status_cannot_complete_an_attach_wait()
    {
        // A revoked label on an attach receipt is a protocol violation, caught during status validation.
        var attach = ProviderFixtures.Receipt();
        var status = ProviderFixtures.DomainStatus(attach, Wire.ProviderReadinessState.Revoked);

        await Assert.That(() => ProviderReadinessLogic.Disposition(ProviderFixtures.Domain(), status)).Throws<OpenShellException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    [Arguments(3601)]
    public async Task Timeout_must_be_positive_and_at_most_one_hour(int seconds)
    {
        var ex = await Assert.That(() => ProviderReadinessLogic.ValidateTimeout(TimeSpan.FromSeconds(seconds))).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Timeout_boundary_of_one_hour_is_accepted()
    {
        ProviderReadinessLogic.ValidateTimeout(TimeSpan.FromHours(1));
        ProviderReadinessLogic.ValidateTimeout(TimeSpan.FromTicks(1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Persisted_status_means_saved_intent_waiting_for_supervisor()
    {
        var status = ProviderReadinessLogic.PersistedStatus(ProviderFixtures.Domain());

        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Persisted);
        await Assert.That(status.Reason).IsEqualTo(ProviderReadinessReason.WaitingForSupervisor);
    }

    [Test]
    public async Task Status_selectors_must_bind_the_response()
    {
        var wire = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);
        var status = ProviderReadinessLogic.ToDomain(wire);

        ProviderReadinessLogic.ValidateStatusForRequest(status, "default", "sandbox", "", "receipt");
        ProviderReadinessLogic.ValidateStatusForRequest(status, "default", "sandbox", "provider", "receipt");
        ProviderReadinessLogic.ValidateStatusForRequest(status, "default", "sandbox", "provider", "");

        var invalid = new[]
        {
            ("default", "sandbox", "", ""),
            ("default", "sandbox", "other-provider", "receipt"),
            ("default", "sandbox", "provider", "other-receipt"),
            ("default", "other-sandbox", "provider", "receipt"),
            ("other-workspace", "sandbox", "provider", "receipt"),
        };
        foreach (var (workspace, sandbox, provider, receiptId) in invalid)
        {
            await Assert.That(() => ProviderReadinessLogic.ValidateStatusForRequest(status, workspace, sandbox, provider, receiptId))
                .Throws<OpenShellException>();
        }
    }

    [Test]
    public async Task Exact_timestamp_converts_to_datetimeoffset()
    {
        var t = new ExactTimestamp(86_400, 500_000_000);

        await Assert.That(t.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1).AddMilliseconds(500));
    }

    [Test]
    public async Task Wire_mapping_covers_status_operation_and_times()
    {
        var wire = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);
        wire.ObservedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMinutes(1));
        wire.EvaluatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMinutes(2));
        wire.Observed.Sequence = 7;
        wire.Operation = new Wire.ConfigUpdateOperation
        {
            OperationId = "op",
            State = Wire.ConfigUpdateOperationState.Applied,
            Outcome = Wire.ConfigApplyOutcome.Applied,
            SanitizedError = "none",
        };

        var status = ProviderReadinessLogic.ToDomain(wire);

        await Assert.That(status.ObservedTime).IsEqualTo(DateTimeOffset.UnixEpoch.AddMinutes(1));
        await Assert.That(status.EvaluatedTime).IsEqualTo(DateTimeOffset.UnixEpoch.AddMinutes(2));
        await Assert.That(status.Observed!.Sequence).IsEqualTo(7ul);
        await Assert.That(status.Operation).IsEqualTo(new ProviderOperation("op", ConfigUpdateOperationState.Applied, ConfigApplyOutcome.Applied, "none"));
        await Assert.That(status.Receipt!.PersistedTime).IsEqualTo(new ExactTimestamp(1, 0));
        await Assert.That(ProviderReadinessLogic.ToDomain((Wire.ProviderMutationReceipt?)null)).IsNull();
        await Assert.That(ProviderReadinessLogic.ToDomain(new Wire.ProviderMutationReceipt())!.PersistedTime).IsNull();
    }

    [Test]
    public async Task Malformed_observation_time_is_an_invalid_status()
    {
        var wire = ProviderFixtures.Status(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready);
        wire.EvaluatedTime = new Timestamp { Seconds = 1, Nanos = -1 };

        await Assert.That(() => ProviderReadinessLogic.ToDomain(wire)).Throws<OpenShellException>();
    }

    // ---- wait loop ----------------------------------------------------------------------------------------

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private static Deadline NewDeadline(FakeTimeProvider time, TimeSpan timeout, CancellationToken ct = default) => new(time, timeout, ct);

    private static async Task Pump(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }
    }

    [Test]
    public async Task Wait_polls_until_the_exact_receipt_is_ready()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        var calls = 0;
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(30));

        var task = ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ =>
            {
                calls++;
                return ValueTask.FromResult(calls == 1
                    ? ProviderReadinessLogic.ToDomain(ProviderFixtures.Pending(ProviderFixtures.Receipt()))
                    : ProviderFixtures.DomainStatus(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready));
            },
            CancellationToken.None).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Completed_initial_status_returns_without_another_rpc()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(1));

        var result = await ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderFixtures.DomainStatus(ProviderFixtures.Receipt(), Wire.ProviderReadinessState.Ready),
            deadline,
            Interval,
            time,
            _ => throw new InvalidOperationException("must not fetch"),
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Deadline_bounds_a_hung_status_rpc_and_keeps_the_last_state()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain(Wire.ProviderMutationKind.Update);
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(5));

        var task = ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            },
            CancellationToken.None).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
        await Assert.That(result.Status.State).IsEqualTo(ProviderReadinessState.Persisted);
    }

    [Test]
    public async Task Initial_pending_status_survives_a_hung_followup_rpc()
    {
        var time = new FakeTimeProvider();
        var wirePending = ProviderFixtures.Pending(ProviderFixtures.Receipt());
        var pending = ProviderReadinessLogic.ToDomain(wirePending);
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(1));

        var task = ProviderReadinessLogic.WaitAsync(
            pending.Receipt!,
            pending,
            deadline,
            Interval,
            time,
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            },
            CancellationToken.None).AsTask();
        await Pump(time, task);
        var result = await task;

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
        await Assert.That(result.Status).IsEqualTo(pending);
    }

    [Test]
    public async Task Deadline_that_elapses_during_the_sleep_reports_timed_out()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var deadline = NewDeadline(time, TimeSpan.FromMilliseconds(300));

        var task = ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            TimeSpan.FromSeconds(10),
            time,
            _ => ValueTask.FromResult(ProviderReadinessLogic.ToDomain(ProviderFixtures.Pending(ProviderFixtures.Receipt()))),
            CancellationToken.None).AsTask();
        await Pump(time, task);

        await Assert.That((await task).Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
    }

    [Test]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var cts = new CancellationTokenSource();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(30), cts.Token);

        var task = ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            },
            cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Cancellation_before_the_first_fetch_throws()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(30), cts.Token);

        await Assert.That(async () => await ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ => throw new InvalidOperationException("must not fetch"),
            cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Wait_rejects_an_invalid_receipt_and_unknown_state()
    {
        var time = new FakeTimeProvider();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(1));
        var receipt = ProviderFixtures.Domain();

        await Assert.That(async () => await ProviderReadinessLogic.WaitAsync(
            receipt with { ReceiptId = "" },
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ => throw new InvalidOperationException(),
            CancellationToken.None)).Throws<OpenShellException>();

        var unknown = ProviderReadinessLogic.PersistedStatus(receipt) with { State = (ProviderReadinessState)99 };
        await Assert.That(async () => await ProviderReadinessLogic.WaitAsync(
            receipt,
            unknown,
            deadline,
            Interval,
            time,
            _ => throw new InvalidOperationException(),
            CancellationToken.None)).Throws<OpenShellException>();
    }

    [Test]
    public async Task Fetch_failures_other_than_the_deadline_propagate()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(30));

        await Assert.That(async () => await ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ => throw new OpenShellException(OpenShellErrorKind.Unavailable, "down"),
            CancellationToken.None)).Throws<OpenShellException>();
    }

    [Test]
    public async Task Deadline_exceeded_error_after_the_deadline_is_a_timeout()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));

        // The deadline already elapsed, so the loop head reports the timeout without another fetch.
        var result = await ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ => throw new OpenShellException(OpenShellErrorKind.DeadlineExceeded, "late"),
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
    }

    [Test]
    public async Task Gateway_deadline_error_while_the_wait_deadline_fires_is_a_timeout()
    {
        var time = new FakeTimeProvider();
        var receipt = ProviderFixtures.Domain();
        using var deadline = NewDeadline(time, TimeSpan.FromSeconds(1));

        var result = await ProviderReadinessLogic.WaitAsync(
            receipt,
            ProviderReadinessLogic.PersistedStatus(receipt),
            deadline,
            Interval,
            time,
            _ =>
            {
                time.Advance(TimeSpan.FromSeconds(2));
                throw new OpenShellException(OpenShellErrorKind.DeadlineExceeded, "rpc deadline");
            },
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
    }
}
