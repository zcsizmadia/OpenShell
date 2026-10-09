// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;

namespace OpenShell.Tests;

public class AuthTests
{
    private static HttpRequestMessage NewRequest() => new(HttpMethod.Post, "https://gateway.example.com/x");

    [Test]
    public async Task StaticToken_sets_bearer_header()
    {
        using var request = NewRequest();
        await Auth.StaticToken("abc").ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
        await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("abc");
    }

    [Test]
    public async Task None_sends_no_credentials()
    {
        using var request = NewRequest();
        await Auth.None.ApplyAsync(request, CancellationToken.None);

        await Assert.That(Auth.None.SendsCredentials).IsFalse();
        await Assert.That(request.Headers.Authorization).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task StaticToken_rejects_blank(string token)
    {
        await Assert.That(() => Auth.StaticToken(token)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Bearer_fetches_token_per_request()
    {
        var n = 0;
        var auth = Auth.Bearer(_ => ValueTask.FromResult($"t{++n}"));
        using var r1 = NewRequest();
        using var r2 = NewRequest();
        await auth.ApplyAsync(r1, CancellationToken.None);
        await auth.ApplyAsync(r2, CancellationToken.None);

        await Assert.That(r1.Headers.Authorization!.Parameter).IsEqualTo("t1");
        await Assert.That(r2.Headers.Authorization!.Parameter).IsEqualTo("t2");
    }

    [Test]
    public async Task CloudflareAccess_sets_header_and_cookie_after_inner()
    {
        var auth = Auth.CloudflareAccess("edge.jwt-token", Auth.StaticToken("bearer"));
        using var request = NewRequest();
        await auth.ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("bearer");
        await Assert.That(request.Headers.GetValues("cf-access-jwt-assertion").Single()).IsEqualTo("edge.jwt-token");
        await Assert.That(request.Headers.GetValues("cookie").Single()).IsEqualTo("CF_Authorization=edge.jwt-token");
    }

    [Test]
    [Arguments("a;b=c")]
    [Arguments("a b")]
    [Arguments("tok\r\nX: y")]
    public async Task CloudflareAccess_rejects_unsafe_token(string token)
    {
        var ex = await Assert.That(() => Auth.CloudflareAccess(token)).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task WithHeaders_overrides_and_keeps_inner_credentials()
    {
        var auth = Auth.WithHeaders(Auth.StaticToken("t"), new Dictionary<string, string> { ["x-tenant"] = "acme" });
        using var request = NewRequest();
        await auth.ApplyAsync(request, CancellationToken.None);

        await Assert.That(auth.SendsCredentials).IsTrue();
        await Assert.That(request.Headers.GetValues("x-tenant").Single()).IsEqualTo("acme");
        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("t");
    }

    [Test]
    public async Task Cloudflare_recover_delegates_to_inner()
    {
        using var token = new RefreshableToken(new AccessToken("old"), _ => ValueTask.FromResult(new AccessToken("new")));
        var auth = Auth.CloudflareAccess("edge", token.AsAuthProvider());

        await Assert.That(await auth.TryRecoverAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await Auth.CloudflareAccess("edge").TryRecoverAsync(CancellationToken.None)).IsFalse();
    }
}

public class RefreshableTokenTests
{
    [Test]
    public async Task Returns_cached_token_while_fresh()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        using var token = new RefreshableToken(
            new AccessToken("a", time.GetUtcNow().AddMinutes(10)),
            _ => { calls++; return ValueTask.FromResult(new AccessToken("b")); },
            timeProvider: time);

        await Assert.That(await token.GetAsync()).IsEqualTo("a");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Refreshes_proactively_inside_leeway()
    {
        var time = new FakeTimeProvider();
        using var token = new RefreshableToken(
            new AccessToken("a", time.GetUtcNow().AddSeconds(20)),
            _ => ValueTask.FromResult(new AccessToken("b", time.GetUtcNow().AddHours(1))),
            leeway: TimeSpan.FromSeconds(30),
            timeProvider: time);

        await Assert.That(await token.GetAsync()).IsEqualTo("b");
    }

    [Test]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var release = new TaskCompletionSource();
        using var token = new RefreshableToken(
            new AccessToken("a", time.GetUtcNow().AddSeconds(-1)),
            async _ =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return new AccessToken("b", time.GetUtcNow().AddHours(1));
            },
            timeProvider: time);

        var tasks = Enumerable.Range(0, 8).Select(_ => token.GetAsync().AsTask()).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(results.All(r => r == "b")).IsTrue();
    }

    [Test]
    public async Task RefreshNow_reports_whether_token_changed()
    {
        var n = 0;
        using var token = new RefreshableToken(new AccessToken("a"), _ => ValueTask.FromResult(new AccessToken($"t{++n}")));

        await Assert.That(await token.RefreshNowAsync()).IsTrue();
        await Assert.That(await token.GetAsync()).IsEqualTo("t1");
    }

    [Test]
    public async Task RefreshNow_false_when_refresher_returns_same_value()
    {
        using var token = new RefreshableToken(new AccessToken("same"), _ => ValueTask.FromResult(new AccessToken("same")));

        await Assert.That(await token.RefreshNowAsync()).IsFalse();
    }

    [Test]
    public async Task Refresher_failure_is_wrapped_as_auth_error()
    {
        using var token = new RefreshableToken(
            new AccessToken("a"), _ => throw new InvalidOperationException("idp down"));

        var ex = await Assert.That(async () => await token.RefreshNowAsync()).Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Auth);
        await Assert.That(ex.InnerException).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task OpenShellException_from_refresher_passes_through()
    {
        using var token = new RefreshableToken(
            new AccessToken("a"), _ => throw new OpenShellException(OpenShellErrorKind.Auth, "invalid_grant"));

        var ex = await Assert.That(async () => await token.RefreshNowAsync()).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task Cancellation_is_not_wrapped()
    {
        using var token = new RefreshableToken(new AccessToken("a"), ct => throw new OperationCanceledException(ct));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await token.RefreshNowAsync(cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task AsAuthProvider_applies_token_and_recovers()
    {
        var n = 0;
        using var token = new RefreshableToken(new AccessToken("a"), _ => ValueTask.FromResult(new AccessToken($"t{++n}")));
        var provider = token.AsAuthProvider();
        using var request = new HttpRequestMessage();

        await provider.ApplyAsync(request, CancellationToken.None);
        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("a");
        await Assert.That(await provider.TryRecoverAsync(CancellationToken.None)).IsTrue();
    }
}
