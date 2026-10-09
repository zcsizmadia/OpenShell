// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Provider attachment and readiness for sandboxes, scoped to one workspace.</summary>
public interface ISandboxProviderClient
{
    /// <summary>Attaches a provider to a sandbox. Pass the receipt to <see cref="WaitAsync(ProviderMutationReceipt, TimeSpan, CancellationToken)"/> to await installation.</summary>
    ValueTask<AttachProviderResult> AttachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Detaches a provider from a sandbox. Revocation is complete only when the receipt reports <see cref="ProviderReadinessState.Revoked"/>.</summary>
    ValueTask<DetachProviderResult> DetachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the providers attached to a sandbox lazily.</summary>
    Pager<Provider> List(string sandbox, ListOptions? options = null);

    /// <summary>
    /// Gets validated readiness for a provider on a sandbox. With <paramref name="receiptId"/> it queries that immutable receipt;
    /// otherwise it reconstructs the current desired state. At least one of provider or receipt id is required.
    /// </summary>
    ValueTask<ProviderReadinessStatus> GetStatusAsync(string sandbox, string? provider = null, string? receiptId = null, CancellationToken cancellationToken = default);

    /// <summary>Gets readiness for the exact authority a receipt names.</summary>
    ValueTask<ProviderReadinessStatus> GetStatusAsync(ProviderMutationReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits, bounded by <paramref name="timeout"/> (greater than zero, at most one hour), for the exact authority a receipt names.
    /// Detach requires <see cref="ProviderReadinessState.Revoked"/>; attach and update require <see cref="ProviderReadinessState.Ready"/>.
    /// A later mutation yields <see cref="ProviderWaitOutcome.Terminal"/>, never success.
    /// </summary>
    ValueTask<ProviderWaitResult> WaitAsync(ProviderMutationReceipt receipt, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Continues a bounded wait from a status that was already observed; a later RPC failure cannot discard it.</summary>
    ValueTask<ProviderWaitResult> WaitAsync(ProviderReadinessStatus status, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Waits for several receipts, for example <see cref="ProviderUpdateResult.TargetReceipts"/>, under one total deadline.</summary>
    ValueTask<IReadOnlyList<ProviderWaitResult>> WaitAllAsync(IReadOnlyCollection<ProviderMutationReceipt> receipts, TimeSpan timeout, CancellationToken cancellationToken = default);
}

internal sealed class SandboxProviderClient(ClientCore core, string workspace) : ISandboxProviderClient
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async ValueTask<AttachProviderResult> AttachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var request = new Wire.AttachSandboxProviderRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Provider = provider,
            ExpectedResourceVersion = options?.ExpectedResourceVersion ?? 0,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.AttachSandboxProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new AttachProviderResult(
            SandboxMapping.ToDomain(response.Sandbox), response.Attached, ProviderReadinessLogic.ToDomain(response.Receipt));
    }

    public async ValueTask<DetachProviderResult> DetachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var request = new Wire.DetachSandboxProviderRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Sandbox = sandbox,
            Provider = provider,
            ExpectedResourceVersion = options?.ExpectedResourceVersion ?? 0,
            RequestId = options?.RequestId ?? "",
        };
        var response = await core.UnaryAsync(o => core.Grpc.DetachSandboxProviderAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new DetachProviderResult(
            SandboxMapping.ToDomain(response.Sandbox), response.Detached, ProviderReadinessLogic.ToDomain(response.Receipt));
    }

    public Pager<Provider> List(string sandbox, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        options ??= new ListOptions();
        return new Pager<Provider>(
            async (token, ct) =>
            {
                var request = new Wire.ListSandboxProvidersRequest
                {
                    WorkspaceScope = ClientCore.Scope(workspace),
                    Sandbox = sandbox,
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListSandboxProvidersAsync(request, o), ct).ConfigureAwait(false);
                return new Page<Provider>(
                    response.Providers.Select(ProviderMapping.ToDomain).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public ValueTask<ProviderReadinessStatus> GetStatusAsync(string sandbox, string? provider = null, string? receiptId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return string.IsNullOrEmpty(provider) && string.IsNullOrEmpty(receiptId)
            ? throw new ArgumentException("a provider name or receipt id is required", nameof(provider))
            : FetchAsync(workspace, sandbox, provider ?? "", receiptId ?? "", cancellationToken);
    }

    public ValueTask<ProviderReadinessStatus> GetStatusAsync(ProviderMutationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ProviderReadinessLogic.ValidateReceipt(receipt);
        return FetchAsync(receipt.Workspace, receipt.Desired!.Sandbox, receipt.Provider, receipt.ReceiptId, cancellationToken);
    }

    public async ValueTask<ProviderWaitResult> WaitAsync(ProviderMutationReceipt receipt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        using var deadline = new Deadline(core.Time, timeout, cancellationToken);
        return await WaitCoreAsync(receipt, ProviderReadinessLogic.PersistedStatus(receipt), deadline, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProviderWaitResult> WaitAsync(ProviderReadinessStatus status, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        var receipt = status.Receipt ?? throw ProviderReadinessLogic.InvalidStatus();
        using var deadline = new Deadline(core.Time, timeout, cancellationToken);
        return await WaitCoreAsync(receipt, status, deadline, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ProviderWaitResult>> WaitAllAsync(IReadOnlyCollection<ProviderMutationReceipt> receipts, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        using var deadline = new Deadline(core.Time, timeout, cancellationToken);
        var tasks = receipts.Select(r => WaitCoreAsync(r, ProviderReadinessLogic.PersistedStatus(r), deadline, cancellationToken).AsTask()).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private ValueTask<ProviderWaitResult> WaitCoreAsync(ProviderMutationReceipt receipt, ProviderReadinessStatus initial, Deadline deadline, CancellationToken cancellationToken) =>
        ProviderReadinessLogic.WaitAsync(
            receipt,
            initial,
            deadline,
            PollInterval,
            core.Time,
            ct => FetchAsync(receipt.Workspace, receipt.Desired!.Sandbox, receipt.Provider, receipt.ReceiptId, ct),
            cancellationToken);

    private async ValueTask<ProviderReadinessStatus> FetchAsync(string statusWorkspace, string sandbox, string provider, string receiptId, CancellationToken cancellationToken)
    {
        var request = new Wire.GetSandboxProviderStatusRequest
        {
            WorkspaceScope = ClientCore.Scope(statusWorkspace),
            Sandbox = sandbox,
            Provider = provider,
            ReceiptId = receiptId,
        };
        Wire.GetSandboxProviderStatusResponse response;
        try
        {
            response = await core.UnaryAsync(o => core.Grpc.GetSandboxProviderStatusAsync(request, o), cancellationToken).ConfigureAwait(false);
        }
        catch (OpenShellException e) when (e.GrpcStatusCode is { } code)
        {
            // Only the protocol code is exposed, never the server's diagnostic message.
            throw new OpenShellException(e.Kind, $"provider readiness request failed ({code})");
        }

        if (response.Status is not { } wire)
        {
            throw ProviderReadinessLogic.InvalidStatus();
        }

        var status = ProviderReadinessLogic.ToDomain(wire);
        ProviderReadinessLogic.ValidateStatusForRequest(status, statusWorkspace, sandbox, provider, receiptId);
        return status;
    }
}
