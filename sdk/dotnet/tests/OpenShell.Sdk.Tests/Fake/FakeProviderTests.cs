// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;

namespace OpenShell.Tests;

internal static class FakeProviderData
{
    public static Provider Github(string name = "gh") => new()
    {
        Name = name,
        Type = "github",
        Credentials = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "secret-value" },
        Config = new Dictionary<string, string> { ["org"] = "acme" },
        Labels = new Dictionary<string, string> { ["team"] = "a" },
    };

    public static ProviderCredentialsOptions Creds(string workspace = "default", TimeSpan? lifetime = null) =>
        new() { Workspace = workspace, MinimumRemainingLifetime = lifetime };
}

public class FakeProviderTests
{
    private static FakeOpenShellClient New(FakeGatewayOptions? options = null) => FakeCoreTests.New(options);

    [Test]
    public async Task Providers_are_created_with_redacted_credentials()
    {
        using var client = New();

        var created = await client.Providers.CreateAsync(FakeProviderData.Github());
        var read = await client.Providers.GetAsync("gh");
        var secrets = await client.Providers.GetCredentialsAsync("gh", ["GITHUB_TOKEN"], FakeProviderData.Creds());

        await Assert.That(created.Credentials["GITHUB_TOKEN"]).IsEqualTo("[REDACTED]");
        await Assert.That(read.Config["org"]).IsEqualTo("acme");
        await Assert.That(read.Workspace).IsEqualTo("default");
        await Assert.That(read.ResourceVersion).IsGreaterThan(0ul);
        await Assert.That(secrets["GITHUB_TOKEN"].Value).IsEqualTo("secret-value");
        await Assert.That(secrets["GITHUB_TOKEN"].ToString()).DoesNotContain("secret-value");
    }

    [Test]
    public async Task Create_rejects_duplicates_and_replays_request_ids()
    {
        using var client = New();
        var first = await client.Providers.CreateAsync(FakeProviderData.Github(), new CreateProviderOptions { RequestId = "r" });

        var replay = await client.Providers.CreateAsync(FakeProviderData.Github("other"), new CreateProviderOptions { RequestId = "r" });
        var duplicate = await Assert.That(async () => await client.Providers.CreateAsync(FakeProviderData.Github())).Throws<OpenShellException>();

        await Assert.That(replay.Id).IsEqualTo(first.Id);
        await Assert.That(duplicate!.Kind).IsEqualTo(OpenShellErrorKind.AlreadyExists);
    }

    [Test]
    public async Task Provider_arguments_are_validated()
    {
        using var client = New();

        await Assert.That(async () => await client.Providers.CreateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.CreateAsync(new Provider { Name = " ", Type = "x" })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.CreateAsync(new Provider { Name = "n", Type = " " })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.UpdateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.EnsureAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.GetAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.DeleteAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.GetAsync("nope")).Throws<OpenShellException>();
    }

