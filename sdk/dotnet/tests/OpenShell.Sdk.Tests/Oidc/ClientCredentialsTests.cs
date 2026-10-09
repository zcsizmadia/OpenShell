// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class ClientCredentialsTests
{
    private const string Issuer = "https://issuer.example.com";
    private static readonly ConformanceFixture Fixture = ConformanceFixture.Load();

    private static OidcOptions Options(StubHandler handler, FakeTimeProvider? time = null) => new()
    {
        Issuer = Issuer,
        ClientId = Fixture.Str("request", "client_id"),
        ClientSecret = Fixture.Str("request", "client_secret"),
        HttpClient = new HttpClient(handler),
        TimeProvider = time,
    };

    private static StubHandler TokenServer(Func<RecordedRequest, HttpResponseMessage> token) => StubHandler.Provider(Issuer, token);

    private static HttpResponseMessage Ok(string token = "access-1", int expiresIn = 120) =>
        StubHandler.Json($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":{{expiresIn}}}""");

    [Test]
    public async Task Request_matches_the_conformance_fixture()
    {
        var handler = TokenServer(_ => Ok());
        var options = Options(handler) with
        {
            Scopes = Fixture.Strings("request", "scopes"),
            Audience = Fixture.Str("request", "audience"),
        };

        await OidcLogin.ClientCredentialsAsync(options);

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["grant_type"]).IsEqualTo(Fixture.Str("request", "grant_type"));
        await Assert.That(form["client_id"]).IsEqualTo(Fixture.Str("request", "client_id"));
        await Assert.That(form["client_secret"]).IsEqualTo(Fixture.Str("request", "client_secret"));
        await Assert.That(form["scope"]).IsEqualTo(Fixture.Str("request", "scope"));
        await Assert.That(form["audience"]).IsEqualTo(Fixture.Str("request", "audience"));
        foreach (var forbidden in Fixture.Strings("request", "forbidden_implicit_scopes"))
        {
            await Assert.That(form["scope"].Split(' ')).DoesNotContain(forbidden);
        }
    }

    [Test]
    public async Task Without_explicit_scopes_no_scope_or_audience_is_sent()
    {
        var handler = TokenServer(_ => Ok());

        await OidcLogin.ClientCredentialsAsync(Options(handler));

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form.ContainsKey("scope")).IsFalse();
        await Assert.That(form.ContainsKey("audience")).IsFalse();
    }

    [Test]
    public async Task Valid_expiry_sets_expires_at_from_the_clock()
    {
        var time = new FakeTimeProvider();
        var handler = TokenServer(_ => Ok(expiresIn: Fixture.Int("expiry", "valid_expires_in")));

        var tokens = await OidcLogin.ClientCredentialsAsync(Options(handler, time));

        await Assert.That(tokens.ExpiresAt).IsEqualTo(time.GetUtcNow().AddSeconds(Fixture.Int("expiry", "valid_expires_in")));
        await Assert.That(tokens.AccessToken).IsEqualTo("access-1");
        await Assert.That(tokens.TokenType).IsEqualTo("Bearer");
        await Assert.That(tokens.ToAccessToken().Value).IsEqualTo("access-1");
    }

    [Test]
    public async Task Invalid_expires_in_values_from_the_fixture_are_rejected_without_leaking_the_token()
    {
        foreach (var raw in Fixture.InvalidExpiresInRaw())
        {
            var json = $$"""{"access_token":"sensitive-access-token","expires_in":{{raw}}}""";
            var handler = TokenServer(_ => StubHandler.Json(json));

            var ex = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(handler))).Throws<OidcException>();

            await Assert.That(ex!.ToString()).DoesNotContain("sensitive-access-token");
        }
    }

    [Test]
    public async Task Fractional_and_huge_expiry_values_are_handled()
    {
        var time = new FakeTimeProvider();
        var fractional = TokenServer(_ => StubHandler.Json("""{"access_token":"t","expires_in":1.5}"""));
        var tokens = await OidcLogin.ClientCredentialsAsync(Options(fractional, time));
        await Assert.That(tokens.ExpiresAt).IsEqualTo(time.GetUtcNow().AddSeconds(1.5));

        var huge = TokenServer(_ => StubHandler.Json("""{"access_token":"t","expires_in":1e300}"""));
        var capped = await OidcLogin.ClientCredentialsAsync(Options(huge, time));
        await Assert.That(capped.ExpiresAt!.Value > time.GetUtcNow().AddYears(50)).IsTrue();
    }

    [Test]
    public async Task Missing_expiry_is_tolerated_one_shot_but_rejected_by_the_provider()
    {
        var handler = TokenServer(_ => StubHandler.Json("""{"access_token":"token","token_type":"Bearer"}"""));

        var tokens = await OidcLogin.ClientCredentialsAsync(Options(handler));
        await Assert.That(tokens.ExpiresAt).IsNull();

        using var auth = ClientCredentialsAuth.Create(Options(handler));
        using var request = new HttpRequestMessage();
        var ex = await Assert.That(async () => await auth.ApplyAsync(request, CancellationToken.None)).Throws<OidcException>();
        await Assert.That(ex!.Message).Contains("positive expires_in");
    }

    [Test]
    public async Task Redirects_from_the_token_endpoint_are_refused()
    {
        foreach (var status in Fixture.Ints("discovery", "redirect_statuses"))
        {
            var handler = TokenServer(_ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
                response.Headers.Location = new Uri("https://attacker.example.com/steal");
                return response;
            });

            var ex = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(handler))).Throws<OidcException>();

            await Assert.That(ex!.Message).Contains($"HTTP {status}");
            await Assert.That(handler.Requests.Count(r => r.Url.Host != "issuer.example.com")).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Oversized_token_response_is_rejected()
    {
        var handler = TokenServer(_ => StubHandler.Json(new string('x', Fixture.Int("limits", "max_response_bytes") + 1)));

        var ex = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(handler))).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("too large");
    }

    [Test]
    public async Task Redaction_keeps_secrets_and_provider_text_out_of_errors()
    {
        var secret = Fixture.Str("redaction", "secret");
        var providerBody = Fixture.Str("redaction", "provider_body");
        var supplierError = Fixture.Str("redaction", "supplier_error");

        // Provider rejection that echoes sensitive text in both error fields.
        var rejecting = TokenServer(_ => StubHandler.Json(
            $$"""{"error":"{{providerBody}}","error_description":"{{providerBody}} {{secret}}"}""", HttpStatusCode.BadRequest));
        var ex1 = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(rejecting))).Throws<OidcException>();
        await AssertClean(ex1!.ToString(), secret, providerBody);

        // A secret supplier that throws with sensitive detail.
        var options = Options(TokenServer(_ => Ok())) with
        {
            ClientSecret = null,
            ClientSecretProvider = _ => throw new InvalidOperationException(supplierError),
        };
        var ex2 = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(options)).Throws<OidcException>();
        await AssertClean(ex2!.ToString(), secret, supplierError);
        await Assert.That(ex2.InnerException).IsNull();

        // Non-JSON error page containing the secret.
        var html = TokenServer(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent($"<html>{secret} {providerBody}</html>") });
        var ex3 = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(html))).Throws<OidcException>();
        await AssertClean(ex3!.ToString(), secret, providerBody);

        // Provider and option ToStrings never expose the secret.
        await Assert.That(Options(rejecting).ToString()).DoesNotContain(secret);
        await Assert.That(ClientCredentialsAuth.Create(Options(rejecting)).ToString()).DoesNotContain(secret);
    }

    private static async Task AssertClean(string text, params string[] forbidden)
    {
        foreach (var value in forbidden)
        {
            await Assert.That(text).DoesNotContain(value);
        }
    }

    [Test]
    public async Task Response_shape_errors_are_rejected()
    {
        foreach (var body in new[] { "[]", "\"text\"", "not json", """{"expires_in":10}""", """{"access_token":""}""", """{"access_token":7}""" })
        {
            var handler = TokenServer(_ => StubHandler.Json(body));
            await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(handler))).Throws<OidcException>();
        }
    }

    [Test]
    public async Task Server_error_and_transport_failures_are_reported()
    {
        var status = TokenServer(_ => StubHandler.Json("{}", HttpStatusCode.InternalServerError));
        var ex1 = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(status))).Throws<OidcException>();
        await Assert.That(ex1!.Message).Contains("HTTP 500");

        var failing = TokenServer(_ => throw new HttpRequestException("reset"));
        var ex2 = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(Options(failing))).Throws<OidcException>();
        await Assert.That(ex2!.Failure).IsEqualTo(OidcFailure.ClientCredentials);
    }

    [Test]
    public async Task Secret_provider_supplies_the_secret()
    {
        var handler = TokenServer(_ => Ok());
        var options = Options(handler) with { ClientSecret = null, ClientSecretProvider = _ => ValueTask.FromResult("from-provider") };

        await OidcLogin.ClientCredentialsAsync(options);

        await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Form["client_secret"]).IsEqualTo("from-provider");
    }

    [Test]
    public async Task Empty_secret_from_provider_and_cancellation_are_handled()
    {
        var empty = Options(TokenServer(_ => Ok())) with { ClientSecret = null, ClientSecretProvider = _ => ValueTask.FromResult("") };
        await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(empty)).Throws<OidcException>();

        var cancelling = Options(TokenServer(_ => Ok())) with
        {
            ClientSecret = null,
            ClientSecretProvider = ct => throw new OperationCanceledException(ct),
        };
        await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(cancelling)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Configuration_errors_are_reported_up_front()
    {
        var handler = TokenServer(_ => Ok());

        var noIssuer = await Assert.That(() => ClientCredentialsAuth.Create(Options(handler) with { Issuer = null })).Throws<OidcException>();
        await Assert.That(noIssuer!.Failure).IsEqualTo(OidcFailure.Config);
        await Assert.That(noIssuer.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);

        var noClient = await Assert.That(() => ClientCredentialsAuth.Create(Options(handler) with { ClientId = "" })).Throws<OidcException>();
        await Assert.That(noClient!.Failure).IsEqualTo(OidcFailure.Config);

        var noSecret = await Assert.That(() => ClientCredentialsAuth.Create(Options(handler) with { ClientSecret = null })).Throws<OidcException>();
        await Assert.That(noSecret!.Failure).IsEqualTo(OidcFailure.ClientCredentials);

        var badTimeout = await Assert.That(() => ClientCredentialsAuth.Create(Options(handler) with { Timeout = TimeSpan.Zero })).Throws<OidcException>();
        await Assert.That(badTimeout!.Failure).IsEqualTo(OidcFailure.Config);

        await Assert.That(() => OidcResolved.Create(null!, requireSecret: false)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Gateway_metadata_fills_missing_settings()
    {
        var handler = TokenServer(_ => Ok());
        var gateway = new GatewayMetadata
        {
            Name = "gw",
            Endpoint = new Uri("https://gw.example.com"),
            Directory = "/unused",
            AuthMode = GatewayAuthMode.Oidc,
            OidcIssuer = Issuer,
            OidcClientId = "gateway-client",
            OidcAudience = "gw-audience",
            OidcScopes = "sandbox:read  sandbox:write",
        };
        var options = new OidcOptions
        {
            Gateway = gateway,
            ClientSecret = "s",
            HttpClient = new HttpClient(handler),
        };

        await OidcLogin.ClientCredentialsAsync(options);

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["client_id"]).IsEqualTo("gateway-client");
        await Assert.That(form["audience"]).IsEqualTo("gw-audience");
        await Assert.That(form["scope"]).IsEqualTo("sandbox:read sandbox:write");

        // Explicit options win over the gateway.
        var handler2 = TokenServer(_ => Ok());
        await OidcLogin.ClientCredentialsAsync(options with { ClientId = "mine", Audience = "mine-aud", Scopes = ["one"], HttpClient = new HttpClient(handler2) });
        var form2 = handler2.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form2["client_id"]).IsEqualTo("mine");
        await Assert.That(form2["audience"]).IsEqualTo("mine-aud");
        await Assert.That(form2["scope"]).IsEqualTo("one");
    }

    [Test]
    public async Task One_shot_exchange_times_out()
    {
        var gate = new TaskCompletionSource();
        var handler = TokenServer(_ => Ok());
        var slow = new SlowHandler(handler);
        var options = Options(handler) with { HttpClient = new HttpClient(slow, disposeHandler: false), Timeout = TimeSpan.FromMilliseconds(50) };

        var ex = await Assert.That(async () => await OidcLogin.ClientCredentialsAsync(options)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
        await Assert.That(ex.Kind).IsEqualTo(OpenShellErrorKind.DeadlineExceeded);
        gate.TrySetResult();
    }

    private sealed class SlowHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // ---- ClientCredentialsAuth (renewable provider) ----

    [Test]
    public async Task Provider_caches_tokens_and_renews_inside_the_leeway()
    {
        var time = new FakeTimeProvider();
        var n = 0;
        var handler = TokenServer(_ => Ok($"access-{++n}"));
        using var auth = ClientCredentialsAuth.Create(Options(handler, time));

        async Task<string> Header()
        {
            using var request = new HttpRequestMessage();
            await auth.ApplyAsync(request, CancellationToken.None);
            return request.Headers.Authorization!.Parameter!;
        }

        await Assert.That(await Header()).IsEqualTo("access-1");
        await Assert.That(await Header()).IsEqualTo("access-1");

        // 120 s lifetime, 30 s leeway: still fresh at 89 s, renewed at 91 s.
        time.Advance(TimeSpan.FromSeconds(89));
        await Assert.That(await Header()).IsEqualTo("access-1");
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(await Header()).IsEqualTo("access-2");
        await Assert.That(auth.SendsCredentials).IsTrue();
    }

    [Test]
    public async Task Provider_renews_single_flight_under_concurrency()
    {
        var release = new TaskCompletionSource();
        var inner = TokenServer(_ => Ok());
        var gated = new GatedHandler(inner, release.Task);
        using var auth = ClientCredentialsAuth.Create(Options(inner) with { HttpClient = new HttpClient(gated) });

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = new HttpRequestMessage();
            await auth.ApplyAsync(request, CancellationToken.None);
        }).ToArray();
        release.SetResult();
        await Task.WhenAll(tasks);

        await Assert.That(inner.CountTo("/token")).IsEqualTo(1);
    }

    private sealed class GatedHandler(HttpMessageHandler inner, Task gate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate;
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task Provider_recovers_after_unauthenticated_by_forcing_a_renewal()
    {
        var n = 0;
        var handler = TokenServer(_ => Ok($"access-{++n}"));
        using var auth = ClientCredentialsAuth.Create(Options(handler));
        using var first = new HttpRequestMessage();
        await auth.ApplyAsync(first, CancellationToken.None);

        var recovered = await auth.TryRecoverAsync(CancellationToken.None);

        using var second = new HttpRequestMessage();
        await auth.ApplyAsync(second, CancellationToken.None);
        await Assert.That(recovered).IsTrue();
        await Assert.That(second.Headers.Authorization!.Parameter).IsEqualTo("access-2");
    }

    [Test]
    public async Task Provider_exchange_failure_surfaces_on_use()
    {
        var handler = TokenServer(_ => StubHandler.Json("{}", HttpStatusCode.Unauthorized));
        using var auth = ClientCredentialsAuth.Create(Options(handler));
        using var request = new HttpRequestMessage();

        var ex = await Assert.That(async () => await auth.ApplyAsync(request, CancellationToken.None)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.ClientCredentials);
    }

    [Test]
    public async Task Provider_exchange_timeout_is_reported_as_timeout()
    {
        var slow = new SlowHandler(TokenServer(_ => Ok()));
        using var auth = ClientCredentialsAuth.Create(Options(TokenServer(_ => Ok())) with
        {
            HttpClient = new HttpClient(slow),
            Timeout = TimeSpan.FromMilliseconds(50),
        });
        using var request = new HttpRequestMessage();

        var ex = await Assert.That(async () => await auth.ApplyAsync(request, CancellationToken.None)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
    }

    [Test]
    public async Task Provider_honors_caller_cancellation()
    {
        var slow = new SlowHandler(TokenServer(_ => Ok()));
        using var auth = ClientCredentialsAuth.Create(Options(TokenServer(_ => Ok())) with { HttpClient = new HttpClient(slow) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var request = new HttpRequestMessage();

        await Assert.That(async () => await auth.ApplyAsync(request, cts.Token)).Throws<OperationCanceledException>();
    }
}

public class OidcTokensTests
{
    [Test]
    public async Task ToString_does_not_reveal_tokens()
    {
        var tokens = new OidcTokens("secret-access", "secret-refresh", "Bearer", DateTimeOffset.UnixEpoch);

        await Assert.That(tokens.ToString()).DoesNotContain("secret");
        await Assert.That(new OidcOptions { ClientSecret = "hunter2" }.ToString()).DoesNotContain("hunter2");
    }
}
