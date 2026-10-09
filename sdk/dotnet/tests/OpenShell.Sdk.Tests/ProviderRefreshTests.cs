// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ProviderRefreshTests
{
    private static Wire.ProviderCredentialRefreshStatus WireStatus(string key = "TOKEN") => new()
    {
        Provider = "gh",
        ProviderId = "pid",
        CredentialKey = key,
        Strategy = Wire.ProviderCredentialRefreshStrategy.Oauth2ClientCredentials,
        Status = "healthy",
        ExpirationTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddHours(1)),
        NextRefreshTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMinutes(30)),
        LastRefreshTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMinutes(1)),
        LastError = "boom",
        RecoveryAction = Wire.ProviderCredentialRefreshRecoveryAction.Reauthorize,
        FailureCode = "oauth_invalid_grant",
        ProviderErrorSubtype = "expired",
        LastErrorTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMinutes(2)),
    };

    [Test]
    public async Task GetStatus_maps_every_field()
    {
        Wire.GetProviderRefreshStatusRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRefreshStatusRequest, Wire.GetProviderRefreshStatusResponse>("GetProviderRefreshStatus", r =>
        {
            seen = r;
            var reply = new Wire.GetProviderRefreshStatusResponse();
            reply.Credentials.Add(WireStatus());
            reply.Credentials.Add(new Wire.ProviderCredentialRefreshStatus { CredentialKey = "BARE" });
            return reply;
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var statuses = await client.Providers.Refresh.GetStatusAsync("gh", "TOKEN");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Provider).IsEqualTo("gh");
        await Assert.That(seen.CredentialKey).IsEqualTo("TOKEN");
        var s = statuses[0];
        await Assert.That(s.Provider).IsEqualTo("gh");
        await Assert.That(s.ProviderId).IsEqualTo("pid");
        await Assert.That(s.Strategy).IsEqualTo(RefreshStrategy.OAuth2ClientCredentials);
        await Assert.That(s.Status).IsEqualTo("healthy");
        await Assert.That(s.ExpiresAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(1));
        await Assert.That(s.NextRefreshAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddMinutes(30));
        await Assert.That(s.LastRefreshAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddMinutes(1));
        await Assert.That(s.LastError).IsEqualTo("boom");
        await Assert.That(s.RecoveryAction).IsEqualTo(RefreshRecoveryAction.Reauthorize);
        await Assert.That(s.FailureCode).IsEqualTo("oauth_invalid_grant");
        await Assert.That(s.ProviderErrorSubtype).IsEqualTo("expired");
        await Assert.That(s.LastErrorAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddMinutes(2));
        await Assert.That(statuses[1].NextRefreshAt).IsNull();
        await Assert.That(statuses[1].ExpiresAt).IsNull();
    }

    [Test]
    public async Task GetStatus_without_key_asks_for_all_credentials()
    {
        Wire.GetProviderRefreshStatusRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetProviderRefreshStatusRequest, Wire.GetProviderRefreshStatusResponse>("GetProviderRefreshStatus", r =>
        {
            seen = r;
            return new Wire.GetProviderRefreshStatusResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        var statuses = await client.Providers.Refresh.GetStatusAsync("gh");

        await Assert.That(seen!.CredentialKey).IsEqualTo("");
        await Assert.That(statuses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Configure_sends_material_secrets_and_expiry()
    {
        Wire.ConfigureProviderRefreshRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ConfigureProviderRefreshRequest, Wire.ConfigureProviderRefreshResponse>("ConfigureProviderRefresh", r =>
        {
            seen = r;
            return new Wire.ConfigureProviderRefreshResponse { Status = WireStatus() };
        });
        using var client = ClientTests.NewClient(invoker);

        var status = await client.Providers.Refresh.ConfigureAsync(new RefreshConfig
        {
            Provider = "gh",
            CredentialKey = "TOKEN",
            Strategy = RefreshStrategy.OAuth2RefreshToken,
            Material = new Dictionary<string, string> { ["client_secret"] = "shh", ["client_id"] = "id" },
            SecretMaterialKeys = ["client_id"],
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(2),
            RequestId = "r",
        });

        await Assert.That(seen!.Provider).IsEqualTo("gh");
        await Assert.That(seen.CredentialKey).IsEqualTo("TOKEN");
        await Assert.That(seen.Strategy).IsEqualTo(Wire.ProviderCredentialRefreshStrategy.Oauth2RefreshToken);
        await Assert.That(seen.Material["client_secret"]).IsEqualTo("shh");
        await Assert.That(seen.SecretMaterialKeys).IsEquivalentTo(new[] { "client_id" });
        await Assert.That(seen.ExpirationTime.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(2));
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(status.CredentialKey).IsEqualTo("TOKEN");
    }

    [Test]
    public async Task Configure_with_minimal_config_omits_optional_fields()
    {
        Wire.ConfigureProviderRefreshRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ConfigureProviderRefreshRequest, Wire.ConfigureProviderRefreshResponse>("ConfigureProviderRefresh", r =>
        {
            seen = r;
            return new Wire.ConfigureProviderRefreshResponse { Status = WireStatus() };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Providers.Refresh.ConfigureAsync(new RefreshConfig { Provider = "gh", CredentialKey = "K" });

        await Assert.That(seen!.ExpirationTime).IsNull();
        await Assert.That(seen.SecretMaterialKeys.Count).IsEqualTo(0);
        await Assert.That(seen.RequestId).IsEqualTo("");
    }

    [Test]
    public async Task RefreshConfig_ToString_never_prints_material_values()
    {
        var config = new RefreshConfig
        {
            Provider = "gh",
            CredentialKey = "TOKEN",
            Material = new Dictionary<string, string> { ["client_secret"] = "hunter2" },
        };

        var text = config.ToString();

        await Assert.That(text).DoesNotContain("hunter2");
        await Assert.That(text).Contains("client_secret");
    }

    [Test]
    public async Task Rotate_forwards_request_id()
    {
        Wire.RotateProviderCredentialRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RotateProviderCredentialRequest, Wire.RotateProviderCredentialResponse>("RotateProviderCredential", r =>
        {
            seen = r;
            return new Wire.RotateProviderCredentialResponse { Status = WireStatus() };
        });
        using var client = ClientTests.NewClient(invoker);

        var status = await client.Providers.Refresh.RotateAsync("gh", "TOKEN", "r");

        await Assert.That(seen!.RequestId).IsEqualTo("r");
        await Assert.That(seen.CredentialKey).IsEqualTo("TOKEN");
        await Assert.That(status.Status).IsEqualTo("healthy");

        await client.Providers.Refresh.RotateAsync("gh", "TOKEN");
        await Assert.That(seen.RequestId).IsEqualTo("");
    }

    [Test]
    public async Task Delete_returns_typed_outcome()
    {
        Wire.DeleteProviderRefreshRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteProviderRefreshRequest, Wire.DeleteProviderRefreshResponse>("DeleteProviderRefresh", r =>
        {
            seen = r;
            return new Wire.DeleteProviderRefreshResponse { Outcome = Wire.DeletionOutcome.Completed };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Providers.Refresh.DeleteAsync("gh", "TOKEN", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(seen!.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var refresh = client.Providers.Refresh;

        await Assert.That(async () => await refresh.GetStatusAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await refresh.ConfigureAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await refresh.RotateAsync("", "k")).Throws<ArgumentException>();
        await Assert.That(async () => await refresh.RotateAsync("p", "")).Throws<ArgumentException>();
        await Assert.That(async () => await refresh.DeleteAsync("", "k")).Throws<ArgumentException>();
        await Assert.That(async () => await refresh.DeleteAsync("p", "")).Throws<ArgumentException>();
    }
}
