// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell;

/// <summary>The scope that controls a setting.</summary>
public enum SettingScope
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>Set on the sandbox.</summary>
    Sandbox = 1,
    /// <summary>Set gateway-wide.</summary>
    Global = 2,
}

/// <summary>Where a sandbox policy payload came from.</summary>
public enum PolicySource
{
    /// <summary>Not reported.</summary>
    Unspecified = 0,
    /// <summary>The sandbox's own policy.</summary>
    Sandbox = 1,
    /// <summary>The gateway-global policy.</summary>
    Global = 2,
}

/// <summary>A typed setting value: string, bool, int64 or bytes.</summary>
public abstract record SettingValue
{
    private protected SettingValue() { }

    /// <summary>Creates a string value.</summary>
    public static SettingValue FromString(string value) => new StringSetting(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Creates a boolean value.</summary>
    public static SettingValue FromBool(bool value) => new BoolSetting(value);

    /// <summary>Creates a 64-bit integer value.</summary>
    public static SettingValue FromInt(long value) => new IntSetting(value);

    /// <summary>Creates a byte-array value; the bytes are copied.</summary>
    public static SettingValue FromBytes(ReadOnlySpan<byte> value) => new BytesSetting(value.ToArray());

    /// <summary>Implicit conversion from <see cref="string"/>.</summary>
    public static implicit operator SettingValue(string value) => FromString(value);

    /// <summary>Implicit conversion from <see cref="bool"/>.</summary>
    public static implicit operator SettingValue(bool value) => FromBool(value);

    /// <summary>Implicit conversion from <see cref="long"/>.</summary>
    public static implicit operator SettingValue(long value) => FromInt(value);

    internal static SettingValue? FromWire(WireSandbox.SettingValue? wire) => wire?.ValueCase switch
    {
        WireSandbox.SettingValue.ValueOneofCase.StringValue => new StringSetting(wire.StringValue),
        WireSandbox.SettingValue.ValueOneofCase.BoolValue => new BoolSetting(wire.BoolValue),
        WireSandbox.SettingValue.ValueOneofCase.IntValue => new IntSetting(wire.IntValue),
        WireSandbox.SettingValue.ValueOneofCase.BytesValue => new BytesSetting(wire.BytesValue.ToByteArray()),
        _ => null,
    };

    internal WireSandbox.SettingValue ToWire() => this switch
    {
        StringSetting s => new WireSandbox.SettingValue { StringValue = s.Value },
        BoolSetting b => new WireSandbox.SettingValue { BoolValue = b.Value },
        IntSetting i => new WireSandbox.SettingValue { IntValue = i.Value },
        // The constructor is private protected, so the four records above are the only subtypes.
        _ => new WireSandbox.SettingValue { BytesValue = Google.Protobuf.ByteString.CopyFrom(((BytesSetting)this).Value) },
    };
}

/// <summary>A string setting.</summary>
public sealed record StringSetting(string Value) : SettingValue;

/// <summary>A boolean setting.</summary>
public sealed record BoolSetting(bool Value) : SettingValue;

/// <summary>A 64-bit integer setting.</summary>
public sealed record IntSetting(long Value) : SettingValue;

/// <summary>A byte-array setting, compared by content.</summary>
public sealed record BytesSetting(byte[] Value) : SettingValue
{
    /// <inheritdoc />
    public bool Equals(BytesSetting? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

/// <summary>A setting value paired with the scope it resolved from.</summary>
/// <remarks><see cref="Value"/> is null for a registered key that has no configured value.</remarks>
public sealed record EffectiveSetting(SettingValue? Value, SettingScope Scope);

/// <summary>The effective configuration of a sandbox.</summary>
public sealed record SandboxConfig
{
    /// <summary>The effective policy; this is the generated wire type.</summary>
    public WireSandbox.SandboxPolicy? Policy { get; init; }

    /// <summary>Current policy version, increasing per sandbox.</summary>
    public uint PolicyVersion { get; init; }

    /// <summary>SHA-256 hash of the serialized policy.</summary>
    public string PolicyHash { get; init; } = "";

    /// <summary>Effective settings by key, excluding the reserved policy key.</summary>
    public IReadOnlyDictionary<string, EffectiveSetting> Settings { get; init; } = new Dictionary<string, EffectiveSetting>();

    /// <summary>Fingerprint of the effective policy plus settings.</summary>
    public ulong ConfigRevision { get; init; }

    /// <summary>Source of the policy payload.</summary>
    public PolicySource PolicySource { get; init; }

    /// <summary>Version of the global policy revision when <see cref="PolicySource"/> is global; otherwise zero.</summary>
    public uint GlobalPolicyVersion { get; init; }

    /// <summary>Fingerprint of the provider credential inputs attached to the sandbox.</summary>
    public ulong ProviderEnvRevision { get; init; }

    /// <summary>Gateway posture for rejected policy generations: <c>fail_closed</c> or <c>retain_last_valid</c>.</summary>
    public string PolicyValidationFailureMode { get; init; } = "";

    /// <summary>The workspace the sandbox belongs to.</summary>
    public string Workspace { get; init; } = "";
}

/// <summary>Gateway-global settings.</summary>
/// <param name="Settings">Settings by key; a registered key without a value maps to null.</param>
/// <param name="SettingsRevision">Monotonic revision of the global settings.</param>
public sealed record GatewayConfig(IReadOnlyDictionary<string, SettingValue?> Settings, ulong SettingsRevision);

/// <summary>A configuration mutation. Prefer the typed helpers on <see cref="IConfigClient"/>.</summary>
public sealed record ConfigUpdate
{
    /// <summary>Target sandbox. Required for sandbox scope, must be empty for global scope.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Apply at gateway-global scope.</summary>
    public bool Global { get; init; }

    /// <summary>
    /// The new policy. Sandbox scope may only change <c>network_policies</c>; the static fields (filesystem,
    /// landlock, process) must match the create-time policy. Global scope applies in full with no merge.
    /// </summary>
    public WireSandbox.SandboxPolicy? Policy { get; init; }

    /// <summary>Single setting key to upsert or delete.</summary>
    public string? SettingKey { get; init; }

    /// <summary>Setting value for an upsert.</summary>
    public SettingValue? SettingValue { get; init; }

    /// <summary>Delete <see cref="SettingKey"/>. Only global deletes are supported by the gateway.</summary>
    public bool DeleteSetting { get; init; }

    /// <summary>Incremental policy merge operations; sandbox scope only.</summary>
    public IReadOnlyList<Wire.PolicyMergeOperation>? MergeOperations { get; init; }

    /// <summary>Optimistic-concurrency pin on the sandbox resource version; zero uses the current version.</summary>
    public ulong ExpectedResourceVersion { get; init; }

    /// <summary>Opaque annotations stored with the policy revision. Must not contain secrets.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Idempotency key; a replay within 24 hours returns the original result.</summary>
    public string? RequestId { get; init; }
}

/// <summary>The result of a configuration update.</summary>
/// <param name="Version">Assigned policy version.</param>
/// <param name="PolicyHash">SHA-256 hash of the applied policy.</param>
/// <param name="SettingsRevision">Revision of the settings scope that changed.</param>
/// <param name="Deleted">True when a delete removed an existing key.</param>
/// <param name="Annotations">Sandbox annotations after the update; empty for global updates.</param>
public sealed record ConfigUpdateResult(
    uint Version,
    string PolicyHash,
    ulong SettingsRevision,
    bool Deleted,
    IReadOnlyDictionary<string, string> Annotations);

/// <summary>Options for sandbox-scoped policy updates.</summary>
public sealed record SetPolicyOptions
{
    /// <summary>Pin the sandbox resource version for optimistic concurrency.</summary>
    public ulong ExpectedResourceVersion { get; init; }

    /// <summary>Annotations stored with the revision.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Idempotency key.</summary>
    public string? RequestId { get; init; }

    /// <summary>Poll the sandbox config until the applied policy hash is observed.</summary>
    public bool Wait { get; init; }

    /// <summary>Bound for <see cref="Wait"/>; defaults to 60 seconds.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
