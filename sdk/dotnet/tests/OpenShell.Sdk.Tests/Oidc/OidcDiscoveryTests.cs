// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class OidcUrlTests
{
    private static readonly ConformanceFixture Fixture = ConformanceFixture.Load();

    [Test]
    public async Task Conformance_allowed_urls_are_accepted()
    {
        foreach (var url in Fixture.Strings("urls", "allowed"))
        {
            OidcUrl.Validate("issuer", url);
        }

        await Assert.That(Fixture.Strings("urls", "allowed").Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Conformance_rejected_urls_are_refused()
    {
        foreach (var url in Fixture.Strings("urls", "rejected"))
        {
            var ex = await Assert.That(() => OidcUrl.Validate("issuer", url)).Throws<OidcException>();
            await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Discovery);
        }
    }

    [Test]
    [Arguments("not a url")]
    [Arguments("/relative")]
    [Arguments("ftp://issuer.example.com")]
    [Arguments("https://issuer.example.com/#")]
    public async Task Malformed_or_unsupported_urls_are_refused(string url)
    {
        await Assert.That(() => OidcUrl.Validate("token_endpoint", url)).Throws<OidcException>();
    }

    [Test]
    public async Task Conformance_leeway_matches_provider_leeway()
    {
        // The provider renews 30 s before expiry; the fixture pins that value for every SDK.
        await Assert.That(Fixture.Int("expiry", "leeway_seconds")).IsEqualTo(30);
    }
}

public class OidcDiscoveryTests
{
    private const string Issuer = "https://issuer.example.com";
    private static readonly ConformanceFixture Fixture = ConformanceFixture.Load();

    private static OidcDiscovery Discovery(StubHandler handler, TimeProvider? time = null) =>
        new(new HttpClient(handler), time);

    [Test]
    public async Task Discovers_provider_metadata()
    {
        var handler = StubHandler.Provider(Issuer, deviceEndpoint: true);

        var metadata = await Discovery(handler).DiscoverAsync(Issuer);

        await Assert.That(metadata.TokenEndpoint).IsEqualTo(Issuer + "/token");
        await Assert.That(metadata.AuthorizationEndpoint).IsEqualTo(Issuer + "/authorize");
        await Assert.That(metadata.DeviceAuthorizationEndpoint).IsEqualTo(Issuer + "/device");
        await Assert.That(metadata.SupportsS256).IsTrue();
        await Assert.That(handler.Requests.Single().Url.AbsoluteUri).IsEqualTo(Issuer + "/.well-known/openid-configuration");
    }

    [Test]
    public async Task Without_pkce_or_device_endpoint_those_are_absent()
    {
        var metadata = await Discovery(StubHandler.Provider(Issuer, pkce: false)).DiscoverAsync(Issuer);

        await Assert.That(metadata.SupportsS256).IsFalse();
        await Assert.That(metadata.DeviceAuthorizationEndpoint).IsNull();
    }

    [Test]
    public async Task Trailing_slash_issuer_matches_configured_issuer()
    {
        var matching = Fixture.Str("discovery", "matching_issuer");
        var configured = Fixture.Str("discovery", "configured_issuer");

        var metadata = await Discovery(StubHandler.Provider(matching)).DiscoverAsync(configured);

        await Assert.That(metadata.TokenEndpoint).IsEqualTo(matching.TrimEnd('/') + "/token");
    }

    [Test]
    public async Task Mismatched_issuer_is_rejected()
    {
        var handler = StubHandler.Provider(Fixture.Str("discovery", "mismatched_issuer"));

        var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Fixture.Str("discovery", "configured_issuer")))
            .Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Discovery);
        await Assert.That(ex.Message).Contains("does not match");
    }

    [Test]
    public async Task Redirect_statuses_are_refused()
    {
        foreach (var status in Fixture.Ints("discovery", "redirect_statuses"))
        {
            var handler = new StubHandler
            {
                Respond = _ =>
                {
                    var response = new HttpResponseMessage((HttpStatusCode)status);
                    response.Headers.Location = new Uri("https://attacker.example.com/");
                    return response;
                },
            };

            var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();
            await Assert.That(ex!.Message).Contains($"HTTP {status}");
            await Assert.That(handler.Requests.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Oversized_response_is_rejected()
    {
        var handler = new StubHandler
        {
            Respond = _ => StubHandler.Json(new string('x', Fixture.Int("limits", "max_response_bytes") + 1)),
        };

        var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("too large");
    }

    [Test]
    public async Task Response_at_the_limit_is_read_and_then_fails_as_invalid_json()
    {
        var handler = new StubHandler { Respond = _ => StubHandler.Json(new string('x', Fixture.Int("limits", "max_response_bytes"))) };

        var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("invalid discovery JSON");
    }

    [Test]
    public async Task Invalid_json_and_non_200_are_rejected()
    {
        var bad = new StubHandler { Respond = _ => StubHandler.Json("{not json") };
        await Assert.That(async () => await Discovery(bad).DiscoverAsync(Issuer)).Throws<OidcException>();

        var notFound = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var ex = await Assert.That(async () => await Discovery(notFound).DiscoverAsync(Issuer)).Throws<OidcException>();
        await Assert.That(ex!.Message).Contains("HTTP 500");
    }

    [Test]
    public async Task Json_null_document_is_rejected()
    {
        var handler = new StubHandler { Respond = _ => StubHandler.Json("null") };

        await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();
    }

    [Test]
    public async Task Missing_endpoints_are_rejected()
    {
        var noToken = new StubHandler { Respond = _ => StubHandler.Json($$"""{"issuer":"{{Issuer}}","authorization_endpoint":"{{Issuer}}/a"}""") };
        var ex1 = await Assert.That(async () => await Discovery(noToken).DiscoverAsync(Issuer)).Throws<OidcException>();
        await Assert.That(ex1!.Message).Contains("token_endpoint");

        var noAuth = new StubHandler { Respond = _ => StubHandler.Json($$"""{"issuer":"{{Issuer}}","token_endpoint":"{{Issuer}}/t"}""") };
        var ex2 = await Assert.That(async () => await Discovery(noAuth).DiscoverAsync(Issuer)).Throws<OidcException>();
        await Assert.That(ex2!.Message).Contains("authorization_endpoint");
    }

    [Test]
    [Arguments("token_endpoint", "http://issuer.example.com/token")]
    [Arguments("authorization_endpoint", "http://issuer.example.com/authorize")]
    [Arguments("device_authorization_endpoint", "http://issuer.example.com/device")]
    [Arguments("token_endpoint", "https://user@issuer.example.com/token")]
    public async Task Insecure_discovered_endpoints_are_rejected(string field, string value)
    {
        var doc = new Dictionary<string, string>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "/authorize",
            ["token_endpoint"] = Issuer + "/token",
            ["device_authorization_endpoint"] = Issuer + "/device",
        };
        doc[field] = value;
        var json = "{" + string.Join(",", doc.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        var handler = new StubHandler { Respond = _ => StubHandler.Json(json) };

        var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Discovery);
    }

    [Test]
    public async Task Insecure_configured_issuer_is_rejected_before_any_request()
    {
        var handler = StubHandler.Provider("http://issuer.example.com");

        await Assert.That(async () => await Discovery(handler).DiscoverAsync("http://issuer.example.com")).Throws<OidcException>();

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Results_are_cached_for_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer);
        var discovery = Discovery(handler, time);

        await discovery.DiscoverAsync(Issuer);
        await discovery.DiscoverAsync(Issuer + "/");
        time.Advance(TimeSpan.FromMinutes(9));
        await discovery.DiscoverAsync(Issuer);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);

        time.Advance(TimeSpan.FromMinutes(2));
        await discovery.DiscoverAsync(Issuer);
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Transport_failure_is_reported_as_discovery_failure()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused") };

        var ex = await Assert.That(async () => await Discovery(handler).DiscoverAsync(Issuer)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Discovery);
        await Assert.That(ex.InnerException).IsTypeOf<HttpRequestException>();
    }

    [Test]
    public async Task Client_side_timeout_is_a_discovery_failure_but_caller_cancellation_is_not_wrapped()
    {
        var timeoutHandler = new StubHandler { Respond = _ => throw new TaskCanceledException("timed out") };
        var ex = await Assert.That(async () => await Discovery(timeoutHandler).DiscoverAsync(Issuer)).Throws<OidcException>();
        await Assert.That(ex!.Message).Contains("timed out");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelHandler = StubHandler.Provider(Issuer);
        await Assert.That(async () => await Discovery(cancelHandler).DiscoverAsync(Issuer, cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Shared_instance_uses_the_default_client()
    {
        await Assert.That(OidcDiscovery.Shared).IsNotNull();
        await Assert.That(OidcHttp.Shared.Timeout).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
