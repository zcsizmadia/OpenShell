// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Testing;

internal sealed class FakeProviderProfileClient : IProviderProfileClient
{
    private readonly FakeGateway _gateway;
    private readonly string _workspace;
    private readonly string _scope;
    private readonly bool _platform;

    public FakeProviderProfileClient(FakeGateway gateway, string workspace, bool platform)
    {
        _gateway = gateway;
        _workspace = workspace;
        _platform = platform;
        _scope = platform ? "" : workspace;
        Platform = platform ? this : new FakeProviderProfileClient(gateway, workspace, platform: true);
    }

    public IProviderProfileClient Platform { get; }

    public Pager<ProviderProfile> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return FakePaging.Create(_gateway, "ProviderProfiles.List", _workspace, null, options.PageSize, options.PageToken, () =>
            _gateway.Profiles
                .Where(p => p.Key.Workspace == _scope || (!_platform && p.Key.Workspace == ""))
                .Select(p => p.Value)
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .ToList());
    }

    public ValueTask<ProviderProfile> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ValueTask.FromResult(_gateway.Run("ProviderProfiles.Get", _workspace, id, cancellationToken, () =>
        {
            return _gateway.Profiles.TryGetValue(new Key(_scope, id), out var profile)
                || (!_platform && _gateway.Profiles.TryGetValue(new Key("", id), out profile))
                ? profile
                : throw FakeGateway.NotFound("provider profile", id);
        }));
    }

    public ValueTask<ImportProfilesResult> ImportAsync(IReadOnlyList<ProfileImportItem> items, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return ValueTask.FromResult(_gateway.Run("ProviderProfiles.Import", _workspace, null, cancellationToken, () =>
        {
            RequireScope();
            var diagnostics = Check(items, existingConflicts: true);
            if (diagnostics.Any(d => d.Severity == "error"))
            {
                return new ImportProfilesResult(diagnostics, [], false);
            }

            var stored = items.Select(Store).ToArray();
            return new ImportProfilesResult(diagnostics, stored, true);
        }));
    }

    public ValueTask<UpdateProfileResult> UpdateAsync(string id, ulong expectedResourceVersion, ProfileImportItem item, ProfileMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(item);
        return ValueTask.FromResult(_gateway.Run("ProviderProfiles.Update", _workspace, id, cancellationToken, () =>
        {
            RequireScope();
            var existing = Custom(id);
            if (existing.ResourceVersion != expectedResourceVersion)
            {
                throw FakeGateway.Conflict($"provider profile '{id}' resource version {existing.ResourceVersion} does not match expected {expectedResourceVersion}");
            }

            var diagnostics = Check([item], existingConflicts: false);
            if (item.Profile.Id != id)
            {
                diagnostics = [.. diagnostics, new ProfileDiagnostic(item.Source, item.Profile.Id, "id", "the profile id cannot change", "error")];
            }

            return diagnostics.Any(d => d.Severity == "error")
                ? new UpdateProfileResult(diagnostics, null, false)
                : new UpdateProfileResult(diagnostics, Store(item), true);
        }));
    }

    public ValueTask<LintProfilesResult> LintAsync(IReadOnlyList<ProfileImportItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return ValueTask.FromResult(_gateway.Run("ProviderProfiles.Lint", _workspace, null, cancellationToken, () =>
        {
            var diagnostics = Check(items, existingConflicts: false);
            return new LintProfilesResult(diagnostics, diagnostics.All(d => d.Severity != "error"));
        }));
    }

    public ValueTask<DeletionResult> DeleteAsync(string id, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ValueTask.FromResult(_gateway.Run("ProviderProfiles.Delete", _workspace, id, cancellationToken, () =>
        {
            var key = new Key(_scope, id);
            var existed = _gateway.Profiles.TryGetValue(key, out var profile);
            if (existed && profile!.Source == "builtin")
            {
                throw FakeGateway.Invalid($"builtin provider profile '{id}' cannot be deleted");
            }

            if (!existed)
            {
                RejectPlatformOwned(id);
            }

            return FakeGateway.DeleteResult(existed, options, "provider profile", id, () => _gateway.Profiles.Remove(key));
        }));
    }

    private void RequireScope()
    {
        if (!_platform)
        {
            _gateway.RequireWorkspace(_workspace);
        }
    }

    /// <summary>A workspace client cannot change a profile that only exists at platform scope, such as a builtin.</summary>
    private void RejectPlatformOwned(string id)
    {
        if (!_platform && _gateway.Profiles.ContainsKey(new Key("", id)))
        {
            throw FakeGateway.Invalid($"provider profile '{id}' is platform-scoped and cannot be changed from a workspace");
        }
    }

    private ProviderProfile Custom(string id)
    {
        if (!_gateway.Profiles.TryGetValue(new Key(_scope, id), out var profile))
        {
            RejectPlatformOwned(id);
            throw FakeGateway.NotFound("provider profile", id);
        }

        return profile.Source == "builtin" ? throw FakeGateway.Invalid($"builtin provider profile '{id}' cannot be changed") : profile;
    }

    private ProviderProfile Store(ProfileImportItem item)
    {
        var profile = item.Profile with
        {
            ResourceVersion = _gateway.NextVersion(),
            Source = "user",
            Scope = _platform ? "platform" : "workspace",
        };
        _gateway.Profiles[new Key(_scope, profile.Id)] = profile;
        return profile;
    }

    private List<ProfileDiagnostic> Check(IReadOnlyList<ProfileImportItem> items, bool existingConflicts)
    {
        var diagnostics = new List<ProfileDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var profile = item.Profile;
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                diagnostics.Add(new ProfileDiagnostic(item.Source, profile.Id, "id", "a profile id is required", "error"));
                continue;
            }

            if (!seen.Add(profile.Id))
            {
                diagnostics.Add(new ProfileDiagnostic(item.Source, profile.Id, "id", "the profile id appears more than once", "error"));
            }
            else if (existingConflicts && _gateway.Profiles.ContainsKey(new Key(_scope, profile.Id)))
            {
                diagnostics.Add(new ProfileDiagnostic(item.Source, profile.Id, "id", "a profile with this id already exists", "error"));
            }

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                diagnostics.Add(new ProfileDiagnostic(item.Source, profile.Id, "display_name", "a display name is recommended", "warning"));
            }

            diagnostics.AddRange(profile.Credentials
                .Where(c => string.IsNullOrWhiteSpace(c.Name))
                .Select(_ => new ProfileDiagnostic(item.Source, profile.Id, "credentials.name", "every credential needs a name", "error")));
        }

        return diagnostics;
    }
}

