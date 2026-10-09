// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>Reads and updates sandbox and gateway configuration.</summary>
public interface IConfigClient
{
    /// <summary>Gets the effective policy and settings of a sandbox.</summary>
    ValueTask<SandboxConfig> GetSandboxAsync(string sandbox, CancellationToken cancellationToken = default);

    /// <summary>Gets the gateway-global settings.</summary>
    ValueTask<GatewayConfig> GetGatewayAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies an arbitrary <see cref="ConfigUpdate"/>.</summary>
    ValueTask<ConfigUpdateResult> UpdateAsync(ConfigUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the sandbox-scoped policy. Only <c>network_policies</c> may differ from the create-time policy;
    /// the gateway rejects changes to the static fields, so set those when the sandbox is created.
    /// </summary>
    ValueTask<ConfigUpdateResult> SetPolicyAsync(string sandbox, WireSandbox.SandboxPolicy policy, SetPolicyOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Applies incremental network policy merge operations to a sandbox.</summary>
    ValueTask<ConfigUpdateResult> MergePolicyAsync(string sandbox, IReadOnlyList<Wire.PolicyMergeOperation> operations, SetPolicyOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Upserts one sandbox-scoped setting. The gateway rejects sandbox-scoped deletes.</summary>
    ValueTask<ConfigUpdateResult> SetSettingAsync(string sandbox, string key, SettingValue value, CancellationToken cancellationToken = default);

    /// <summary>Replaces the gateway-global policy for every sandbox.</summary>
    ValueTask<ConfigUpdateResult> SetGlobalPolicyAsync(WireSandbox.SandboxPolicy policy, string? requestId = null, CancellationToken cancellationToken = default);

    /// <summary>Upserts one gateway-global setting.</summary>
    ValueTask<ConfigUpdateResult> SetGlobalSettingAsync(string key, SettingValue value, CancellationToken cancellationToken = default);

    /// <summary>Deletes one gateway-global setting.</summary>
    ValueTask<ConfigUpdateResult> DeleteGlobalSettingAsync(string key, CancellationToken cancellationToken = default);
}

internal sealed class ConfigClient(ClientCore core, string workspace) : IConfigClient
{
    public async ValueTask<SandboxConfig> GetSandboxAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        var request = new WireSandbox.GetSandboxConfigRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = sandbox };
        var r = await core.UnaryAsync(o => core.Grpc.GetSandboxConfigAsync(request, o), cancellationToken).ConfigureAwait(false);

        var settings = new Dictionary<string, EffectiveSetting>(r.Settings.Count);
        foreach (var (key, value) in r.Settings)
        {
            settings[key] = new EffectiveSetting(SettingValue.FromWire(value.Value), (SettingScope)(int)value.Scope);
        }

        return new SandboxConfig
        {
            Policy = r.Policy,
            PolicyVersion = r.Version,
            PolicyHash = r.PolicyHash,
            Settings = settings,
            ConfigRevision = r.ConfigRevision,
            PolicySource = (PolicySource)(int)r.PolicySource,
            GlobalPolicyVersion = r.GlobalPolicyVersion,
            ProviderEnvRevision = r.ProviderEnvRevision,
            PolicyValidationFailureMode = r.PolicyValidationFailureMode,
            Workspace = r.Workspace,
        };
    }

    public async ValueTask<GatewayConfig> GetGatewayAsync(CancellationToken cancellationToken = default)
    {
        var r = await core.UnaryAsync(o => core.Grpc.GetGatewayConfigAsync(new WireSandbox.GetGatewayConfigRequest(), o), cancellationToken).ConfigureAwait(false);
        var settings = new Dictionary<string, SettingValue?>(r.Settings.Count);
        foreach (var (key, value) in r.Settings)
        {
            settings[key] = SettingValue.FromWire(value);
        }

        return new GatewayConfig(settings, r.SettingsRevision);
    }

    public async ValueTask<ConfigUpdateResult> UpdateAsync(ConfigUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var hasSandbox = !string.IsNullOrWhiteSpace(update.Sandbox);
        if (update.Global && hasSandbox)
        {
            throw new ArgumentException("a global update must not name a sandbox", nameof(update));
        }

        if (!update.Global && !hasSandbox)
        {
            throw new ArgumentException("a sandbox-scoped update requires a sandbox name", nameof(update));
        }

        if ((update.SettingValue is not null || update.DeleteSetting) && string.IsNullOrEmpty(update.SettingKey))
        {
            throw new ArgumentException("a setting update requires a setting key", nameof(update));
        }

        var request = new Wire.UpdateConfigRequest
        {
            Global = update.Global,
            Sandbox = update.Sandbox ?? "",
            SettingKey = update.SettingKey ?? "",
            DeleteSetting = update.DeleteSetting,
            ExpectedResourceVersion = update.ExpectedResourceVersion,
            RequestId = update.RequestId ?? "",
        };
        if (!update.Global)
        {
            request.WorkspaceScope = ClientCore.Scope(workspace);
        }

        if (update.Policy is { } policy)
        {
            request.Policy = policy;
        }

        if (update.SettingValue is { } value)
        {
            request.SettingValue = value.ToWire();
        }

        if (update.MergeOperations is { } ops)
        {
            request.MergeOperations.AddRange(ops);
        }

        SandboxMapping.AddAll(request.Annotations, update.Annotations);

        var r = await core.UnaryAsync(o => core.Grpc.UpdateConfigAsync(request, o), cancellationToken).ConfigureAwait(false);
        return new ConfigUpdateResult(r.Version, r.PolicyHash, r.SettingsRevision, r.Deleted, SandboxMapping.Copy(r.Annotations));
    }

    public async ValueTask<ConfigUpdateResult> SetPolicyAsync(string sandbox, WireSandbox.SandboxPolicy policy, SetPolicyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var result = await UpdateAsync(ForSandbox(sandbox, options) with { Policy = policy }, cancellationToken).ConfigureAwait(false);
        return await WaitIfRequestedAsync(sandbox, result, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigUpdateResult> MergePolicyAsync(string sandbox, IReadOnlyList<Wire.PolicyMergeOperation> operations, SetPolicyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException("at least one merge operation is required", nameof(operations));
        }

        var result = await UpdateAsync(ForSandbox(sandbox, options) with { MergeOperations = operations }, cancellationToken).ConfigureAwait(false);
        return await WaitIfRequestedAsync(sandbox, result, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ConfigUpdateResult> SetSettingAsync(string sandbox, string key, SettingValue value, CancellationToken cancellationToken = default)
    {
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

    private static ConfigUpdate ForSandbox(string sandbox, SetPolicyOptions? options) => new()
    {
        Sandbox = sandbox,
        ExpectedResourceVersion = options?.ExpectedResourceVersion ?? 0,
        Annotations = options?.Annotations,
        RequestId = options?.RequestId,
    };

    private async ValueTask<ConfigUpdateResult> WaitIfRequestedAsync(string sandbox, ConfigUpdateResult result, SetPolicyOptions? options, CancellationToken cancellationToken)
    {
        if (options is not { Wait: true })
        {
            return result;
        }

        using var deadline = new Deadline(core.Time, options.WaitTimeout, cancellationToken);
        var message = $"timed out waiting for policy '{result.PolicyHash}' on sandbox '{sandbox}'";
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            try
            {
                var config = await GetSandboxAsync(sandbox, deadline.Token).ConfigureAwait(false);
                if (config.PolicyHash == result.PolicyHash)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (deadline.TimedOut)
            {
                throw Deadline.Expired(message);
            }

            delay = await deadline.SleepAsync(delay, TimeSpan.FromSeconds(2), message).ConfigureAwait(false);
        }
    }
}