    [Test]
    public async Task List_pages_scopes_and_filters()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github("b"));
        await client.Providers.CreateAsync(FakeProviderData.Github("a") with { Labels = new Dictionary<string, string> { ["team"] = "z" } });
        await client.InWorkspace("w").Providers.CreateAsync(FakeProviderData.Github("c"));

        var here = (await client.Providers.List(new ListOptions { PageSize = 1 }).ToListAsync()).Select(p => p.Name);
        var all = (await client.Providers.List(new ListOptions { AllWorkspaces = true }).ToListAsync()).Select(p => p.Name);
        var team = (await client.Providers.List(new ListOptions { LabelSelector = "team=z" }).ToListAsync()).Select(p => p.Name);

        await Assert.That(here).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(all).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(team).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task Update_merges_fields_and_issues_receipts_for_attached_sandboxes()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s1", new SandboxSpec { Providers = ["gh"] });
        await client.Sandboxes.CreateAsync("s2", FakeCoreTests.Spec());
        var before = await client.Providers.GetAsync("gh");

        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        var result = await client.Providers.UpdateAsync(
            FakeProviderData.Github() with
            {
                Credentials = new Dictionary<string, string> { ["EXTRA"] = "x" },
                Config = new Dictionary<string, string> { ["region"] = "eu" },
                Annotations = new Dictionary<string, string> { ["n"] = "1" },
                CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["EXTRA"] = expiry },
            });

        await Assert.That(result.Provider.ResourceVersion).IsGreaterThan(before.ResourceVersion);
        await Assert.That(result.Provider.Credentials.Keys).IsEquivalentTo(new[] { "GITHUB_TOKEN", "EXTRA" });
        await Assert.That(result.Provider.Config.Keys).IsEquivalentTo(new[] { "org", "region" });
        await Assert.That(result.Provider.Annotations["n"]).IsEqualTo("1");
        await Assert.That(result.Provider.CredentialExpirationTimes["EXTRA"]).IsEqualTo(expiry);
        var receipt = result.TargetReceipts.Single();
        await Assert.That(receipt.Kind).IsEqualTo(ProviderMutationKind.Update);
        await Assert.That(receipt.MutationId).IsEqualTo(result.MutationId);
        await Assert.That(receipt.Desired!.Sandbox).IsEqualTo("s1");
        await Assert.That(receipt.Desired.ProviderId).IsEqualTo(before.Id);
    }

    [Test]
    public async Task Update_options_override_and_clear_expirations()
    {
        using var client = New();
        var soon = DateTimeOffset.UtcNow.AddDays(2);
        await client.Providers.CreateAsync(FakeProviderData.Github() with
        {
            CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["GITHUB_TOKEN"] = DateTimeOffset.UtcNow.AddDays(1) },
        });

        await client.Providers.UpdateAsync(
            FakeProviderData.Github(),
            new UpdateProviderOptions { CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["GITHUB_TOKEN"] = soon } });
        var moved = (await client.Providers.GetAsync("gh")).CredentialExpirationTimes["GITHUB_TOKEN"];
        await client.Providers.UpdateAsync(FakeProviderData.Github(), new UpdateProviderOptions { ClearCredentialExpirationKeys = ["GITHUB_TOKEN"] });

        await Assert.That(moved).IsEqualTo(soon);
        await Assert.That((await client.Providers.GetAsync("gh")).CredentialExpirationTimes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Update_errors_are_typed()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());

        var missing = await Assert.That(async () => await client.Providers.UpdateAsync(FakeProviderData.Github("nope"))).Throws<OpenShellException>();
        var retyped = await Assert.That(async () => await client.Providers.UpdateAsync(FakeProviderData.Github() with { Type = "gitlab" })).Throws<OpenShellException>();

        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(retyped!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Ensure_creates_then_updates()
    {
        using var client = New();

        var created = await client.Providers.EnsureAsync(FakeProviderData.Github());
        var updated = await client.Providers.EnsureAsync(FakeProviderData.Github() with { Config = new Dictionary<string, string> { ["org"] = "new" } });

        await Assert.That(updated.Id).IsEqualTo(created.Id);
        await Assert.That(updated.Config["org"]).IsEqualTo("new");
        await Assert.That(updated.ResourceVersion).IsGreaterThan(created.ResourceVersion);
    }

    [Test]
    public async Task Delete_is_blocked_while_a_sandbox_uses_the_provider()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s", new SandboxSpec { Providers = ["gh"] });

        var blocked = await Assert.That(async () => await client.Providers.DeleteAsync("gh")).Throws<OpenShellException>();
        await client.Sandboxes.DeleteAsync("s");
        var deleted = await client.Providers.DeleteAsync("gh");
        var tolerated = await client.Providers.DeleteAsync("gh", new DeleteOptions { AllowMissing = true });
        var missing = await Assert.That(async () => await client.Providers.DeleteAsync("gh")).Throws<OpenShellException>();

        await Assert.That(blocked!.IsConflict).IsTrue();
        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(missing!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Credential_retrieval_validates_its_inputs()
    {
        using var client = New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        var good = FakeProviderData.Creds();

        static async Task<OpenShellException> Reject(Func<Task> action)
        {
            return (await Assert.That(action).Throws<OpenShellException>())!;
        }

        await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", ["K"], null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", null!, good)).Throws<ArgumentNullException>();
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["K"], FakeProviderData.Creds("")).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["K"], FakeProviderData.Creds(" w")).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("", ["K"], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync(" gh", ["K"], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync(new string('n', 254), ["K"], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", [], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", Enumerable.Range(0, 33).Select(i => "K" + i).ToArray(), good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["bad-key"], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["K", "K"], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", [new string('K', 257)], good).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["K"], FakeProviderData.Creds(lifetime: TimeSpan.FromHours(25))).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.GetCredentialsAsync("gh", ["K"], FakeProviderData.Creds(lifetime: TimeSpan.FromSeconds(-1))).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Credential_retrieval_reports_missing_keys_providers_and_expiry()
    {
        var time = new FakeTimeProvider();
        using var client = New(new FakeGatewayOptions { TimeProvider = time });
        var now = time.GetUtcNow();
        await client.Providers.CreateAsync(FakeProviderData.Github() with
        {
            Credentials = new Dictionary<string, string> { ["SHORT"] = "a", ["LONG"] = "b", ["PLAIN"] = "c" },
            CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["SHORT"] = now.AddMinutes(1), ["LONG"] = now.AddHours(3) },
        });

        var absentKey = await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", ["NOPE"], FakeProviderData.Creds())).Throws<OpenShellException>();
        var noProvider = await Assert.That(async () => await client.Providers.GetCredentialsAsync("zzz", ["K"], FakeProviderData.Creds())).Throws<OpenShellException>();
        var expiring = await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", ["SHORT"], FakeProviderData.Creds())).Throws<OpenShellException>();
        var strict = await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", ["LONG"], FakeProviderData.Creds(lifetime: TimeSpan.FromHours(4)))).Throws<OpenShellException>();
        var fine = await client.Providers.GetCredentialsAsync("gh", ["LONG", "PLAIN"], FakeProviderData.Creds(lifetime: TimeSpan.FromHours(1)));
        var defaultMargin = await client.Providers.GetCredentialsAsync("gh", ["PLAIN"], FakeProviderData.Creds(lifetime: TimeSpan.Zero));

        await Assert.That(absentKey!.IsNotFound).IsTrue();
        await Assert.That(noProvider!.IsNotFound).IsTrue();
        await Assert.That(expiring!.IsConflict).IsTrue();
        await Assert.That(strict!.IsConflict).IsTrue();
        await Assert.That(fine["LONG"].ExpirationTime).IsEqualTo(now.AddHours(3));
        await Assert.That(fine["PLAIN"].ExpirationTime).IsNull();
        await Assert.That(defaultMargin.Count).IsEqualTo(1);
    }
}

public class FakeProfileTests
{
    private static ProfileImportItem Item(string id, string display = "Name") =>
        new(new ProviderProfile { Id = id, DisplayName = display, Credentials = [new ProfileCredential { Name = "TOKEN" }] }, "test.yaml");

    [Test]
    public async Task Builtin_profiles_are_visible_and_immutable()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { BuiltinProfiles = [new ProviderProfile { Id = "github", DisplayName = "GitHub" }, new ProviderProfile { Id = "custom", DisplayName = "C", Source = "interceptor/x" }] });

        var listed = await client.Providers.Profiles.List().ToListAsync();
        var fetched = await client.Providers.Profiles.GetAsync("github");
        var update = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("github", 0, Item("github"))).Throws<OpenShellException>();
        var delete = await Assert.That(async () => await client.Providers.Profiles.DeleteAsync("github")).Throws<OpenShellException>();

        await Assert.That(listed.Select(p => p.Id)).IsEquivalentTo(new[] { "custom", "github" });
        await Assert.That(fetched.Source).IsEqualTo("builtin");
        await Assert.That(fetched.Scope).IsEqualTo("platform");
        await Assert.That(listed.Single(p => p.Id == "custom").Source).IsEqualTo("interceptor/x");
        await Assert.That(update!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(delete!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task Import_stores_workspace_profiles_that_can_be_read_updated_and_deleted()
    {
        using var client = FakeCoreTests.New();

        var imported = await client.Providers.Profiles.ImportAsync([Item("one"), Item("two")], new ProfileMutationOptions { RequestId = "r" });
        var fetched = await client.Providers.Profiles.GetAsync("one");
        var updated = await client.Providers.Profiles.UpdateAsync("one", fetched.ResourceVersion, Item("one", "Renamed"));
        var stale = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("one", fetched.ResourceVersion, Item("one"))).Throws<OpenShellException>();
        var deleted = await client.Providers.Profiles.DeleteAsync("two");
        var tolerated = await client.Providers.Profiles.DeleteAsync("two", new DeleteOptions { AllowMissing = true });

        await Assert.That(imported.Imported).IsTrue();
        await Assert.That(imported.Profiles.Count).IsEqualTo(2);
        await Assert.That(fetched.Scope).IsEqualTo("workspace");
        await Assert.That(fetched.Source).IsEqualTo("user");
        await Assert.That(updated.Updated).IsTrue();
        await Assert.That(updated.Profile!.DisplayName).IsEqualTo("Renamed");
        await Assert.That(stale!.IsConflict).IsTrue();
        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
    }

    [Test]
    public async Task Import_reports_diagnostics_instead_of_storing_invalid_profiles()
    {
        using var client = FakeCoreTests.New();
        await client.Providers.Profiles.ImportAsync([Item("dup")]);

        var result = await client.Providers.Profiles.ImportAsync(
            [
                Item("dup"),
                Item("twice"),
                Item("twice"),
                new ProfileImportItem(new ProviderProfile { Id = " " }),
                new ProfileImportItem(new ProviderProfile { Id = "bad", DisplayName = "", Credentials = [new ProfileCredential { Name = " " }] }),
            ]);

        await Assert.That(result.Imported).IsFalse();
        await Assert.That(result.Profiles.Count).IsEqualTo(0);
        await Assert.That(result.Diagnostics.Count(d => d.Severity == "error")).IsEqualTo(4);
        await Assert.That(result.Diagnostics.Any(d => d.Severity == "warning")).IsTrue();
        await Assert.That(async () => await client.Providers.Profiles.GetAsync("twice")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Lint_reports_validity_without_storing()
    {
        using var client = FakeCoreTests.New();

        var good = await client.Providers.Profiles.LintAsync([Item("fine")]);
        var bad = await client.Providers.Profiles.LintAsync([new ProfileImportItem(new ProviderProfile { Id = "" })]);

        await Assert.That(good.Valid).IsTrue();
        await Assert.That(bad.Valid).IsFalse();
        await Assert.That(async () => await client.Providers.Profiles.GetAsync("fine")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Update_rejects_changed_ids_and_unknown_profiles()
    {
        using var client = FakeCoreTests.New();
        await client.Providers.Profiles.ImportAsync([Item("one")]);
        var version = (await client.Providers.Profiles.GetAsync("one")).ResourceVersion;

        var renamed = await client.Providers.Profiles.UpdateAsync("one", version, Item("other"));
        var missing = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("zzz", 1, Item("zzz"))).Throws<OpenShellException>();

        await Assert.That(renamed.Updated).IsFalse();
        await Assert.That(renamed.Profile).IsNull();
        await Assert.That(renamed.Diagnostics.Any(d => d.Field == "id")).IsTrue();
        await Assert.That(missing!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task Platform_profiles_are_separate_from_workspace_profiles()
    {
        using var client = FakeCoreTests.New();
        await client.Providers.Profiles.Platform.ImportAsync([Item("shared")]);
        await client.Providers.Profiles.ImportAsync([Item("mine")]);

        var workspaceView = (await client.Providers.Profiles.List().ToListAsync()).Select(p => p.Id);
        var platformView = (await client.Providers.Profiles.Platform.List().ToListAsync()).Select(p => p.Id);
        var inherited = await client.Providers.Profiles.GetAsync("shared");

        await Assert.That(workspaceView).IsEquivalentTo(new[] { "mine", "shared" });
        await Assert.That(platformView).IsEquivalentTo(new[] { "shared" });
        await Assert.That(inherited.Scope).IsEqualTo("platform");
        await Assert.That(async () => await client.Providers.Profiles.Platform.GetAsync("mine")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Profile_arguments_are_validated_and_missing_workspaces_rejected()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { AutoCreateWorkspaces = false });

        await Assert.That(async () => await client.Providers.Profiles.GetAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Profiles.ImportAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.Profiles.UpdateAsync(" ", 1, Item("x"))).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("x", 1, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.Profiles.LintAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.Profiles.DeleteAsync(" ")).Throws<ArgumentException>();
        var ex = await Assert.That(async () => await client.InWorkspace("ghost").Providers.Profiles.ImportAsync([Item("x")])).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
        await client.Providers.Profiles.Platform.ImportAsync([Item("platform-ok")]);
    }
}

public class FakeRefreshTests
{
    private static async Task<FakeOpenShellClient> WithProviderAsync()
    {
        var client = FakeCoreTests.New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        return client;
    }

    private static RefreshConfig Config(string key = "GITHUB_TOKEN") => new()
    {
        Provider = "gh",
        CredentialKey = key,
        Strategy = RefreshStrategy.OAuth2RefreshToken,
        Material = new Dictionary<string, string> { ["refresh_token"] = "r" },
        SecretMaterialKeys = ["refresh_token"],
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Test]
    public async Task Refresh_can_be_configured_inspected_rotated_and_deleted()
    {
        using var client = await WithProviderAsync();

        var configured = await client.Providers.Refresh.ConfigureAsync(Config());
        var statuses = await client.Providers.Refresh.GetStatusAsync("gh");
        var filtered = await client.Providers.Refresh.GetStatusAsync("gh", "GITHUB_TOKEN");
        var other = await client.Providers.Refresh.GetStatusAsync("gh", "OTHER");
        var rotated = await client.Providers.Refresh.RotateAsync("gh", "GITHUB_TOKEN");
        var deleted = await client.Providers.Refresh.DeleteAsync("gh", "GITHUB_TOKEN");
        var tolerated = await client.Providers.Refresh.DeleteAsync("gh", "GITHUB_TOKEN", new DeleteOptions { AllowMissing = true });

        await Assert.That(configured.Status).IsEqualTo("configured");
        await Assert.That(configured.Strategy).IsEqualTo(RefreshStrategy.OAuth2RefreshToken);
        await Assert.That(configured.ProviderId.Length).IsGreaterThan(0);
        await Assert.That(statuses.Count).IsEqualTo(1);
        await Assert.That(filtered.Count).IsEqualTo(1);
        await Assert.That(other.Count).IsEqualTo(0);
        await Assert.That(rotated.Status).IsEqualTo("rotated");
        await Assert.That(rotated.LastRefreshAt).IsNotNull();
        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
    }

    [Test]
    public async Task Refresh_errors_are_typed()
    {
        using var client = await WithProviderAsync();

        static async Task<OpenShellException> Reject(Func<Task> action)
        {
            return (await Assert.That(action).Throws<OpenShellException>())!;
        }

        await Assert.That((await Reject(() => client.Providers.Refresh.ConfigureAsync(Config("MISSING")).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.Refresh.ConfigureAsync(Config() with { Strategy = RefreshStrategy.Unspecified }).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.Refresh.ConfigureAsync(Config() with { SecretMaterialKeys = ["absent"] }).AsTask())).Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That((await Reject(() => client.Providers.Refresh.ConfigureAsync(Config() with { Provider = "nope" }).AsTask())).IsNotFound).IsTrue();
        await Assert.That((await Reject(() => client.Providers.Refresh.RotateAsync("gh", "GITHUB_TOKEN").AsTask())).IsNotFound).IsTrue();
        await Assert.That((await Reject(() => client.Providers.Refresh.RotateAsync("nope", "K").AsTask())).IsNotFound).IsTrue();
        await Assert.That((await Reject(() => client.Providers.Refresh.GetStatusAsync("nope").AsTask())).IsNotFound).IsTrue();
        await Assert.That((await Reject(() => client.Providers.Refresh.DeleteAsync("nope", "K").AsTask())).IsNotFound).IsTrue();
        await Assert.That((await Reject(() => client.Providers.Refresh.DeleteAsync("gh", "GITHUB_TOKEN").AsTask())).IsNotFound).IsTrue();
        await Assert.That(async () => await client.Providers.Refresh.ConfigureAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.Refresh.GetStatusAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Refresh.RotateAsync(" ", "k")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Refresh.RotateAsync("gh", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Refresh.DeleteAsync(" ", "k")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.Refresh.DeleteAsync("gh", " ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Configuring_without_secret_keys_or_expiry_is_fine()
    {
        using var client = await WithProviderAsync();

        var status = await client.Providers.Refresh.ConfigureAsync(Config() with { SecretMaterialKeys = null, ExpiresAt = null });

        await Assert.That(status.ExpiresAt).IsNull();
    }
}

public class FakeSandboxProviderTests
{
    private static async Task<FakeOpenShellClient> NewAsync(FakeGatewayOptions? options = null)
    {
        var client = FakeCoreTests.New(options);
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s", FakeCoreTests.Spec());
        await client.Sandboxes.GetAsync("s");
        return client;
    }

    private static async Task Advance(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }

    [Test]
    public async Task Attach_and_detach_issue_receipts_and_change_the_provider_set()
    {
        using var client = await NewAsync();

        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var again = await client.SandboxProviders.AttachAsync("s", "gh");
        var listed = await client.SandboxProviders.List("s").ToListAsync();
        var detach = await client.SandboxProviders.DetachAsync("s", "gh");
        var noop = await client.SandboxProviders.DetachAsync("s", "gh");

        await Assert.That(attach.Attached).IsTrue();
        await Assert.That(attach.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Attach);
        await Assert.That(attach.Receipt.Desired!.ProviderId.Length).IsGreaterThan(0);
        await Assert.That(attach.Receipt.PersistedTime).IsNotNull();
        await Assert.That(again.Attached).IsFalse();
        await Assert.That(again.Receipt).IsNull();
        await Assert.That(listed.Single().Name).IsEqualTo("gh");
        await Assert.That(listed.Single().Credentials["GITHUB_TOKEN"]).IsEqualTo("[REDACTED]");
        await Assert.That(detach.Detached).IsTrue();
        await Assert.That(detach.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Detach);
        await Assert.That(detach.Receipt.Desired!.ProviderId).IsEqualTo("");
        await Assert.That(noop.Detached).IsFalse();
        await Assert.That(noop.Receipt).IsNull();
    }

    [Test]
    public async Task Attach_errors_are_typed()
    {
        using var client = await NewAsync();
        var version = (await client.Sandboxes.GetAsync("s")).ResourceVersion;

        var noProvider = await Assert.That(async () => await client.SandboxProviders.AttachAsync("s", "zzz")).Throws<OpenShellException>();
        var noSandbox = await Assert.That(async () => await client.SandboxProviders.AttachAsync("nope", "gh")).Throws<OpenShellException>();
        var stale = await Assert.That(async () => await client.SandboxProviders.AttachAsync("s", "gh", new SandboxProviderMutationOptions { ExpectedResourceVersion = version + 50 })).Throws<OpenShellException>();
        var staleDetach = await Assert.That(async () => await client.SandboxProviders.DetachAsync("s", "gh", new SandboxProviderMutationOptions { ExpectedResourceVersion = version + 50 })).Throws<OpenShellException>();
        await client.SandboxProviders.AttachAsync("s", "gh", new SandboxProviderMutationOptions { ExpectedResourceVersion = version });

        await Assert.That(noProvider!.IsNotFound).IsTrue();
        await Assert.That(noSandbox!.IsNotFound).IsTrue();
        await Assert.That(stale!.IsConflict).IsTrue();
        await Assert.That(staleDetach!.IsConflict).IsTrue();
        await Assert.That(async () => await client.SandboxProviders.AttachAsync(" ", "gh")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.AttachAsync("s", " ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.DetachAsync(" ", "gh")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.DetachAsync("s", " ")).Throws<ArgumentException>();
        await Assert.That(() => client.SandboxProviders.List(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Status_is_ready_once_the_sandbox_is_ready_and_revoked_after_detach()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var detach = await client.SandboxProviders.DetachAsync("s", "gh");

        var byReceipt = await client.SandboxProviders.GetStatusAsync("s", receiptId: attach.Receipt!.ReceiptId);
        var byObject = await client.SandboxProviders.GetStatusAsync(attach.Receipt);
        var latest = await client.SandboxProviders.GetStatusAsync("s", "gh");
        var revoked = await client.SandboxProviders.GetStatusAsync(detach.Receipt!);

        await Assert.That(byReceipt.State).IsEqualTo(ProviderReadinessState.Ready);
        await Assert.That(byReceipt.Observed!.CredentialsInstalled).IsTrue();
        await Assert.That(byReceipt.Operation!.State).IsEqualTo(ConfigUpdateOperationState.Applied);
        await Assert.That(byObject.Receipt).IsEqualTo(attach.Receipt);
        await Assert.That(latest.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Detach);
        await Assert.That(revoked.State).IsEqualTo(ProviderReadinessState.Revoked);
    }

    [Test]
    public async Task Status_is_pending_while_the_sandbox_is_not_ready()
    {
        var time = new FakeTimeProvider();
        using var client = FakeCoreTests.New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromMinutes(5) });
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s", FakeCoreTests.Spec());

        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var status = await client.SandboxProviders.GetStatusAsync(attach.Receipt!);

        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Pending);
        await Assert.That(status.Reason).IsEqualTo(ProviderReadinessReason.WaitingForSupervisor);
        await Assert.That(status.Observed).IsNull();
        await Assert.That(status.ObservedTime).IsNull();
        await Assert.That(status.Operation!.State).IsEqualTo(ConfigUpdateOperationState.Pending);
    }

    [Test]
    public async Task A_spec_attachment_is_reported_as_observed_state()
    {
        using var client = FakeCoreTests.New();
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s", new SandboxSpec { Providers = ["gh"] });

        var status = await client.SandboxProviders.GetStatusAsync("s", "gh");

        await Assert.That(status.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Observe);
        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Ready);
    }

    [Test]
    public async Task Status_lookups_report_unknown_targets_and_validate_arguments()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        await client.Sandboxes.CreateAsync("other", FakeCoreTests.Spec());

        var unattached = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("s", "zzz")).Throws<OpenShellException>();
        var wrongSandbox = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("other", receiptId: attach.Receipt!.ReceiptId)).Throws<OpenShellException>();
        var unknownReceipt = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("s", receiptId: "receipt-missing")).Throws<OpenShellException>();
        var foreign = attach.Receipt! with { ReceiptId = "receipt-foreign" };
        var foreignStatus = await Assert.That(async () => await client.SandboxProviders.GetStatusAsync(foreign)).Throws<OpenShellException>();

        await Assert.That(unattached!.IsNotFound).IsTrue();
        await Assert.That(wrongSandbox!.IsNotFound).IsTrue();
        await Assert.That(unknownReceipt!.IsNotFound).IsTrue();
        await Assert.That(foreignStatus!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync(" ", "gh")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync("s")).Throws<ArgumentException>();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync((ProviderMutationReceipt)null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.GetStatusAsync(new ProviderMutationReceipt())).Throws<OpenShellException>();
    }

    [Test]
    public async Task Waiting_completes_for_ready_authority()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var detach = await client.SandboxProviders.DetachAsync("s", "gh");

        var attached = await client.SandboxProviders.WaitAsync(attach.Receipt!, TimeSpan.FromSeconds(30));
        var detached = await client.SandboxProviders.WaitAsync(detach.Receipt!, TimeSpan.FromSeconds(30));
        var fromStatus = await client.SandboxProviders.WaitAsync(await client.SandboxProviders.GetStatusAsync(attach.Receipt!), TimeSpan.FromSeconds(30));
        var many = await client.SandboxProviders.WaitAllAsync([attach.Receipt!, detach.Receipt!], TimeSpan.FromSeconds(30));
        var none = await client.SandboxProviders.WaitAllAsync([], TimeSpan.FromSeconds(30));

        await Assert.That(attached.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(detached.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(fromStatus.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
        await Assert.That(many.Select(r => r.Outcome)).IsEquivalentTo(new[] { ProviderWaitOutcome.Complete, ProviderWaitOutcome.Complete });
        await Assert.That(none.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Waiting_times_out_while_pending_and_follows_the_sandbox_to_ready()
    {
        var time = new FakeTimeProvider();
        using var client = FakeCoreTests.New(new FakeGatewayOptions { TimeProvider = time, ProvisioningDelay = TimeSpan.FromSeconds(10) });
        await client.Providers.CreateAsync(FakeProviderData.Github());
        await client.Sandboxes.CreateAsync("s", FakeCoreTests.Spec());
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");

        var slow = client.SandboxProviders.WaitAsync(attach.Receipt!, TimeSpan.FromSeconds(3)).AsTask();
        await Advance(time, slow);
        var late = client.SandboxProviders.WaitAsync(attach.Receipt!, TimeSpan.FromSeconds(60)).AsTask();
        await Advance(time, late);

        await Assert.That((await slow).Outcome).IsEqualTo(ProviderWaitOutcome.TimedOut);
        await Assert.That((await late).Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
    }

    [Test]
    public async Task Scripted_readiness_ends_waits_as_terminal()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        client.Gateway.SetProviderReadiness(attach.Receipt!.ReceiptId, ProviderReadinessState.Failed, ProviderReadinessReason.CredentialInstallFailed);

        var wait = await client.SandboxProviders.WaitAsync(attach.Receipt, TimeSpan.FromSeconds(5));
        var status = await client.SandboxProviders.GetStatusAsync(attach.Receipt);

        await Assert.That(wait.Outcome).IsEqualTo(ProviderWaitOutcome.Terminal);
        await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Failed);
        await Assert.That(status.Reason).IsEqualTo(ProviderReadinessReason.CredentialInstallFailed);
    }

    [Test]
    public async Task Readiness_overrides_validate_their_arguments()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var detach = await client.SandboxProviders.DetachAsync("s", "gh");

        await Assert.That(() => client.Gateway.SetProviderReadiness(" ", ProviderReadinessState.Failed)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.SetProviderReadiness("receipt-missing", ProviderReadinessState.Failed)).Throws<OpenShellException>();
        await Assert.That(() => client.Gateway.SetProviderReadiness(attach.Receipt!.ReceiptId, ProviderReadinessState.Revoked)).Throws<ArgumentException>();
        await Assert.That(() => client.Gateway.SetProviderReadiness(detach.Receipt!.ReceiptId, ProviderReadinessState.Ready)).Throws<ArgumentException>();
        client.Gateway.SetProviderReadiness(attach.Receipt!.ReceiptId, ProviderReadinessState.Ready);
    }

    [Test]
    public async Task Wait_arguments_are_validated()
    {
        using var client = await NewAsync();
        var attach = await client.SandboxProviders.AttachAsync("s", "gh");
        var status = await client.SandboxProviders.GetStatusAsync(attach.Receipt!);

        await Assert.That(async () => await client.SandboxProviders.WaitAsync((ProviderMutationReceipt)null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync((ProviderReadinessStatus)null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAllAsync(null!, TimeSpan.FromSeconds(1))).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync(attach.Receipt!, TimeSpan.Zero)).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync(status, TimeSpan.FromDays(1))).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAllAsync([], TimeSpan.Zero)).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync(new ProviderMutationReceipt(), TimeSpan.FromSeconds(1))).Throws<OpenShellException>();
        await Assert.That(async () => await client.SandboxProviders.WaitAsync(new ProviderReadinessStatus(), TimeSpan.FromSeconds(1))).Throws<OpenShellException>();
    }
}