internal sealed class FakeProviderRefreshClient(FakeGateway gateway, string workspace) : IProviderRefreshClient
{
    public ValueTask<IReadOnlyList<RefreshStatus>> GetStatusAsync(string provider, string? credentialKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return ValueTask.FromResult(gateway.Run<IReadOnlyList<RefreshStatus>>("ProviderRefresh.GetStatus", workspace, provider, cancellationToken, () =>
        {
            _ = gateway.FindProvider(workspace, provider);
            return
            [
                .. gateway.Refreshes
                    .Where(r => r.Key.Workspace == workspace && r.Key.Sandbox == provider && (string.IsNullOrEmpty(credentialKey) || r.Key.Name == credentialKey))
                    .OrderBy(r => r.Key.Name, StringComparer.Ordinal)
                    .Select(r => ToStatus(r.Value, "configured")),
            ];
        }));
    }

    public ValueTask<RefreshStatus> ConfigureAsync(RefreshConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ValueTask.FromResult(gateway.Run("ProviderRefresh.Configure", workspace, config.Provider, cancellationToken, () =>
        {
            var provider = gateway.FindProvider(workspace, config.Provider);
            if (!provider.Credentials.ContainsKey(config.CredentialKey))
            {
                throw FakeGateway.Invalid($"provider '{provider.Name}' has no credential '{config.CredentialKey}'");
            }

            if (config.Strategy == RefreshStrategy.Unspecified)
            {
                throw FakeGateway.Invalid("a refresh strategy is required");
            }

            if (config.SecretMaterialKeys?.FirstOrDefault(k => !config.Material.ContainsKey(k)) is { } missing)
            {
                throw FakeGateway.Invalid($"secret material key '{missing}' is not present in the material");
            }

            var entry = new RefreshEntry
            {
                Provider = provider.Name,
                ProviderId = provider.Id,
                CredentialKey = config.CredentialKey,
                Strategy = config.Strategy,
                ExpiresAt = config.ExpiresAt,
            };
            gateway.Refreshes[new SubKey(workspace, provider.Name, config.CredentialKey)] = entry;
            return ToStatus(entry, "configured");
        }));
    }

