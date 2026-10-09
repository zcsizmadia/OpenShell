// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell;

internal static class ProviderMapping
{
    private const long MinSeconds = -62_135_596_800;
    private const long MaxSeconds = 253_402_300_799;

    public static bool IsCanonical(Timestamp t) =>
        t.Seconds is >= MinSeconds and <= MaxSeconds && t.Nanos is >= 0 and <= 999_999_999;

    public static DateTimeOffset? ToTime(Timestamp? t)
    {
        if (t is null)
        {
            return null;
        }

        if (!IsCanonical(t))
        {
            throw new OpenShellException(OpenShellErrorKind.Internal, "gateway returned a malformed timestamp");
        }

        return t.ToDateTimeOffset();
    }

    public static Timestamp ToWire(DateTimeOffset t) => Timestamp.FromDateTimeOffset(t);

    public static Provider ToDomain(Openshell.Datamodel.V1.Provider p)
    {
        var meta = p.Metadata;
        return new Provider
        {
            Name = meta?.Name ?? "",
            Type = p.Type,
            Id = meta?.Id ?? "",
            Workspace = meta?.Workspace ?? "",
            Credentials = SandboxMapping.Copy(p.Credentials),
            Config = SandboxMapping.Copy(p.Config),
            Labels = SandboxMapping.Copy(meta?.Labels),
            Annotations = SandboxMapping.Copy(meta?.Annotations),
            CredentialExpirationTimes = p.CredentialExpirationTimes.Count == 0
                ? Provider.EmptyTimes
                : p.CredentialExpirationTimes.ToDictionary(kv => kv.Key, kv => ToTime(kv.Value)!.Value),
            ProfileWorkspace = p.ProfileWorkspace,
            CredentialHandles = p.CredentialHandles.Count == 0
                ? Provider.EmptyHandles
                : p.CredentialHandles.ToDictionary(
                    kv => kv.Key, kv => new CredentialHandle(kv.Value.Driver, kv.Value.Handle, SandboxMapping.Copy(kv.Value.Metadata))),
            ResourceVersion = meta?.ResourceVersion ?? 0,
            CreatedAt = ToTime(meta?.CreatedTime),
            DeletedAt = ToTime(meta?.DeletionTime),
        };
    }

    public static Openshell.Datamodel.V1.Provider ToWire(Provider p)
    {
        var wire = new Openshell.Datamodel.V1.Provider
        {
            Type = p.Type,
            Metadata = new ObjectMeta
            {
                Id = p.Id,
                Name = p.Name,
                Workspace = p.Workspace,
                ResourceVersion = p.ResourceVersion,
            },
            ProfileWorkspace = p.ProfileWorkspace,
        };
        SandboxMapping.AddAll(wire.Credentials, p.Credentials);
        SandboxMapping.AddAll(wire.Config, p.Config);
        SandboxMapping.AddAll(wire.Metadata.Labels, p.Labels);
        SandboxMapping.AddAll(wire.Metadata.Annotations, p.Annotations);
        foreach (var (key, time) in p.CredentialExpirationTimes)
        {
            wire.CredentialExpirationTimes[key] = ToWire(time);
        }

        // Credential handles are gateway-owned and never accepted as input.
        return wire;
    }

    public static Wire.UpdateProviderRequest ToUpdateRequest(Provider provider, UpdateProviderOptions? options, string workspace)
    {
        var request = new Wire.UpdateProviderRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Provider = ToWire(provider),
            RequestId = options?.RequestId ?? "",
        };
        foreach (var (key, time) in options?.CredentialExpirationTimes ?? provider.CredentialExpirationTimes)
        {
            request.CredentialExpirationTimes[key] = ToWire(time);
        }

        if (options?.ClearCredentialExpirationKeys is { } clear)
        {
            request.ClearCredentialExpirationKeys.AddRange(clear.Order(StringComparer.Ordinal));
        }

        return request;
    }

    public static DeletionResult ToDeletion(Wire.DeletionOutcome outcome) => new((DeletionOutcome)(int)outcome);
}
