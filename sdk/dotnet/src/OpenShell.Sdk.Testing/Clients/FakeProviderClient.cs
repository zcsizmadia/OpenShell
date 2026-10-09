// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    private const string Redacted = "[REDACTED]";

    internal static Provider ToProvider(ProviderEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Type = e.Type,
        Workspace = e.Workspace,
        Credentials = e.Credentials.ToDictionary(p => p.Key, _ => Redacted, StringComparer.Ordinal),
        Config = new Dictionary<string, string>(e.Config),
        Labels = new Dictionary<string, string>(e.Labels),
        Annotations = new Dictionary<string, string>(e.Annotations),
        CredentialExpirationTimes = new Dictionary<string, DateTimeOffset>(e.Expirations),
        ProfileWorkspace = e.ProfileWorkspace,
        ResourceVersion = e.ResourceVersion,
        CreatedAt = e.CreatedAt,
    };

    internal ProviderEntry FindProvider(string workspace, string name) =>
        ProviderStore.TryGetValue(new Key(workspace, name), out var entry) ? entry : throw NotFound("provider", name);

    /// <summary>
    /// Forces how the supervisor reports a provider mutation, for example <see cref="ProviderReadinessState.Failed"/>.
    /// Without an override, a mutation is ready once its sandbox is, and pending before that.
    /// </summary>
    /// <param name="receiptId">The receipt id from an attach, detach or update result.</param>
    /// <param name="state">The state to report. Ready and revoked must match the receipt's mutation kind.</param>
    /// <param name="reason">The reason to report with a non-complete state.</param>
    public void SetProviderReadiness(string receiptId, ProviderReadinessState state, ProviderReadinessReason reason = ProviderReadinessReason.Unspecified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        lock (Sync)
        {
            if (!Receipts.TryGetValue(receiptId, out var entry))
            {
                throw NotFound("provider receipt", receiptId);
            }

            if (state is ProviderReadinessState.Ready or ProviderReadinessState.Revoked
                && state != ProviderReadinessLogic.CompletionState(entry.Receipt))
            {
                throw new ArgumentException($"state {state} does not match the receipt's {entry.Receipt.Kind} mutation", nameof(state));
            }

            entry.StateOverride = state;
            entry.ReasonOverride = reason;
        }
    }

    internal ReceiptEntry NewReceipt(SandboxEntry sandbox, string providerName, ProviderEntry? provider, ProviderMutationKind kind, string mutationId)
    {
        var now = Time.GetUtcNow();
        var receipt = new ProviderMutationReceipt
        {
            ReceiptId = NextId("receipt"),
            MutationId = mutationId,
            Provider = providerName,
            Workspace = sandbox.Workspace,
            Kind = kind,
            Desired = new ProviderDesiredIdentity
            {
                SandboxId = sandbox.Id,
                Sandbox = sandbox.Name,
                AttachmentEpoch = sandbox.AttachmentEpoch,
                ProviderId = provider?.Id ?? "",
                ProviderResourceVersion = provider?.ResourceVersion ?? 0,
                ProviderEnvRevision = sandbox.ProviderEnvRevision,
                ConfigRevision = sandbox.ConfigRevision,
                PolicyHash = HashPolicy(sandbox.Policy),
            },
            PersistedTime = new ExactTimestamp(now.ToUnixTimeSeconds(), (int)(now.Ticks % TimeSpan.TicksPerSecond * 100)),
        };
        var entry = new ReceiptEntry { Receipt = receipt, SandboxName = sandbox.Name };
        Receipts[receipt.ReceiptId] = entry;
        return entry;
    }

    /// <summary>Marks a provider-set change on a sandbox: new attachment epoch and bumped revisions.</summary>
    internal void ChangeProviderSet(SandboxEntry sandbox)
    {
        sandbox.AttachmentEpoch = NextId("epoch");
        sandbox.ProviderEnvRevision++;
        sandbox.ConfigRevision++;
        Touch(sandbox);
    }

    internal ProviderReadinessStatus StatusOf(ReceiptEntry entry)
    {
        var receipt = entry.Receipt;
        var now = Time.GetUtcNow();
        var sandbox = FindSandbox(receipt.Workspace, entry.SandboxName);
        ProviderReadinessState state;
        ProviderReadinessReason reason;
        if (entry.StateOverride is { } forced)
        {
            state = forced;
            reason = entry.ReasonOverride;
        }
        else if (sandbox.Phase == SandboxPhase.Ready)
        {
            state = ProviderReadinessLogic.CompletionState(receipt);
            reason = ProviderReadinessReason.Unspecified;
        }
        else
        {
            state = ProviderReadinessState.Pending;
            reason = ProviderReadinessReason.WaitingForSupervisor;
        }

        var complete = state is ProviderReadinessState.Ready or ProviderReadinessState.Revoked;
        var desired = receipt.Desired!;
        return new ProviderReadinessStatus
        {
            Receipt = receipt,
            State = state,
            Reason = reason,
            Observed = complete
                ? new ProviderReadinessObservation
                {
                    SessionId = "fake-session",
                    Sequence = 1,
                    AttachmentEpoch = desired.AttachmentEpoch,
                    ProviderEnvRevision = desired.ProviderEnvRevision,
                    ConfigRevision = desired.ConfigRevision,
                    PolicyHash = desired.PolicyHash,
                    CredentialsInstalled = true,
                    PolicyActive = true,
                    LaunchEnvironmentInstalled = true,
                    ProcessInstanceId = "fake-process",
                }
                : null,
            NetworkInstanceId = "fake-network",
            ObservedTime = complete ? now : null,
            EvaluatedTime = now,
            Operation = new ProviderOperation(
                "op-" + receipt.ReceiptId,
                complete ? ConfigUpdateOperationState.Applied : ConfigUpdateOperationState.Pending,
                complete ? ConfigApplyOutcome.Applied : ConfigApplyOutcome.Unspecified,
                ""),
        };
    }
}