    public ValueTask<RefreshStatus> RotateAsync(string provider, string credentialKey, string? requestId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);
        return ValueTask.FromResult(gateway.Run("ProviderRefresh.Rotate", workspace, provider, cancellationToken, () =>
        {
            _ = gateway.FindProvider(workspace, provider);
            if (!gateway.Refreshes.TryGetValue(new SubKey(workspace, provider, credentialKey), out var entry))
            {
                throw FakeGateway.NotFound("credential refresh", credentialKey);
            }

            var now = gateway.Time.GetUtcNow();
            entry.Rotations++;
            entry.LastRefreshAt = now;
            entry.ExpiresAt = now + TimeSpan.FromHours(1);
            return ToStatus(entry, "rotated");
        }));
    }

    public ValueTask<DeletionResult> DeleteAsync(string provider, string credentialKey, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);
        return ValueTask.FromResult(gateway.Run("ProviderRefresh.Delete", workspace, provider, cancellationToken, () =>
        {
            _ = gateway.FindProvider(workspace, provider);
            var key = new SubKey(workspace, provider, credentialKey);
            return FakeGateway.DeleteResult(gateway.Refreshes.ContainsKey(key), options, "credential refresh", credentialKey, () => gateway.Refreshes.Remove(key));
        }));
    }

    private static RefreshStatus ToStatus(RefreshEntry e, string status) => new()
    {
        Provider = e.Provider,
        ProviderId = e.ProviderId,
        CredentialKey = e.CredentialKey,
        Strategy = e.Strategy,
        Status = status,
        ExpiresAt = e.ExpiresAt,
        LastRefreshAt = e.LastRefreshAt,
    };
}

