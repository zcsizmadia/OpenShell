// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ProviderProfileTests
{
    private static Wire.ProviderProfile WireProfile(string id = "my-profile", ulong version = 4)
    {
        var profile = new Wire.ProviderProfile
        {
            Id = id,
            DisplayName = "My Profile",
            Description = "d",
            Category = Wire.ProviderProfileCategory.SourceControl,
            InferenceCapable = true,
            Discovery = new Wire.ProviderProfileDiscovery { Credentials = { "TOKEN" } },
            ResourceVersion = version,
            Source = "user",
            Scope = "workspace",
            Annotations = { ["k"] = "v" },
            Files = { new Wire.ProviderProfileFile { Path = "cfg", Content = "{{config.X}}", EnvVar = "CFG" } },
        };
        profile.Endpoints.Add(new Openshell.Sandbox.V1.NetworkEndpoint { Host = "api.example.com", Port = 443 });
        profile.Binaries.Add(new Openshell.Sandbox.V1.NetworkBinary { Path = "/usr/bin/git" });
        var credential = new Wire.ProviderProfileCredential
        {
            Name = "TOKEN",
            Description = "token",
            EnvVars = { "GH_TOKEN" },
            Required = true,
            AuthStyle = "header",
            HeaderName = "Authorization",
            QueryParam = "q",
            PathTemplate = "/{x}",
            Refresh = new Wire.ProviderCredentialRefresh
            {
                Strategy = Wire.ProviderCredentialRefreshStrategy.Oauth2RefreshToken,
                TokenUrl = "https://idp/token",
                Scopes = { "read" },
                RefreshBefore = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)),
                MaxLifetime = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
                Material = { new Wire.ProviderCredentialRefreshMaterial { Name = "client_secret", Description = "m", Required = true, Secret = true } },
                AdditionalOutputs = { new Wire.ProviderCredentialRefreshOutput { Output = "session_token", Credential = "SESSION" } },
            },
            TokenGrant = new Wire.ProviderCredentialTokenGrant
            {
                TokenEndpoint = "https://idp/token",
                Audience = "aud",
                JwtSvidAudience = "svid",
                Scopes = { "s" },
                CacheTtl = Duration.FromTimeSpan(TimeSpan.FromSeconds(30)),
                AudienceOverrides =
                {
                    new Wire.ProviderCredentialTokenGrantAudienceOverride { Host = "h", Port = 8443, Path = "/p", Audience = "a2", Scopes = { "s2" } },
                },
                ClientAssertionType = "cat",
                GrantType = Wire.ProviderCredentialTokenGrantType.TokenExchange,
                SubjectToken = new Wire.ProviderCredentialTokenGrantSubjectToken { Source = "provider_credential", Credential = "OTHER", SubjectTokenType = "st" },
                RequestedTokenType = "rtt",
            },
            TokenGrantOwners = { "api.example.com:443" },
        };
        profile.Credentials.Add(credential);
        profile.Credentials.Add(new Wire.ProviderProfileCredential { Name = "plain" });
        return profile;
    }

    [Test]
    public async Task Get_maps_the_full_profile()
    {
        Wire.GetProviderProfileRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderProfileRequest, Wire.ProviderProfileResponse>("GetProviderProfile", r =>
        {
            seen = r;
            return new Wire.ProviderProfileResponse { Profile = WireProfile() };
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var profile = await client.Providers.Profiles.GetAsync("my-profile");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Id).IsEqualTo("my-profile");
        await Assert.That(profile.DisplayName).IsEqualTo("My Profile");
        await Assert.That(profile.Category).IsEqualTo(ProviderProfileCategory.SourceControl);
        await Assert.That(profile.InferenceCapable).IsTrue();
        await Assert.That(profile.DiscoveryCredentials.Single()).IsEqualTo("TOKEN");
        await Assert.That(profile.ResourceVersion).IsEqualTo(4ul);
        await Assert.That(profile.Source).IsEqualTo("user");
        await Assert.That(profile.Scope).IsEqualTo("workspace");
        await Assert.That(profile.Annotations["k"]).IsEqualTo("v");
        await Assert.That(profile.Files.Single()).IsEqualTo(new ProfileFile("cfg", "{{config.X}}", "CFG"));
        await Assert.That(profile.Endpoints.Single().Host).IsEqualTo("api.example.com");
        await Assert.That(profile.Binaries.Single().Path).IsEqualTo("/usr/bin/git");

        var cred = profile.Credentials[0];
        await Assert.That(cred.EnvVars.Single()).IsEqualTo("GH_TOKEN");
        await Assert.That(cred.Required).IsTrue();
        await Assert.That(cred.AuthStyle).IsEqualTo("header");
        await Assert.That(cred.Refresh!.Strategy).IsEqualTo(RefreshStrategy.OAuth2RefreshToken);
        await Assert.That(cred.Refresh.RefreshBefore).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(cred.Refresh.MaxLifetime).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(cred.Refresh.Material.Single().Secret).IsTrue();
        await Assert.That(cred.Refresh.AdditionalOutputs.Single().Credential).IsEqualTo("SESSION");
        var grant = cred.TokenGrant!;
        await Assert.That(grant.GrantType).IsEqualTo(CredentialTokenGrantType.TokenExchange);
        await Assert.That(grant.CacheTtl).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(grant.AudienceOverrides.Single().Port).IsEqualTo(8443u);
        await Assert.That(grant.SubjectToken!.SubjectTokenType).IsEqualTo("st");
        await Assert.That(grant.Owners.Single()).IsEqualTo("api.example.com:443");

        var plain = profile.Credentials[1];
        await Assert.That(plain.Refresh).IsNull();
        await Assert.That(plain.TokenGrant).IsNull();
    }

    [Test]
    public async Task Round_trip_through_import_preserves_fields_and_drops_output_only_owners()
    {
        Wire.ImportProviderProfilesRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ImportProviderProfilesRequest, Wire.ImportProviderProfilesResponse>("ImportProviderProfiles", r =>
        {
            seen = r;
            var reply = new Wire.ImportProviderProfilesResponse { Imported = true };
            reply.Profiles.Add(WireProfile());
            reply.Diagnostics.Add(new Wire.ProviderProfileDiagnostic { Source = "s", ProfileId = "p", Field = "f", Message = "m", Severity = "warning" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);
        var domain = ProfileMapping.ToDomain(WireProfile());

        var result = await client.Providers.Profiles.ImportAsync([new ProfileImportItem(domain, "file.yaml")], new ProfileMutationOptions { RequestId = "r" });

        await Assert.That(result.Imported).IsTrue();
        await Assert.That(result.Profiles.Single().Id).IsEqualTo("my-profile");
        await Assert.That(result.Diagnostics.Single()).IsEqualTo(new ProfileDiagnostic("s", "p", "f", "m", "warning"));
        await Assert.That(seen!.RequestId).IsEqualTo("r");
        var item = seen.Profiles.Single();
        await Assert.That(item.Source).IsEqualTo("file.yaml");
        var expected = WireProfile();
        expected.Credentials[0].TokenGrantOwners.Clear();
        await Assert.That(item.Profile.Equals(expected)).IsTrue();
    }

    [Test]
    public async Task Profile_without_discovery_or_optional_blocks_round_trips()
    {
        var wire = new Wire.ProviderProfile { Id = "bare" };
        var domain = ProfileMapping.ToDomain(wire);
        var back = ProfileMapping.ToWire(domain);

        await Assert.That(domain.DiscoveryCredentials.Count).IsEqualTo(0);
        await Assert.That(back.Discovery).IsNull();
        await Assert.That(back.Id).IsEqualTo("bare");
    }

    [Test]
    public async Task Refresh_and_grant_without_durations_stay_absent()
    {
        var wire = new Wire.ProviderProfile { Id = "x" };
        wire.Credentials.Add(new Wire.ProviderProfileCredential
        {
            Name = "c",
            Refresh = new Wire.ProviderCredentialRefresh { Strategy = Wire.ProviderCredentialRefreshStrategy.Static },
            TokenGrant = new Wire.ProviderCredentialTokenGrant { TokenEndpoint = "e" },
        });

        var domain = ProfileMapping.ToDomain(wire);
        await Assert.That(domain.Credentials[0].Refresh!.RefreshBefore).IsNull();
        await Assert.That(domain.Credentials[0].Refresh!.MaxLifetime).IsNull();
        await Assert.That(domain.Credentials[0].TokenGrant!.CacheTtl).IsNull();
        await Assert.That(domain.Credentials[0].TokenGrant!.SubjectToken).IsNull();

        var back = ProfileMapping.ToWire(domain);
        await Assert.That(back.Credentials[0].Refresh.RefreshBefore).IsNull();
        await Assert.That(back.Credentials[0].Refresh.MaxLifetime).IsNull();
        await Assert.That(back.Credentials[0].TokenGrant.CacheTtl).IsNull();
        await Assert.That(back.Credentials[0].TokenGrant.SubjectToken).IsNull();
    }

    [Test]
    public async Task List_pages_and_platform_view_omits_workspace_scope()
    {
        var requests = new List<Wire.ListProviderProfilesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListProviderProfilesRequest, Wire.ListProviderProfilesResponse>("ListProviderProfiles", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListProviderProfilesResponse();
            reply.Profiles.Add(WireProfile(r.PageToken == "" ? "a" : "b"));
            if (r.PageToken == "")
            {
                reply.NextPageToken = "p2";
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var all = await client.Providers.Profiles.List(new ListOptions { PageSize = 5 }).ToListAsync();
        var platform = client.Providers.Profiles.Platform;
        _ = await platform.List().ToListAsync();

        await Assert.That(all.Select(p => p.Id)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(requests[0].PageSize).IsEqualTo(5);
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
        await Assert.That(requests[2].WorkspaceScope).IsNull();
        await Assert.That(ReferenceEquals(platform.Platform, platform)).IsTrue();
    }

    [Test]
    public async Task Platform_scope_applies_to_get_import_update_lint_and_delete()
    {
        var scopes = new List<Openshell.Datamodel.V1.WorkspaceSelector?>();
        var invoker = new FakeCallInvoker()
            .Unary<Wire.GetProviderProfileRequest, Wire.ProviderProfileResponse>("GetProviderProfile", r =>
            {
                scopes.Add(r.WorkspaceScope);
                return new Wire.ProviderProfileResponse { Profile = WireProfile() };
            })
            .Unary<Wire.ImportProviderProfilesRequest, Wire.ImportProviderProfilesResponse>("ImportProviderProfiles", r =>
            {
                scopes.Add(r.WorkspaceScope);
                return new Wire.ImportProviderProfilesResponse();
            })
            .Unary<Wire.UpdateProviderProfilesRequest, Wire.UpdateProviderProfilesResponse>("UpdateProviderProfiles", r =>
            {
                scopes.Add(r.WorkspaceScope);
                return new Wire.UpdateProviderProfilesResponse();
            })
            .Unary<Wire.LintProviderProfilesRequest, Wire.LintProviderProfilesResponse>("LintProviderProfiles", r =>
            {
                scopes.Add(r.WorkspaceScope);
                return new Wire.LintProviderProfilesResponse();
            })
            .Unary<Wire.DeleteProviderProfileRequest, Wire.DeleteProviderProfileResponse>("DeleteProviderProfile", r =>
            {
                scopes.Add(r.WorkspaceScope);
                return new Wire.DeleteProviderProfileResponse();
            });
        using var client = ClientTests.NewClient(invoker);
        var platform = client.Providers.Profiles.Platform;
        var item = new ProfileImportItem(new ProviderProfile { Id = "p" });

        await platform.GetAsync("p");
        await platform.ImportAsync([item]);
        await platform.UpdateAsync("p", 1, item);
        await platform.LintAsync([item]);
        await platform.DeleteAsync("p");

        await Assert.That(scopes.Count).IsEqualTo(5);
        await Assert.That(scopes.All(s => s is null)).IsTrue();
    }

    [Test]
    public async Task Update_sends_id_version_and_maps_result()
    {
        Wire.UpdateProviderProfilesRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateProviderProfilesRequest, Wire.UpdateProviderProfilesResponse>("UpdateProviderProfiles", r =>
        {
            seen = r;
            var reply = new Wire.UpdateProviderProfilesResponse { Updated = true, Profile = WireProfile(version: 5) };
            reply.Diagnostics.Add(new Wire.ProviderProfileDiagnostic { Message = "ok" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.Profiles.UpdateAsync(
            "my-profile", 4, new ProfileImportItem(new ProviderProfile { Id = "my-profile" }, "src"), new ProfileMutationOptions { RequestId = "r" });

        await Assert.That(seen!.Id).IsEqualTo("my-profile");
        await Assert.That(seen.ExpectedResourceVersion).IsEqualTo(4ul);
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(seen.Profile.Source).IsEqualTo("src");
        await Assert.That(result.Updated).IsTrue();
        await Assert.That(result.Profile!.ResourceVersion).IsEqualTo(5ul);
        await Assert.That(result.Diagnostics.Single().Message).IsEqualTo("ok");
    }

    [Test]
    public async Task Update_without_profile_in_response_maps_null()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateProviderProfilesRequest, Wire.UpdateProviderProfilesResponse>(
            "UpdateProviderProfiles", _ => new Wire.UpdateProviderProfilesResponse());
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.Profiles.UpdateAsync("p", 1, new ProfileImportItem(new ProviderProfile { Id = "p" }));

        await Assert.That(result.Profile).IsNull();
        await Assert.That(result.Updated).IsFalse();
    }

    [Test]
    public async Task Lint_maps_validity_and_diagnostics()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.LintProviderProfilesRequest, Wire.LintProviderProfilesResponse>("LintProviderProfiles", r =>
        {
            var reply = new Wire.LintProviderProfilesResponse { Valid = r.Profiles.Count == 1 };
            reply.Diagnostics.Add(new Wire.ProviderProfileDiagnostic { Severity = "error" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.Profiles.LintAsync([new ProfileImportItem(new ProviderProfile { Id = "p" })]);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Diagnostics.Single().Severity).IsEqualTo("error");
    }

    [Test]
    public async Task Delete_returns_typed_outcome_and_forwards_options()
    {
        Wire.DeleteProviderProfileRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteProviderProfileRequest, Wire.DeleteProviderProfileResponse>("DeleteProviderProfile", r =>
        {
            seen = r;
            return new Wire.DeleteProviderProfileResponse { Outcome = Wire.DeletionOutcome.AlreadyAbsent };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.Profiles.DeleteAsync("p", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(seen!.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var profiles = client.Providers.Profiles;
        var item = new ProfileImportItem(new ProviderProfile { Id = "p" });

        await Assert.That(async () => await profiles.GetAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await profiles.ImportAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await profiles.UpdateAsync("", 1, item)).Throws<ArgumentException>();
        await Assert.That(async () => await profiles.UpdateAsync("p", 1, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await profiles.LintAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await profiles.DeleteAsync("")).Throws<ArgumentException>();
    }
}
