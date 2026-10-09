// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ProviderClientTests
{
    private static Provider NewProvider(string name = "github-prod") => new()
    {
        Name = name,
        Type = "github",
        Credentials = new Dictionary<string, string> { ["TOKEN"] = "s3cret" },
        Config = new Dictionary<string, string> { ["org"] = "acme" },
        Labels = new Dictionary<string, string> { ["l"] = "1" },
        Annotations = new Dictionary<string, string> { ["a"] = "2" },
        CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["TOKEN"] = DateTimeOffset.UnixEpoch.AddDays(2) },
        ProfileWorkspace = "pw",
        CredentialHandles = new Dictionary<string, CredentialHandle> { ["TOKEN"] = new("vault", "h", Sandbox.EmptyMap) },
    };

    [Test]
    public async Task Create_sends_wire_provider_and_maps_response()
    {
        Wire.CreateProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.CreateProviderRequest, Wire.ProviderResponse>("CreateProvider", r =>
        {
            seen = r;
            return new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider() };
        });
        using var client = ClientTests.NewClient(invoker, "ws1");

        var created = await client.Providers.CreateAsync(NewProvider(), new CreateProviderOptions { RequestId = "req" });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(seen.RequestId).IsEqualTo("req");
        await Assert.That(seen.Provider.Type).IsEqualTo("github");
        await Assert.That(seen.Provider.Metadata.Name).IsEqualTo("github-prod");
        await Assert.That(seen.Provider.Metadata.Labels["l"]).IsEqualTo("1");
        await Assert.That(seen.Provider.Metadata.Annotations["a"]).IsEqualTo("2");
        await Assert.That(seen.Provider.Credentials["TOKEN"]).IsEqualTo("s3cret");
        await Assert.That(seen.Provider.Config["org"]).IsEqualTo("acme");
        await Assert.That(seen.Provider.ProfileWorkspace).IsEqualTo("pw");
        await Assert.That(seen.Provider.CredentialExpirationTimes.ContainsKey("TOKEN")).IsTrue();
        // Gateway-owned handles are never sent.
        await Assert.That(seen.Provider.CredentialHandles.Count).IsEqualTo(0);

        await Assert.That(created.Id).IsEqualTo("pid");
        await Assert.That(created.Name).IsEqualTo("github-prod");
        await Assert.That(created.Workspace).IsEqualTo("default");
        await Assert.That(created.ResourceVersion).IsEqualTo(3ul);
        await Assert.That(created.Labels["l"]).IsEqualTo("1");
        await Assert.That(created.Annotations["a"]).IsEqualTo("2");
        await Assert.That(created.Config["org"]).IsEqualTo("acme");
        await Assert.That(created.CredentialExpirationTimes["TOKEN"]).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(2));
        await Assert.That(created.ProfileWorkspace).IsEqualTo("pw");
        await Assert.That(created.CredentialHandles["TOKEN"].Driver).IsEqualTo("vault");
        await Assert.That(created.CredentialHandles["TOKEN"].Metadata["k"]).IsEqualTo("v");
        await Assert.That(created.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(created.DeletedAt).IsNull();
    }

    [Test]
    public async Task Provider_without_optional_fields_has_safe_defaults()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRequest, Wire.ProviderResponse>(
            "GetProvider", _ => new Wire.ProviderResponse { Provider = new Openshell.Datamodel.V1.Provider { Type = "x" } });
        using var client = ClientTests.NewClient(invoker);

        var provider = await client.Providers.GetAsync("p");

        await Assert.That(provider.Name).IsEqualTo("");
        await Assert.That(provider.Credentials.Count).IsEqualTo(0);
        await Assert.That(provider.CredentialExpirationTimes.Count).IsEqualTo(0);
        await Assert.That(provider.CredentialHandles.Count).IsEqualTo(0);
        await Assert.That(provider.CreatedAt).IsNull();
    }

    [Test]
    public async Task Malformed_gateway_timestamp_is_rejected()
    {
        var wire = ProviderFixtures.WireProvider();
        wire.Metadata.CreatedTime = new Timestamp { Seconds = 1, Nanos = -1 };
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRequest, Wire.ProviderResponse>(
            "GetProvider", _ => new Wire.ProviderResponse { Provider = wire });
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Providers.GetAsync("p")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Internal);
    }

    [Test]
    public async Task ToString_never_prints_credential_values()
    {
        var text = NewProvider().ToString();

        await Assert.That(text).DoesNotContain("s3cret");
        await Assert.That(text).Contains("TOKEN");
        await Assert.That(text).Contains("github-prod");
    }

    [Test]
    public async Task Get_validates_name_and_scopes_request()
    {
        Wire.GetProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRequest, Wire.ProviderResponse>("GetProvider", r =>
        {
            seen = r;
            return new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider() };
        });
        using var client = ClientTests.NewClient(invoker, "ws2");

        await client.Providers.GetAsync("p");

        await Assert.That(seen!.Name).IsEqualTo("p");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws2");
        await Assert.That(async () => await client.Providers.GetAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.CreateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.UpdateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.DeleteAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Providers.EnsureAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task List_pages_and_supports_all_workspaces()
    {
        var requests = new List<Wire.ListProvidersRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListProvidersRequest, Wire.ListProvidersResponse>("ListProviders", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListProvidersResponse();
            reply.Providers.Add(ProviderFixtures.WireProvider(r.PageToken == "" ? "a" : "b"));
            if (r.PageToken == "")
            {
                reply.NextPageToken = "p2";
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Providers.List(new ListOptions { PageSize = 1, AllWorkspaces = true }).ToListAsync();

        await Assert.That(all.Select(p => p.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].WorkspaceScope.SelectionCase)
            .IsEqualTo(Openshell.Datamodel.V1.WorkspaceSelector.SelectionOneofCase.AllWorkspaces);
        await Assert.That(requests[0].PageSize).IsEqualTo(1);
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");

        _ = await client.Providers.List().ToListAsync();
        await Assert.That(requests[^1].WorkspaceScope.Workspace).IsEqualTo("default");
    }

    [Test]
    public async Task Update_returns_receipts_and_sends_expiry_controls()
    {
        Wire.UpdateProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateProviderRequest, Wire.ProviderResponse>("UpdateProvider", r =>
        {
            seen = r;
            var reply = new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider(), MutationId = "mut" };
            reply.TargetReceipts.Add(ProviderFixtures.Receipt(Wire.ProviderMutationKind.Update));
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.UpdateAsync(
            NewProvider(),
            new UpdateProviderOptions
            {
                CredentialExpirationTimes = new Dictionary<string, DateTimeOffset> { ["NEW"] = DateTimeOffset.UnixEpoch },
                ClearCredentialExpirationKeys = ["zeta", "alpha"],
                RequestId = "r",
            });

        await Assert.That(result.MutationId).IsEqualTo("mut");
        await Assert.That(result.TargetReceipts.Single().Kind).IsEqualTo(ProviderMutationKind.Update);
        await Assert.That(result.Provider.Name).IsEqualTo("github-prod");
        await Assert.That(seen!.RequestId).IsEqualTo("r");
        await Assert.That(seen.CredentialExpirationTimes.Keys).IsEquivalentTo(new[] { "NEW" });
        await Assert.That(seen.ClearCredentialExpirationKeys).IsEquivalentTo(new[] { "alpha", "zeta" });
    }

    [Test]
    public async Task Update_defaults_expiry_to_provider_values()
    {
        Wire.UpdateProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateProviderRequest, Wire.ProviderResponse>("UpdateProvider", r =>
        {
            seen = r;
            return new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider() };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Providers.UpdateAsync(NewProvider());

        await Assert.That(seen!.CredentialExpirationTimes.Keys).IsEquivalentTo(new[] { "TOKEN" });
        await Assert.That(seen.ClearCredentialExpirationKeys.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(Wire.DeletionOutcome.Completed, DeletionOutcome.Completed)]
    [Arguments(Wire.DeletionOutcome.AlreadyAbsent, DeletionOutcome.AlreadyAbsent)]
    [Arguments(Wire.DeletionOutcome.Accepted, DeletionOutcome.Accepted)]
    public async Task Delete_returns_typed_outcome(Wire.DeletionOutcome wire, DeletionOutcome expected)
    {
        Wire.DeleteProviderRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteProviderRequest, Wire.DeleteProviderResponse>("DeleteProvider", r =>
        {
            seen = r;
            return new Wire.DeleteProviderResponse { Outcome = wire };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.DeleteAsync("p", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(expected);
        await Assert.That(seen!.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(seen.Name).IsEqualTo("p");
    }

    [Test]
    public async Task Ensure_creates_when_missing()
    {
        var invoker = new FakeCallInvoker()
            .Throws<Wire.GetProviderRequest, Wire.ProviderResponse>("GetProvider", StatusCode.NotFound, "no")
            .Unary<Wire.CreateProviderRequest, Wire.ProviderResponse>(
                "CreateProvider", _ => new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider(id: "new") });
        using var client = ClientTests.NewClient(invoker);

        var provider = await client.Providers.EnsureAsync(NewProvider());

        await Assert.That(provider.Id).IsEqualTo("new");
        await Assert.That(invoker.CountOf("UpdateProvider")).IsEqualTo(0);
    }

    [Test]
    public async Task Ensure_updates_with_existing_identity()
    {
        Wire.UpdateProviderRequest? seen = null;
        var invoker = new FakeCallInvoker()
            .Unary<Wire.GetProviderRequest, Wire.ProviderResponse>(
                "GetProvider", _ => new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider(id: "existing", version: 9) })
            .Unary<Wire.UpdateProviderRequest, Wire.ProviderResponse>("UpdateProvider", r =>
            {
                seen = r;
                return new Wire.ProviderResponse { Provider = ProviderFixtures.WireProvider(id: "existing", version: 10) };
            });
        using var client = ClientTests.NewClient(invoker);

        var provider = await client.Providers.EnsureAsync(NewProvider());

        await Assert.That(seen!.Provider.Metadata.Id).IsEqualTo("existing");
        await Assert.That(seen.Provider.Metadata.ResourceVersion).IsEqualTo(9ul);
        await Assert.That(provider.ResourceVersion).IsEqualTo(10ul);
        await Assert.That(invoker.CountOf("CreateProvider")).IsEqualTo(0);
    }

    [Test]
    public async Task Ensure_propagates_other_lookup_errors()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetProviderRequest, Wire.ProviderResponse>("GetProvider", StatusCode.PermissionDenied, "no");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Providers.EnsureAsync(NewProvider())).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.PermissionDenied);
    }

    // ---- GetCredentials ------------------------------------------------------------------------------------

    private static Wire.GetProviderCredentialsResponse Credentials(params string[] keys)
    {
        var reply = new Wire.GetProviderCredentialsResponse();
        foreach (var key in keys)
        {
            reply.Credentials[key] = new Wire.ProviderCredentialValue
            {
                Value = $"secret-{key}",
                ExpirationTime = key == "WITH_EXPIRY" ? Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddHours(1)) : null,
            };
        }

        return reply;
    }

    [Test]
    public async Task GetCredentials_returns_exactly_the_selected_keys()
    {
        Wire.GetProviderCredentialsRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderCredentialsRequest, Wire.GetProviderCredentialsResponse>("GetProviderCredentials", r =>
        {
            seen = r;
            return Credentials("A", "WITH_EXPIRY");
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.GetCredentialsAsync(
            "gh",
            ["A", "WITH_EXPIRY"],
            new ProviderCredentialsOptions { Workspace = "ops", MinimumRemainingLifetime = TimeSpan.FromMinutes(10) });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ops");
        await Assert.That(seen.Name).IsEqualTo("gh");
        await Assert.That(seen.CredentialKeys).IsEquivalentTo(new[] { "A", "WITH_EXPIRY" });
        await Assert.That(seen.MinimumRemainingLifetime.Seconds).IsEqualTo(600L);
        await Assert.That(result["A"].Value).IsEqualTo("secret-A");
        await Assert.That(result["A"].ExpirationTime).IsNull();
        await Assert.That(result["WITH_EXPIRY"].ExpirationTime).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(1));
    }

    [Test]
    public async Task GetCredentials_omits_lifetime_when_not_requested()
    {
        Wire.GetProviderCredentialsRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderCredentialsRequest, Wire.GetProviderCredentialsResponse>("GetProviderCredentials", r =>
        {
            seen = r;
            return Credentials("A");
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Providers.GetCredentialsAsync("gh", ["A"], new ProviderCredentialsOptions { Workspace = "ops" });

        await Assert.That(seen!.MinimumRemainingLifetime).IsNull();
    }

    [Test]
    public async Task Credential_value_ToString_redacts_the_secret()
    {
        var text = new ProviderCredentialValue("hunter2", DateTimeOffset.UnixEpoch).ToString();
        var noExpiry = new ProviderCredentialValue("hunter2").ToString();

        await Assert.That(text).DoesNotContain("hunter2");
        await Assert.That(text).Contains("redacted");
        await Assert.That(noExpiry).DoesNotContain("hunter2");
        await Assert.That(noExpiry).Contains("none");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ops")]
    public async Task GetCredentials_requires_canonical_workspace(string workspace)
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        var ex = await Assert.That(async () => await client.Providers.GetCredentialsAsync(
            "gh", ["A"], new ProviderCredentialsOptions { Workspace = workspace })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    [Arguments("")]
    [Arguments(" gh")]
    public async Task GetCredentials_requires_canonical_name(string name)
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Providers.GetCredentialsAsync(
            name, ["A"], new ProviderCredentialsOptions { Workspace = "w" })).Throws<OpenShellException>();
    }

    [Test]
    public async Task GetCredentials_rejects_overlong_name()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Providers.GetCredentialsAsync(
            new string('x', 254), ["A"], new ProviderCredentialsOptions { Workspace = "w" })).Throws<OpenShellException>();
    }

    [Test]
    public async Task GetCredentials_rejects_bad_key_selections()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var options = new ProviderCredentialsOptions { Workspace = "w" };
        var tooMany = Enumerable.Range(0, 33).Select(i => $"K{i}").ToArray();

        foreach (var keys in new[] { Array.Empty<string>(), ["1BAD"], ["a-b"], ["A", "A"], [new string('K', 257)], tooMany, [null!] })
        {
            var ex = await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", keys, options)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        }
    }

    [Test]
    public async Task GetCredentials_rejects_out_of_range_lifetime()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        foreach (var lifetime in new[] { TimeSpan.FromSeconds(-1), TimeSpan.FromHours(24) + TimeSpan.FromTicks(1) })
        {
            await Assert.That(async () => await client.Providers.GetCredentialsAsync(
                "gh", ["A"], new ProviderCredentialsOptions { Workspace = "w", MinimumRemainingLifetime = lifetime })).Throws<OpenShellException>();
        }

        await Assert.That(async () => await client.Providers.GetCredentialsAsync("gh", ["A"], null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Providers.GetCredentialsAsync(
            "gh", null!, new ProviderCredentialsOptions { Workspace = "w" })).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task GetCredentials_accepts_the_24_hour_boundary()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderCredentialsRequest, Wire.GetProviderCredentialsResponse>(
            "GetProviderCredentials", _ => Credentials("A"));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.GetCredentialsAsync(
            "gh", ["A"], new ProviderCredentialsOptions { Workspace = "w", MinimumRemainingLifetime = TimeSpan.FromHours(24) });

        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetCredentials_rejects_unexpected_selection()
    {
        foreach (var returned in new[] { new[] { "A" }, ["A", "B", "C"], ["A", "X"] })
        {
            var invoker = new FakeCallInvoker().Unary<Wire.GetProviderCredentialsRequest, Wire.GetProviderCredentialsResponse>(
                "GetProviderCredentials", _ => Credentials(returned));
            using var client = ClientTests.NewClient(invoker);

            var ex = await Assert.That(async () => await client.Providers.GetCredentialsAsync(
                "gh", ["A", "B"], new ProviderCredentialsOptions { Workspace = "w" })).Throws<OpenShellException>();

            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Internal);
        }
    }

    [Test]
    public async Task GetCredentials_copies_keys_before_the_call()
    {
        var keys = new List<string> { "A" };
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderCredentialsRequest, Wire.GetProviderCredentialsResponse>("GetProviderCredentials", _ =>
        {
            keys.Add("MUTATED");
            return Credentials("A");
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.GetCredentialsAsync("gh", keys, new ProviderCredentialsOptions { Workspace = "w" });

        await Assert.That(result.Keys).IsEquivalentTo(new[] { "A" });
    }
}
