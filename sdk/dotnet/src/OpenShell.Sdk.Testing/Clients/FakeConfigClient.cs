// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using Google.Protobuf;
using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    internal Dictionary<string, SettingValue> GlobalSettings { get; } = [with(StringComparer.Ordinal)];

    internal List<PolicyRevisionEntry> GlobalRevisions { get; } = [];

    internal WireSandbox.SandboxPolicy? GlobalPolicy { get; set; }

    internal ulong GlobalSettingsRevision { get; set; } = 1;

    /// <summary>Marks how a sandbox reported a policy revision, as the supervisor would. Use it to script failed or superseded loads.</summary>
    public void SetPolicyLoadStatus(string sandbox, uint version, PolicyLoadStatus status, string error = "", string? workspace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        lock (Sync)
        {
            var entry = FindSandbox(workspace ?? Options.DefaultWorkspace, sandbox);
            var revision = entry.Revisions.FirstOrDefault(r => r.Version == version) ?? throw NotFound("policy revision", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            revision.Status = status;
            revision.LoadError = error;
            revision.LoadedAt = status == PolicyLoadStatus.Loaded ? Time.GetUtcNow() : null;
        }
    }

    internal static string HashPolicy(WireSandbox.SandboxPolicy policy) =>
        Convert.ToHexString(SHA256.HashData(policy.ToByteArray())).ToLowerInvariant();

    /// <summary>Stores a new policy revision on a sandbox and makes it the effective policy.</summary>
    internal PolicyRevisionEntry AddRevision(SandboxEntry entry, WireSandbox.SandboxPolicy policy, IReadOnlyDictionary<string, string>? provenance)
    {
        entry.PolicyVersion++;
        var stored = policy.Clone();
        stored.Version = entry.PolicyVersion;
        entry.Policy = stored;
        entry.ConfigRevision++;
        var now = Time.GetUtcNow();
        var revision = new PolicyRevisionEntry
        {
            Version = entry.PolicyVersion,
            Hash = HashPolicy(stored),
            Policy = stored.Clone(),
            CreatedAt = now,
            LoadedAt = now,
        };
        if (provenance is not null)
        {
            foreach (var (k, v) in provenance)
            {
                revision.Provenance[k] = v;
            }
        }

        entry.Revisions.Add(revision);
        return revision;
    }
}