internal sealed partial class FakeProviderClient : IProviderClient
{
    private readonly FakeGateway _gateway;
    private readonly string _workspace;

    public FakeProviderClient(FakeGateway gateway, string workspace)
    {
        _gateway = gateway;
        _workspace = workspace;
        Profiles = new FakeProviderProfileClient(gateway, workspace, platform: false);
        Refresh = new FakeProviderRefreshClient(gateway, workspace);
    }

    public IProviderProfileClient Profiles { get; }

    public IProviderRefreshClient Refresh { get; }

    public ValueTask<Provider> CreateAsync(Provider provider, CreateProviderOptions? options = null, CancellationToken cancellationToken = default)
    {
        Validate(provider);
        return ValueTask.FromResult(_gateway.Run("Providers.Create", _workspace, provider.Name, cancellationToken, () =>
        {
            _gateway.RequireWorkspace(_workspace);
            if (_gateway.ReplayName("provider", _workspace, options?.RequestId) is { } replay
                && _gateway.ProviderStore.TryGetValue(new Key(_workspace, replay), out var original))
            {
                return FakeGateway.ToProvider(original);
            }

            if (_gateway.ProviderStore.ContainsKey(new Key(_workspace, provider.Name)))
            {
                throw FakeGateway.AlreadyExists("provider", provider.Name);
            }

            var entry = Create(provider);
            _gateway.RememberRequest("provider", _workspace, options?.RequestId, provider.Name);
            return FakeGateway.ToProvider(entry);
        }));
    }

