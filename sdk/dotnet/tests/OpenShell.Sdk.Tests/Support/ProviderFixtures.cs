// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell.Tests.Support;

internal static class ProviderFixtures
{
    public static Wire.ProviderMutationReceipt Receipt(Wire.ProviderMutationKind kind = Wire.ProviderMutationKind.Attach)
    {
        var receipt = new Wire.ProviderMutationReceipt
        {
            ReceiptId = "receipt",
            MutationId = "mutation",
            Provider = "provider",
            Workspace = "default",
            Kind = kind,
            PersistedTime = new Timestamp { Seconds = 1 },
            Desired = new Wire.ProviderDesiredIdentity
            {
                SandboxId = "sandbox-id",
                Sandbox = "sandbox",
                ProviderId = kind == Wire.ProviderMutationKind.Detach ? "" : "provider-id",
                AttachmentEpoch = "epoch",
                PolicyHash = "hash",
                ProviderEnvRevision = ulong.MaxValue,
            },
        };
        return receipt;
    }

    public static Wire.ProviderReadinessStatus Status(Wire.ProviderMutationReceipt receipt, Wire.ProviderReadinessState state)
    {
        var desired = receipt.Desired;
        return new Wire.ProviderReadinessStatus
        {
            Receipt = receipt,
            State = state,
            NetworkInstanceId = "network",
            Observed = new Wire.ProviderReadinessObservation
            {
                SessionId = "session",
                ProcessInstanceId = "process",
                AttachmentEpoch = desired.AttachmentEpoch,
                ProviderEnvRevision = desired.ProviderEnvRevision,
                ConfigRevision = desired.ConfigRevision,
                PolicyHash = desired.PolicyHash,
                CredentialsInstalled = true,
                PolicyActive = true,
                LaunchEnvironmentInstalled = true,
            },
        };
    }

    public static Wire.ProviderReadinessStatus Pending(Wire.ProviderMutationReceipt receipt) =>
        new()
        {
            Receipt = receipt,
            State = Wire.ProviderReadinessState.Pending,
            Reason = Wire.ProviderReadinessReason.WaitingForProcess,
        };

    public static ProviderMutationReceipt Domain(Wire.ProviderMutationKind kind = Wire.ProviderMutationKind.Attach) =>
        ProviderReadinessLogic.ToDomain(Receipt(kind))!;

    public static ProviderReadinessStatus DomainStatus(Wire.ProviderMutationReceipt receipt, Wire.ProviderReadinessState state) =>
        ProviderReadinessLogic.ToDomain(Status(receipt, state));

    public static Openshell.Datamodel.V1.Provider WireProvider(string name = "github-prod", string id = "pid", ulong version = 3)
    {
        var provider = new Openshell.Datamodel.V1.Provider
        {
            Type = "github",
            Metadata = new ObjectMeta
            {
                Id = id,
                Name = name,
                Workspace = "default",
                ResourceVersion = version,
                CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1)),
            },
            ProfileWorkspace = "pw",
        };
        provider.Metadata.Labels["l"] = "1";
        provider.Metadata.Annotations["a"] = "2";
        provider.Credentials["TOKEN"] = "***";
        provider.Config["org"] = "acme";
        provider.CredentialExpirationTimes["TOKEN"] = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(2));
        provider.CredentialHandles["TOKEN"] = new Openshell.Datamodel.V1.CredentialHandle
        {
            Driver = "vault",
            Handle = "h",
            Metadata = { ["k"] = "v" },
        };
        return provider;
    }
}
