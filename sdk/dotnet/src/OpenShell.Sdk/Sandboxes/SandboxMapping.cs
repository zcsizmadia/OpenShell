// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Wire = Openshell.V1;

namespace OpenShell;

internal static class SandboxMapping
{
    public static Sandbox ToDomain(Wire.SandboxResponse response) =>
        ToDomain(response.Sandbox, response.ServiceUrls);

    public static Sandbox ToDomain(Wire.Sandbox s, MapField<string, string>? serviceUrls = null)
    {
        var meta = s.Metadata;
        var status = s.Status;
        return new Sandbox
        {
            Id = meta?.Id ?? "",
            Name = meta?.Name ?? "",
            Workspace = meta?.Workspace ?? "",
            Phase = status is null ? SandboxPhase.Unspecified : (SandboxPhase)(int)status.Phase,
            Labels = Copy(meta?.Labels),
            Annotations = Copy(meta?.Annotations),
            ResourceVersion = meta?.ResourceVersion ?? 0,
            CreatedAt = meta?.CreatedTime?.ToDateTimeOffset(),
            DeletedAt = meta?.DeletionTime?.ToDateTimeOffset(),
            ServiceUrls = Copy(serviceUrls),
            Conditions = status is null
                ? []
                : status.Conditions
                    .Select(c => new SandboxCondition(c.Type, c.Status, c.Reason, c.Message, c.TransitionTime?.ToDateTimeOffset()))
                    .ToArray(),
            ExitCode = status is { HasExitCode: true } ? status.ExitCode : null,
            CurrentPolicyVersion = status?.CurrentPolicyVersion ?? 0,
            RestartCount = status?.RestartCount ?? 0,
            HostKeyFingerprint = s.HostKeyFingerprint is { Length: > 0 } fp ? fp : null,
            Image = s.Spec?.Template?.Image is { Length: > 0 } img ? img : null,
        };
    }

    public static Wire.SandboxSpec ToWire(SandboxSpec spec)
    {
        var wire = new Wire.SandboxSpec { Tty = spec.Tty, RestartPolicy = (Wire.SandboxRestartPolicy)(int)spec.RestartPolicy };
        if (spec.LogLevel is { } level)
        {
            wire.LogLevel = level;
        }

        if (spec.Command is { } cmd)
        {
            wire.Command.AddRange(cmd);
        }

        AddAll(wire.Environment, spec.Environment);
        if (spec.Providers is { } providers)
        {
            wire.Providers.AddRange(providers);
        }

        if (spec.Template is not null || spec.Image is not null)
        {
            var t = spec.Template;
            var template = new Wire.SandboxTemplate { Image = t?.Image ?? spec.Image ?? "" };
            if (t is not null)
            {
                template.RuntimeClassName = t.RuntimeClassName ?? "";
                AddAll(template.Labels, t.Labels);
                AddAll(template.Annotations, t.Annotations);
                AddAll(template.Environment, t.Environment);
                if (t.UserNamespaces is { } un)
                {
                    template.UserNamespaces = un;
                }

                if (t.ResourcesJson is { } res)
                {
                    template.Resources = Struct.Parser.ParseJson(res);
                }

                if (t.DriverConfigJson is { } dc)
                {
                    template.DriverConfig = Struct.Parser.ParseJson(dc);
                }
            }

            wire.Template = template;
        }

        if (spec.Policy is { } policy)
        {
            wire.Policy = policy;
        }

        spec.RawSpec?.Invoke(wire);
        return wire;
    }

    public static Wire.SandboxServiceExposure ToWire(ServiceExposure e) => new()
    {
        Service = e.Name,
        TargetPort = e.TargetPort,
        AuthorizationMode = (Wire.ServiceAuthorizationMode)(int)e.AuthorizationMode,
    };

    public static DeletionResult ToDomain(Wire.DeleteSandboxResponse r) =>
        new((DeletionOutcome)(int)r.Outcome, r.SandboxId is { Length: > 0 } id ? id : null);

    internal static IReadOnlyDictionary<string, string> Copy(MapField<string, string>? map) =>
        map is null or { Count: 0 } ? Sandbox.EmptyMap : new Dictionary<string, string>(map);

    internal static void AddAll(MapField<string, string> target, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (k, v) in source)
        {
            target[k] = v;
        }
    }
}