    public ValueTask<Provider> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(_gateway.Run("Providers.Get", _workspace, name, cancellationToken, () => FakeGateway.ToProvider(_gateway.FindProvider(_workspace, name))));
    }

    public Pager<Provider> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return FakePaging.Create(_gateway, "Providers.List", _workspace, null, options.PageSize, options.PageToken, () =>
        {
            var selector = LabelSelector.Compile(options.LabelSelector);
            return _gateway.ProviderStore.Values
                .Where(p => FakePaging.InScope(p.Workspace, _workspace, options.AllWorkspaces) && selector(p.Labels))
                .OrderBy(p => p.Workspace, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(FakeGateway.ToProvider)
                .ToList();
        });
    }

    public ValueTask<ProviderUpdateResult> UpdateAsync(Provider provider, UpdateProviderOptions? options = null, CancellationToken cancellationToken = default)
    {
        Validate(provider);
        return ValueTask.FromResult(_gateway.Run("Providers.Update", _workspace, provider.Name, cancellationToken, () => Update(provider, options)));
    }

    public ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(_gateway.Run("Providers.Delete", _workspace, name, cancellationToken, () =>
        {
            var key = new Key(_workspace, name);
            _gateway.ProviderStore.TryGetValue(key, out var entry);
            return entry is not null && _gateway.Sandboxes.Values.FirstOrDefault(s => s.Workspace == _workspace && s.Providers.Contains(name)) is { } attached
                ? throw FakeGateway.Conflict($"provider '{name}' is attached to sandbox '{attached.Name}'")
                : FakeGateway.DeleteResult(entry is not null, options, "provider", name, () => _gateway.ProviderStore.Remove(key), entry?.Id);
        }));
    }

    public ValueTask<Provider> EnsureAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        Validate(provider);
        return ValueTask.FromResult(_gateway.Run("Providers.Ensure", _workspace, provider.Name, cancellationToken, () =>
        {
            _gateway.RequireWorkspace(_workspace);
            return _gateway.ProviderStore.ContainsKey(new Key(_workspace, provider.Name))
                ? Update(provider, null).Provider
                : FakeGateway.ToProvider(Create(provider));
        }));
    }

    public ValueTask<IReadOnlyDictionary<string, ProviderCredentialValue>> GetCredentialsAsync(
        string name,
        IReadOnlyCollection<string> credentialKeys,
        ProviderCredentialsOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialKeys);
        var keys = credentialKeys.ToArray();
        ValidateCredentialsRequest(name, keys, options);

        return ValueTask.FromResult<IReadOnlyDictionary<string, ProviderCredentialValue>>(
            _gateway.Run("Providers.GetCredentials", options.Workspace, name, cancellationToken, () =>
            {
                var entry = _gateway.FindProvider(options.Workspace, name);
                var margin = options.MinimumRemainingLifetime is { } m && m > TimeSpan.Zero ? m : TimeSpan.FromMinutes(5);
                var result = new Dictionary<string, ProviderCredentialValue>(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    if (!entry.Credentials.TryGetValue(key, out var value))
                    {
                        throw FakeGateway.NotFound("credential", key);
                    }

                    DateTimeOffset? expiry = entry.Expirations.TryGetValue(key, out var at) ? at : null;
                    if (expiry is { } e && e - _gateway.Time.GetUtcNow() < margin)
                    {
                        throw FakeGateway.Conflict($"credential '{key}' expires within the required margin");
                    }

                    result[key] = new ProviderCredentialValue(value, expiry);
                }

                return result;
            }));
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex KeyPattern();

    private static OpenShellException InvalidInput(string message) => new(OpenShellErrorKind.InvalidArgument, message);

    private static void ValidateCredentialsRequest(string name, string[] keys, ProviderCredentialsOptions options)
    {
        if (string.IsNullOrEmpty(options.Workspace) || options.Workspace.Trim() != options.Workspace)
        {
            throw InvalidInput("an explicit canonical workspace is required");
        }

        if (string.IsNullOrEmpty(name) || name.Trim() != name || Encoding.UTF8.GetByteCount(name) > 253)
        {
            throw InvalidInput("a canonical provider name is required");
        }

        if (keys.Length is < 1 or > 32
            || keys.Any(k => k is null || k.Length > 256 || !KeyPattern().IsMatch(k))
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw InvalidInput("select between 1 and 32 unique runtime environment keys");
        }

        if (options.MinimumRemainingLifetime is { } lifetime && (lifetime < TimeSpan.Zero || lifetime > TimeSpan.FromHours(24)))
        {
            throw InvalidInput("MinimumRemainingLifetime must be between zero and 24 hours");
        }
    }

    private static void Validate(Provider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Type);
    }

    private ProviderEntry Create(Provider provider)
    {
        var entry = new ProviderEntry
        {
            Id = _gateway.NextId("prov"),
            Name = provider.Name,
            Workspace = _workspace,
            Type = provider.Type,
            CreatedAt = _gateway.Time.GetUtcNow(),
            ResourceVersion = _gateway.NextVersion(),
            ProfileWorkspace = provider.ProfileWorkspace,
            Credentials = new Dictionary<string, string>(provider.Credentials, StringComparer.Ordinal),
            Config = new Dictionary<string, string>(provider.Config, StringComparer.Ordinal),
            Labels = new Dictionary<string, string>(provider.Labels, StringComparer.Ordinal),
            Annotations = new Dictionary<string, string>(provider.Annotations, StringComparer.Ordinal),
            Expirations = new Dictionary<string, DateTimeOffset>(provider.CredentialExpirationTimes, StringComparer.Ordinal),
        };
        _gateway.ProviderStore[new Key(_workspace, provider.Name)] = entry;
        return entry;
    }

    private ProviderUpdateResult Update(Provider provider, UpdateProviderOptions? options)
    {
        var entry = _gateway.FindProvider(_workspace, provider.Name);
        if (!string.Equals(entry.Type, provider.Type, StringComparison.Ordinal))
        {
            throw FakeGateway.Invalid("a provider's type cannot change");
        }

        Merge(provider.Credentials, entry.Credentials);
        Merge(provider.Config, entry.Config);
        Merge(provider.Labels, entry.Labels);
        Merge(provider.Annotations, entry.Annotations);
        foreach (var (key, at) in options?.CredentialExpirationTimes ?? provider.CredentialExpirationTimes)
        {
            entry.Expirations[key] = at;
        }

        foreach (var key in options?.ClearCredentialExpirationKeys ?? [])
        {
            entry.Expirations.Remove(key);
        }

        entry.ResourceVersion = _gateway.NextVersion();
        var mutationId = _gateway.NextId("mutation");
        var receipts = new List<ProviderMutationReceipt>();
        foreach (var sandbox in _gateway.Sandboxes.Values.Where(s => s.Workspace == _workspace && s.Providers.Contains(entry.Name)).OrderBy(s => s.Name, StringComparer.Ordinal).ToArray())
        {
            _gateway.ChangeProviderSet(sandbox);
            receipts.Add(_gateway.NewReceipt(sandbox, entry.Name, entry, ProviderMutationKind.Update, mutationId).Receipt);
        }

        return new ProviderUpdateResult(FakeGateway.ToProvider(entry), mutationId, receipts);
    }

    private static void Merge(IReadOnlyDictionary<string, string> source, Dictionary<string, string> target)
    {
        foreach (var (k, v) in source)
        {
            target[k] = v;
        }
    }
}