internal sealed class FakeConfigClient(FakeGateway gateway, string workspace) : IConfigClient
{
    public ValueTask<SandboxConfig> GetSandboxAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return ValueTask.FromResult(gateway.Run("Config.GetSandbox", workspace, sandbox, cancellationToken, () => Describe(gateway.FindSandbox(workspace, sandbox))));
    }

    public ValueTask<GatewayConfig> GetGatewayAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(gateway.Run("Config.GetGateway", "", null, cancellationToken, () =>
            new GatewayConfig(
                gateway.GlobalSettings.ToDictionary(p => p.Key, p => (SettingValue?)p.Value, StringComparer.Ordinal),
                gateway.GlobalSettingsRevision)));

    public ValueTask<ConfigUpdateResult> UpdateAsync(ConfigUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return ValueTask.FromResult(gateway.Run("Config.Update", update.Global ? "" : workspace, update.Sandbox, cancellationToken, () => Apply(update)));
    }

    public ValueTask<ConfigUpdateResult> SetPolicyAsync(string sandbox, WireSandbox.SandboxPolicy policy, SetPolicyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(policy);
        return UpdateAsync(
            new ConfigUpdate
            {
                Sandbox = sandbox,
                Policy = policy,
                ExpectedResourceVersion = options?.ExpectedResourceVersion ?? 0,
                Annotations = options?.Annotations,
                RequestId = options?.RequestId,
            },
            cancellationToken);
    }

    public ValueTask<ConfigUpdateResult> MergePolicyAsync(string sandbox, IReadOnlyList<Wire.PolicyMergeOperation> operations, SetPolicyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentNullException.ThrowIfNull(operations);
        return UpdateAsync(
            new ConfigUpdate
            {
                Sandbox = sandbox,
                MergeOperations = operations,
                ExpectedResourceVersion = options?.ExpectedResourceVersion ?? 0,
                Annotations = options?.Annotations,
                RequestId = options?.RequestId,
            },
            cancellationToken);
    }

    public ValueTask<ConfigUpdateResult> SetSettingAsync(string sandbox, string key, SettingValue value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return UpdateAsync(new ConfigUpdate { Sandbox = sandbox, SettingKey = key, SettingValue = value }, cancellationToken);
    }

    public ValueTask<ConfigUpdateResult> SetGlobalPolicyAsync(WireSandbox.SandboxPolicy policy, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return UpdateAsync(new ConfigUpdate { Global = true, Policy = policy, RequestId = requestId }, cancellationToken);
    }

    public ValueTask<ConfigUpdateResult> SetGlobalSettingAsync(string key, SettingValue value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return UpdateAsync(new ConfigUpdate { Global = true, SettingKey = key, SettingValue = value }, cancellationToken);
    }

    public ValueTask<ConfigUpdateResult> DeleteGlobalSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return UpdateAsync(new ConfigUpdate { Global = true, SettingKey = key, DeleteSetting = true }, cancellationToken);
    }

    private SandboxConfig Describe(SandboxEntry entry)
    {
        var settings = new Dictionary<string, EffectiveSetting>(StringComparer.Ordinal);
        foreach (var (key, value) in entry.Settings)
        {
            settings[key] = new EffectiveSetting(value, SettingScope.Sandbox);
        }

        // A global value takes precedence over the sandbox's own.
        foreach (var (key, value) in gateway.GlobalSettings)
        {
            settings[key] = new EffectiveSetting(value, SettingScope.Global);
        }

        var global = gateway.GlobalPolicy;
        var policy = (global ?? entry.Policy).Clone();
        return new SandboxConfig
        {
            Policy = policy,
            PolicyVersion = entry.PolicyVersion,
            PolicyHash = FakeGateway.HashPolicy(policy),
            Settings = settings,
            ConfigRevision = entry.ConfigRevision + gateway.GlobalSettingsRevision,
            PolicySource = global is null ? PolicySource.Sandbox : PolicySource.Global,
            GlobalPolicyVersion = global is null ? 0 : gateway.GlobalRevisions[^1].Version,
            ProviderEnvRevision = entry.ProviderEnvRevision,
            PolicyValidationFailureMode = "fail_closed",
            Workspace = entry.Workspace,
        };
    }

    private ConfigUpdateResult Apply(ConfigUpdate update)
    {
        var kinds = (update.Policy is not null ? 1 : 0) + (update.MergeOperations is not null ? 1 : 0) + (update.SettingKey is not null ? 1 : 0);
        return kinds != 1
            ? throw FakeGateway.Invalid("exactly one of policy, merge operations or a setting must be supplied")
            : update.Global ? ApplyGlobal(update) : ApplySandbox(update);
    }

    private ConfigUpdateResult ApplyGlobal(ConfigUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Sandbox))
        {
            throw FakeGateway.Invalid("global updates must not name a sandbox");
        }

        if (update.MergeOperations is not null)
        {
            throw FakeGateway.Invalid("merge operations apply to sandbox scope only");
        }

        var deleted = false;
        var hash = "";
        var version = 0u;
        if (update.Policy is { } policy)
        {
            var stored = policy.Clone();
            version = (uint)gateway.GlobalRevisions.Count + 1;
            stored.Version = version;
            gateway.GlobalPolicy = stored;
            hash = FakeGateway.HashPolicy(stored);
            gateway.GlobalRevisions.Add(new PolicyRevisionEntry
            {
                Version = version,
                Hash = hash,
                Policy = stored.Clone(),
                CreatedAt = gateway.Time.GetUtcNow(),
                LoadedAt = gateway.Time.GetUtcNow(),
            });
        }
        else if (update.DeleteSetting)
        {
            deleted = gateway.GlobalSettings.Remove(update.SettingKey!);
        }
        else
        {
            gateway.GlobalSettings[update.SettingKey!] = update.SettingValue ?? throw FakeGateway.Invalid("a setting value is required");
        }

        gateway.GlobalSettingsRevision++;
        return new ConfigUpdateResult(version, hash, gateway.GlobalSettingsRevision, deleted, new Dictionary<string, string>());
    }

    private ConfigUpdateResult ApplySandbox(ConfigUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Sandbox))
        {
            throw FakeGateway.Invalid("a sandbox is required for sandbox-scoped updates");
        }

        var entry = gateway.FindSandbox(workspace, update.Sandbox);
        if (update.ExpectedResourceVersion != 0 && update.ExpectedResourceVersion != entry.ResourceVersion)
        {
            throw FakeGateway.Conflict($"sandbox '{entry.Name}' resource version {entry.ResourceVersion} does not match expected {update.ExpectedResourceVersion}");
        }

        if (update.Policy is { } policy)
        {
            EnsureStaticFieldsUnchanged(entry, policy);
            gateway.AddRevision(entry, policy, update.Annotations);
        }
        else if (update.MergeOperations is { } operations)
        {
            var merged = entry.Policy.Clone();
            foreach (var operation in operations)
            {
                Merge(merged, operation);
            }

            gateway.AddRevision(entry, merged, update.Annotations);
        }
        else
        {
            if (update.DeleteSetting)
            {
                throw FakeGateway.Invalid("only global settings can be deleted");
            }

            entry.Settings[update.SettingKey!] = update.SettingValue ?? throw FakeGateway.Invalid("a setting value is required");
            entry.ConfigRevision++;
        }

        if (update.Annotations is not null)
        {
            foreach (var (k, v) in update.Annotations)
            {
                entry.Annotations[k] = v;
            }
        }

        gateway.Touch(entry);
        var latest = entry.Revisions[^1];
        return new ConfigUpdateResult(entry.PolicyVersion, latest.Hash, entry.ConfigRevision, false, new Dictionary<string, string>(entry.Annotations));
    }

    /// <summary>A sandbox can only change network policies; filesystem, landlock and process are fixed at creation.</summary>
    private static void EnsureStaticFieldsUnchanged(SandboxEntry entry, WireSandbox.SandboxPolicy next)
    {
        var baseline = entry.CreatePolicy;
        if (!Same(baseline?.Filesystem, next.Filesystem) || !Same(baseline?.Landlock, next.Landlock) || !Same(baseline?.Process, next.Process))
        {
            throw FakeGateway.Invalid("filesystem, landlock and process policy cannot change after the sandbox is created");
        }
    }

    private static bool Same<T>(T? a, T? b)
        where T : class, IMessage<T>, new() =>
        (a ?? new T()).Equals(b ?? new T());

    private static void Merge(WireSandbox.SandboxPolicy policy, Wire.PolicyMergeOperation operation)
    {
        switch (operation.OperationCase)
        {
            case Wire.PolicyMergeOperation.OperationOneofCase.AddRule:
                policy.NetworkPolicies[operation.AddRule.RuleName] = operation.AddRule.Rule.Clone();
                break;
            case Wire.PolicyMergeOperation.OperationOneofCase.RemoveRule:
                if (!policy.NetworkPolicies.Remove(operation.RemoveRule.RuleName))
                {
                    throw FakeGateway.NotFound("network rule", operation.RemoveRule.RuleName);
                }

                break;
            case Wire.PolicyMergeOperation.OperationOneofCase.RemoveEndpoint:
                RemoveEndpoint(policy, operation.RemoveEndpoint);
                break;
            default:
                throw new OpenShellException(OpenShellErrorKind.Unimplemented, $"policy merge operation {operation.OperationCase} is not supported by the fake gateway");
        }
    }

    private static void RemoveEndpoint(WireSandbox.SandboxPolicy policy, Wire.RemoveNetworkEndpoint target)
    {
        if (!policy.NetworkPolicies.TryGetValue(target.RuleName, out var rule))
        {
            throw FakeGateway.NotFound("network rule", target.RuleName);
        }

        var remaining = rule.Endpoints.Where(e => !(e.Host == target.Host && e.Port == target.Port)).ToArray();
        if (remaining.Length == rule.Endpoints.Count)
        {
            throw FakeGateway.NotFound("network endpoint", $"{target.Host}:{target.Port}");
        }

        rule.Endpoints.Clear();
        rule.Endpoints.AddRange(remaining);
    }
}