internal sealed class FakeSandboxProviderClient(FakeGateway gateway, string workspace) : ISandboxProviderClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public ValueTask<AttachProviderResult> AttachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return ValueTask.FromResult(gateway.Run("SandboxProviders.Attach", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            var stored = gateway.FindProvider(workspace, provider);
            CheckVersion(entry, options);
            if (entry.Providers.Contains(provider))
            {
                return new AttachProviderResult(gateway.Project(entry), false, null);
            }

            entry.Providers.Add(provider);
            gateway.ChangeProviderSet(entry);
            var receipt = gateway.NewReceipt(entry, provider, stored, ProviderMutationKind.Attach, gateway.NextId("mutation"));
            return new AttachProviderResult(gateway.Project(entry), true, receipt.Receipt);
        }));
    }

    public ValueTask<DetachProviderResult> DetachAsync(string sandbox, string provider, SandboxProviderMutationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return ValueTask.FromResult(gateway.Run("SandboxProviders.Detach", workspace, sandbox, cancellationToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            CheckVersion(entry, options);
            if (!entry.Providers.Remove(provider))
            {
                return new DetachProviderResult(gateway.Project(entry), false, null);
            }

            gateway.ChangeProviderSet(entry);
            var receipt = gateway.NewReceipt(entry, provider, null, ProviderMutationKind.Detach, gateway.NextId("mutation"));
            return new DetachProviderResult(gateway.Project(entry), true, receipt.Receipt);
        }));
    }

    public Pager<Provider> List(string sandbox, ListOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        options ??= new ListOptions();
        return FakePaging.Create(gateway, "SandboxProviders.List", workspace, sandbox, options.PageSize, options.PageToken, () =>
        {
            var entry = gateway.FindSandbox(workspace, sandbox);
            return entry.Providers
                .Order(StringComparer.Ordinal)
                .Where(name => gateway.ProviderStore.ContainsKey(new Key(workspace, name)))
                .Select(name => FakeGateway.ToProvider(gateway.ProviderStore[new Key(workspace, name)]))
                .ToList();
        });
    }

    public ValueTask<ProviderReadinessStatus> GetStatusAsync(string sandbox, string? provider = null, string? receiptId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandbox);
        return string.IsNullOrEmpty(provider) && string.IsNullOrEmpty(receiptId)
            ? throw new ArgumentException("a provider name or receipt id is required", nameof(provider))
            : ValueTask.FromResult(gateway.Run("SandboxProviders.GetStatus", workspace, sandbox, cancellationToken, () =>
            {
                var entry = gateway.FindSandbox(workspace, sandbox);
                return gateway.StatusOf(Locate(entry, provider, receiptId));
            }));
    }

    public ValueTask<ProviderReadinessStatus> GetStatusAsync(ProviderMutationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ProviderReadinessLogic.ValidateReceipt(receipt);
        return ValueTask.FromResult(gateway.Run("SandboxProviders.GetStatus", workspace, receipt.Desired!.Sandbox, cancellationToken, () =>
            gateway.StatusOf(KnownReceipt(receipt.ReceiptId, receipt.Desired!.Sandbox))));
    }

    public ValueTask<ProviderWaitResult> WaitAsync(ProviderMutationReceipt receipt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        ProviderReadinessLogic.ValidateReceipt(receipt);
        return WaitCoreAsync(receipt, ProviderReadinessLogic.PersistedStatus(receipt), timeout, cancellationToken);
    }

    public ValueTask<ProviderWaitResult> WaitAsync(ProviderReadinessStatus status, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        ProviderReadinessLogic.ValidateStatus(status);
        return WaitCoreAsync(status.Receipt!, status, timeout, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ProviderWaitResult>> WaitAllAsync(IReadOnlyCollection<ProviderMutationReceipt> receipts, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ProviderReadinessLogic.ValidateTimeout(timeout);
        return await Task.WhenAll(receipts.Select(r => WaitAsync(r, timeout, cancellationToken).AsTask())).ConfigureAwait(false);
    }

    private async ValueTask<ProviderWaitResult> WaitCoreAsync(ProviderMutationReceipt receipt, ProviderReadinessStatus initial, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new Deadline(gateway.Time, timeout, cancellationToken);
        return await ProviderReadinessLogic.WaitAsync(
            receipt,
            initial,
            deadline,
            PollInterval,
            gateway.Time,
            ct => GetStatusAsync(receipt, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static void CheckVersion(SandboxEntry entry, SandboxProviderMutationOptions? options)
    {
        if (options is { ExpectedResourceVersion: not 0 } && options.ExpectedResourceVersion != entry.ResourceVersion)
        {
            throw FakeGateway.Conflict($"sandbox '{entry.Name}' resource version {entry.ResourceVersion} does not match expected {options.ExpectedResourceVersion}");
        }
    }

    private ReceiptEntry KnownReceipt(string receiptId, string sandbox) =>
        gateway.Receipts.TryGetValue(receiptId, out var found) && found.Receipt.Workspace == workspace && found.SandboxName == sandbox
            ? found
            : throw FakeGateway.NotFound("provider receipt", receiptId);

    private ReceiptEntry Locate(SandboxEntry sandbox, string? provider, string? receiptId)
    {
        if (!string.IsNullOrEmpty(receiptId))
        {
            return KnownReceipt(receiptId, sandbox.Name);
        }

        var latest = gateway.Receipts.Values
            .Where(r => r.Receipt.Workspace == workspace && r.SandboxName == sandbox.Name && r.Receipt.Provider == provider)
            .MaxBy(r => r.Receipt.ReceiptId, StringComparer.Ordinal);
        if (latest is not null)
        {
            return latest;
        }

        // An attachment made before any receipt existed is reported as observed desired state.
        var stored = sandbox.Providers.Contains(provider!) ? gateway.FindProvider(workspace, provider!) : null;
        return stored is null
            ? throw FakeGateway.NotFound("provider attachment", provider!)
            : gateway.NewReceipt(sandbox, provider!, stored, ProviderMutationKind.Observe, gateway.NextId("mutation"));
    }
}
