// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

[NotInParallel("oidc-ports")]
public class OidcLoginTests
{
    private const string Issuer = "https://issuer.example.com";

    private static HttpResponseMessage TokenOk(string access = "login-access", string refresh = "login-refresh") =>
        StubHandler.Json($$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","token_type":"Bearer","expires_in":3600}""");

    private static OidcOptions Options(StubHandler handler, Func<Uri, CancellationToken, ValueTask>? launcher = null, int? port = null) => new()
    {
        Issuer = Issuer,
        ClientId = "cli",
        HttpClient = new HttpClient(handler),
        InMemory = true,
        BrowserLauncher = launcher,
        CallbackPort = port ?? TestPorts.Free(),
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>A "browser" that follows the redirect by calling the loopback callback with the given parameters.</summary>
    private static Func<Uri, CancellationToken, ValueTask> Browser(Func<Dictionary<string, string>, string> callbackQuery, Action<Uri>? onUrl = null) =>
        async (url, ct) =>
        {
            onUrl?.Invoke(url);
            var query = CallbackServer.ParseQuery(url.Query.TrimStart('?'));
            var callback = new Uri(query["redirect_uri"]);
            using var http = new HttpClient();
            await http.GetAsync($"http://127.0.0.1:{callback.Port}/callback?{callbackQuery(query)}", ct);
        };

    [Test]
    public async Task Browser_flow_exchanges_the_code_with_pkce()
    {
        Uri? authUrl = null;
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, Browser(q => $"code=the-code&state={Uri.EscapeDataString(q["state"])}", u => authUrl = u));

        var tokens = await OidcLogin.LoginAsync(options);

        await Assert.That(tokens.AccessToken).IsEqualTo("login-access");
        await Assert.That(tokens.RefreshToken).IsEqualTo("login-refresh");

        var auth = CallbackServer.ParseQuery(authUrl!.Query.TrimStart('?'));
        await Assert.That(authUrl.AbsoluteUri.StartsWith(Issuer + "/authorize?", StringComparison.Ordinal)).IsTrue();
        await Assert.That(auth["response_type"]).IsEqualTo("code");
        await Assert.That(auth["client_id"]).IsEqualTo("cli");
        await Assert.That(auth["scope"]).IsEqualTo("openid profile email");
        await Assert.That(auth["code_challenge_method"]).IsEqualTo("S256");
        await Assert.That(auth["redirect_uri"].StartsWith("http://localhost:", StringComparison.Ordinal)).IsTrue();

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["grant_type"]).IsEqualTo("authorization_code");
        await Assert.That(form["code"]).IsEqualTo("the-code");
        await Assert.That(form["redirect_uri"]).IsEqualTo(auth["redirect_uri"]);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Assert.That(auth["code_challenge"]).IsEqualTo(expected);
    }

    [Test]
    public async Task Without_s256_support_pkce_is_skipped()
    {
        Uri? authUrl = null;
        var handler = StubHandler.Provider(Issuer, _ => TokenOk(), pkce: false);
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}", u => authUrl = u));

        await OidcLogin.LoginAsync(options);

        await Assert.That(authUrl!.Query.Contains("code_challenge", StringComparison.Ordinal)).IsFalse();
        await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Form.ContainsKey("code_verifier")).IsFalse();
    }

    [Test]
    public async Task Custom_scopes_and_existing_query_on_the_authorization_endpoint_are_preserved()
    {
        Uri? authUrl = null;
        var handler = StubHandler.Provider(Issuer, _ => TokenOk(), discoveryTransform: d => d.Replace("/authorize\"", "/authorize?tenant=t1\"", StringComparison.Ordinal));
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}", u => authUrl = u)) with { Scopes = ["openid", "offline_access"] };

        await OidcLogin.LoginAsync(options);

        var query = CallbackServer.ParseQuery(authUrl!.Query.TrimStart('?'));
        await Assert.That(query["tenant"]).IsEqualTo("t1");
        await Assert.That(query["scope"]).IsEqualTo("openid offline_access");
    }

    [Test]
    public async Task Callbacks_with_the_wrong_state_are_ignored_until_the_right_one_arrives()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var statuses = new List<HttpStatusCode>();
        var options = Options(handler, async (url, ct) =>
        {
            var query = CallbackServer.ParseQuery(url.Query.TrimStart('?'));
            var port = new Uri(query["redirect_uri"]).Port;
            using var http = new HttpClient();
            statuses.Add((await http.GetAsync($"http://127.0.0.1:{port}/callback?code=evil&state=wrong", ct)).StatusCode);
            statuses.Add((await http.GetAsync($"http://127.0.0.1:{port}/callback?state={query["state"]}", ct)).StatusCode);
            statuses.Add((await http.GetAsync($"http://127.0.0.1:{port}/elsewhere", ct)).StatusCode);
            statuses.Add((await http.PostAsync($"http://127.0.0.1:{port}/callback", new StringContent(""), ct)).StatusCode);
            statuses.Add((await http.GetAsync($"http://127.0.0.1:{port}/callback?code=good&state={query["state"]}", ct)).StatusCode);
        });

        var tokens = await OidcLogin.LoginAsync(options);

        await Assert.That(tokens.AccessToken).IsEqualTo("login-access");
        await Assert.That(statuses).IsEquivalentTo(new[]
        {
            HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.OK,
        });
        await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Form["code"]).IsEqualTo("good");
    }

    [Test]
    public async Task Provider_error_on_the_callback_fails_the_login_without_echoing_provider_text()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, Browser(q => $"error=access_denied&error_description=very-sensitive&state={q["state"]}"));

        var ex = await Assert.That(async () => await OidcLogin.LoginAsync(options)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.AuthCode);
        await Assert.That(ex.Message).DoesNotContain("very-sensitive");
    }

    [Test]
    public async Task Token_endpoint_rejection_surfaces_only_the_error_code()
    {
        var handler = StubHandler.Provider(Issuer, _ => StubHandler.Json(
            """{"error":"invalid_grant","error_description":"secret detail"}""", HttpStatusCode.BadRequest));
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}"));

        var ex = await Assert.That(async () => await OidcLogin.LoginAsync(options)).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("invalid_grant");
        await Assert.That(ex.Message).DoesNotContain("secret detail");
    }

    [Test]
    public async Task Error_codes_that_do_not_look_like_codes_are_dropped()
    {
        var handler = StubHandler.Provider(Issuer, _ => StubHandler.Json("""{"error":"has spaces and secrets"}""", HttpStatusCode.BadRequest));
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}"));

        var ex = await Assert.That(async () => await OidcLogin.LoginAsync(options)).Throws<OidcException>();

        await Assert.That(ex!.Message).DoesNotContain("secrets");
    }

    [Test]
    public async Task Failing_browser_falls_back_to_the_keyboard_flow()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var output = new StringWriter();
        var options = Options(handler, (_, _) => throw new InvalidOperationException("no browser")) with
        {
            KeyboardInput = new StringReader("  pasted-code  \n"),
            KeyboardOutput = output,
        };

        await OidcLogin.LoginAsync(options);

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["code"]).IsEqualTo("pasted-code");
        await Assert.That(form["redirect_uri"]).IsEqualTo("urn:ietf:wg:oauth:2.0:oob");
        await Assert.That(output.ToString()).Contains("Paste the authorization code");
        await Assert.That(output.ToString()).Contains("https://issuer.example.com/authorize?");
    }

    [Test]
    public async Task Keyboard_flow_can_be_selected_directly()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler) with { UseKeyboardFlow = true, KeyboardInput = new StringReader("abc\n"), KeyboardOutput = new StringWriter() };

        var tokens = await OidcLogin.LoginAsync(options);

        await Assert.That(tokens.AccessToken).IsEqualTo("login-access");
    }

    [Test]
    public async Task Keyboard_flow_rejects_eof_and_empty_input()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var eof = Options(handler) with { UseKeyboardFlow = true, KeyboardInput = new StringReader(""), KeyboardOutput = new StringWriter() };
        var ex1 = await Assert.That(async () => await OidcLogin.LoginAsync(eof)).Throws<OidcException>();
        await Assert.That(ex1!.Message).Contains("EOF");

        var empty = eof with { KeyboardInput = new StringReader("   \n") };
        var ex2 = await Assert.That(async () => await OidcLogin.LoginAsync(empty)).Throws<OidcException>();
        await Assert.That(ex2!.Message).Contains("empty");

        var broken = eof with { KeyboardInput = new ThrowingReader() };
        var ex3 = await Assert.That(async () => await OidcLogin.LoginAsync(broken)).Throws<OidcException>();
        await Assert.That(ex3!.Failure).IsEqualTo(OidcFailure.AuthCode);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => throw new IOException("closed");
    }

    [Test]
    public async Task Busy_callback_port_falls_back_to_the_keyboard_flow()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            var handler = StubHandler.Provider(Issuer, _ => TokenOk());
            var options = Options(handler, port: port) with { KeyboardInput = new StringReader("kb\n"), KeyboardOutput = new StringWriter() };

            await OidcLogin.LoginAsync(options);

            await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Form["code"]).IsEqualTo("kb");
        }
        finally
        {
            busy.Stop();
        }
    }

    [Test]
    public async Task Default_ports_are_tried_in_order_then_the_keyboard_flow()
    {
        var holders = new List<TcpListener>();
        try
        {
            foreach (var port in new[] { 8000, 18000 })
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    holders.Add(listener);
                }
                catch (SocketException)
                {
                    // Already occupied by something else, which has the same effect.
                }
            }

            var handler = StubHandler.Provider(Issuer, _ => TokenOk());
            var options = Options(handler) with { CallbackPort = 0, KeyboardInput = new StringReader("kb\n"), KeyboardOutput = new StringWriter() };

            await OidcLogin.LoginAsync(options);

            await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Form["code"]).IsEqualTo("kb");
        }
        finally
        {
            foreach (var l in holders)
            {
                l.Stop();
            }
        }
    }

    [Test]
    public async Task Login_times_out_when_nothing_happens()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, (_, _) => ValueTask.CompletedTask) with { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.That(async () => await OidcLogin.LoginAsync(options)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
    }

    [Test]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var options = Options(handler, (_, _) => ValueTask.CompletedTask);

        await Assert.That(async () => await OidcLogin.LoginAsync(options, cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Tokens_are_cached_on_disk_and_reused_without_network()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}")) with
        {
            InMemory = false,
            TokenCacheDirectory = dir.Path,
            TimeProvider = time,
        };

        await OidcLogin.LoginAsync(options);
        var cached = await OidcLogin.LoginAsync(options);

        await Assert.That(cached.AccessToken).IsEqualTo("login-access");
        await Assert.That(handler.CountTo("/token")).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(dir.Path, "oidc_token.json"))).IsTrue();

        // Once expired the cache is ignored and a fresh login happens.
        time.Advance(TimeSpan.FromHours(2));
        await OidcLogin.LoginAsync(options);
        await Assert.That(handler.CountTo("/token")).IsEqualTo(2);
    }

    [Test]
    public async Task In_memory_login_never_touches_the_disk()
    {
        using var dir = new TempDirectory();
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}")) with { TokenCacheDirectory = dir.Path, InMemory = true };

        await OidcLogin.LoginAsync(options);

        await Assert.That(Directory.GetFileSystemEntries(dir.Path).Length).IsEqualTo(0);
    }

    [Test]
    public async Task A_corrupt_cache_does_not_block_a_fresh_login()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"), "{corrupt");
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = Options(handler, Browser(q => $"code=c&state={q["state"]}")) with { InMemory = true, TokenCacheDirectory = dir.Path };
        // InMemory skips the cache entirely; flip it off to exercise the corrupt-read path, accepting that the write then replaces it.
        options = options with { InMemory = false };

        var tokens = await OidcLogin.LoginAsync(options);

        await Assert.That(tokens.AccessToken).IsEqualTo("login-access");
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)!.AccessToken).IsEqualTo("login-access");
    }

    [Test]
    public async Task Gateway_directory_is_the_default_cache_location()
    {
        using var dir = new TempDirectory();
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var gateway = new GatewayConfig { Name = "gw", Endpoint = new Uri("https://gw.example.com"), Directory = dir.Path, OidcIssuer = Issuer, OidcClientId = "cli" };
        var options = new OidcOptions
        {
            Gateway = gateway,
            HttpClient = new HttpClient(handler),
            BrowserLauncher = Browser(q => $"code=c&state={q["state"]}"),
            CallbackPort = TestPorts.Free(),
        };

        await OidcLogin.LoginAsync(options);

        await Assert.That(File.Exists(Path.Combine(dir.Path, "oidc_token.json"))).IsTrue();
    }

    [Test]
    public async Task Login_requires_issuer_and_client()
    {
        var ex = await Assert.That(async () => await OidcLogin.LoginAsync(new OidcOptions())).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Config);
    }

    // ---- refresh ----

    [Test]
    public async Task Refresh_sends_the_refresh_grant()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk("renewed", "rotated"));
        var options = new OidcOptions
        {
            Issuer = Issuer,
            ClientId = "cli",
            ClientSecret = "shh",
            Scopes = ["openid"],
            HttpClient = new HttpClient(handler),
        };

        var tokens = await OidcLogin.RefreshAsync(options, "old-refresh");

        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["grant_type"]).IsEqualTo("refresh_token");
        await Assert.That(form["refresh_token"]).IsEqualTo("old-refresh");
        await Assert.That(form["client_id"]).IsEqualTo("cli");
        await Assert.That(form["client_secret"]).IsEqualTo("shh");
        await Assert.That(form["scope"]).IsEqualTo("openid");
        await Assert.That(tokens.AccessToken).IsEqualTo("renewed");
        await Assert.That(tokens.RefreshToken).IsEqualTo("rotated");
    }

    [Test]
    public async Task Refresh_without_secret_or_scopes_sends_neither_and_supports_a_secret_provider()
    {
        var handler = StubHandler.Provider(Issuer, _ => TokenOk());
        var options = new OidcOptions { Issuer = Issuer, ClientId = "cli", HttpClient = new HttpClient(handler) };
        await OidcLogin.RefreshAsync(options, "r");
        var form = handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form.ContainsKey("client_secret")).IsFalse();
        await Assert.That(form.ContainsKey("scope")).IsFalse();

        var handler2 = StubHandler.Provider(Issuer, _ => TokenOk());
        await OidcLogin.RefreshAsync(options with { HttpClient = new HttpClient(handler2), ClientSecretProvider = _ => ValueTask.FromResult("prov") }, "r");
        await Assert.That(handler2.Requests.Single(r => r.Method == HttpMethod.Post).Form["client_secret"]).IsEqualTo("prov");
    }

    [Test]
    public async Task Refresh_failures_are_reported_without_leaking()
    {
        var rejected = StubHandler.Provider(Issuer, _ => StubHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var options = new OidcOptions { Issuer = Issuer, ClientId = "cli", HttpClient = new HttpClient(rejected) };
        var ex = await Assert.That(async () => await OidcLogin.RefreshAsync(options, "old-refresh")).Throws<OidcException>();
        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Refresh);
        await Assert.That(ex.Message).Contains("invalid_grant");
        await Assert.That(ex.ToString()).DoesNotContain("old-refresh");

        var throwing = options with { ClientSecretProvider = _ => throw new InvalidOperationException("vault says no") };
        var ex2 = await Assert.That(async () => await OidcLogin.RefreshAsync(throwing, "r")).Throws<OidcException>();
        await Assert.That(ex2!.ToString()).DoesNotContain("vault says no");

        var cancelling = options with { ClientSecretProvider = ct => throw new OperationCanceledException(ct) };
        await Assert.That(async () => await OidcLogin.RefreshAsync(cancelling, "r")).Throws<OperationCanceledException>();

        await Assert.That(async () => await OidcLogin.RefreshAsync(options, "")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Refresh_times_out()
    {
        var slow = new SlowTokenHandler();
        var options = new OidcOptions { Issuer = Issuer, ClientId = "cli", HttpClient = new HttpClient(slow), Timeout = TimeSpan.FromMilliseconds(50) };

        var ex = await Assert.That(async () => await OidcLogin.RefreshAsync(options, "r")).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
    }

    private sealed class SlowTokenHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public class OidcDeviceLoginTests
{
    private const string Issuer = "https://issuer.example.com";

    private static string DeviceJson(int expiresIn = 600, int interval = 5) =>
        $$"""{"device_code":"dev-code","user_code":"ABCD-EFGH","verification_uri":"https://issuer.example.com/activate","expires_in":{{expiresIn}},"interval":{{interval}}}""";

    private static OidcOptions Options(StubHandler handler, FakeTimeProvider time, List<(string, string)>? shown = null) => new()
    {
        Issuer = Issuer,
        ClientId = "cli",
        HttpClient = new HttpClient(handler),
        TimeProvider = time,
        DisplayDeviceCode = (uri, code) => shown?.Add((uri, code)),
        Timeout = TimeSpan.FromHours(1),
    };

    private static HttpResponseMessage Pending() => StubHandler.Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);

    [Test]
    public async Task Polls_through_pending_and_slow_down_until_authorized()
    {
        var time = new FakeTimeProvider();
        var polls = new Queue<HttpResponseMessage>([
            Pending(),
            StubHandler.Json("""{"error":"slow_down"}""", HttpStatusCode.BadRequest),
            StubHandler.Json("""{"access_token":"dev-access","refresh_token":"dev-refresh","expires_in":300}"""),
        ]);
        var handler = StubHandler.Provider(Issuer, token: _ => polls.Dequeue(), device: _ => StubHandler.Json(DeviceJson()), deviceEndpoint: true);
        var shown = new List<(string, string)>();

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time, shown));
        await time.AdvanceUntilDone(task);
        var tokens = await task;

        await Assert.That(tokens.AccessToken).IsEqualTo("dev-access");
        await Assert.That(shown.Single()).IsEqualTo(("https://issuer.example.com/activate", "ABCD-EFGH"));
        var deviceForm = handler.Requests.Single(r => r.Url.AbsolutePath == "/device").Form;
        await Assert.That(deviceForm["client_id"]).IsEqualTo("cli");
        await Assert.That(deviceForm["scope"]).IsEqualTo("openid profile email");
        var pollForm = handler.Requests.Last(r => r.Url.AbsolutePath == "/token").Form;
        await Assert.That(pollForm["grant_type"]).IsEqualTo("urn:ietf:params:oauth:grant-type:device_code");
        await Assert.That(pollForm["device_code"]).IsEqualTo("dev-code");
    }

    [Test]
    public async Task Missing_interval_defaults_to_five_seconds()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(
            Issuer,
            token: _ => StubHandler.Json("""{"access_token":"a"}"""),
            device: _ => StubHandler.Json(DeviceJson(interval: 0)),
            deviceEndpoint: true);

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time));
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50);
        await Assert.That(task.IsCompleted).IsFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That((await task).AccessToken).IsEqualTo("a");
    }

    [Test]
    [Arguments("expired_token", "expired")]
    [Arguments("access_denied", "denied")]
    [Arguments("invalid_client", "invalid_client")]
    [Arguments("weird error!", "device code exchange failed")]
    public async Task Terminal_errors_are_reported(string error, string expected)
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(
            Issuer,
            token: _ => StubHandler.Json($$"""{"error":"{{error}}","error_description":"private"}""", HttpStatusCode.BadRequest),
            device: _ => StubHandler.Json(DeviceJson()),
            deviceEndpoint: true);

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time));
        await time.AdvanceUntilDone(task);

        var ex = await Assert.That(async () => await task).Throws<OidcException>();
        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.DeviceCode);
        await Assert.That(ex.Message).Contains(expected);
        await Assert.That(ex.Message).DoesNotContain("private");
    }

    [Test]
    public async Task Device_code_expiry_is_a_timeout()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer, token: _ => Pending(), device: _ => StubHandler.Json(DeviceJson(expiresIn: 12, interval: 5)), deviceEndpoint: true);

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time));
        await time.AdvanceUntilDone(task);

        var ex = await Assert.That(async () => await task).Throws<OidcException>();
        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
    }

    [Test]
    public async Task Overall_timeout_applies_when_the_device_response_has_no_expiry()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer, token: _ => Pending(), device: _ => StubHandler.Json(DeviceJson(expiresIn: 0)), deviceEndpoint: true);
        var options = Options(handler, time) with { Timeout = TimeSpan.FromSeconds(30) };

        var task = OidcLogin.DeviceLoginAsync(options);
        await time.AdvanceUntilDone(task);

        var ex = await Assert.That(async () => await task).Throws<OidcException>();
        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.Timeout);
    }

    [Test]
    public async Task Provider_without_a_device_endpoint_is_rejected()
    {
        var handler = StubHandler.Provider(Issuer, deviceEndpoint: false);

        var ex = await Assert.That(async () => await OidcLogin.DeviceLoginAsync(Options(handler, new FakeTimeProvider()))).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("device_authorization_endpoint");
    }

    [Test]
    public async Task Device_authorization_response_problems_are_rejected()
    {
        foreach (var body in new[]
        {
            "{not json",
            "null",
            """{"user_code":"x","verification_uri":"u"}""",
            """{"device_code":"x","verification_uri":"u"}""",
            """{"device_code":"x","user_code":"y"}""",
        })
        {
            var handler = StubHandler.Provider(Issuer, device: _ => StubHandler.Json(body), deviceEndpoint: true);
            var ex = await Assert.That(async () => await OidcLogin.DeviceLoginAsync(Options(handler, new FakeTimeProvider()))).Throws<OidcException>();
            await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.DeviceCode);
        }

        var notOk = StubHandler.Provider(Issuer, device: _ => StubHandler.Json("{}", HttpStatusCode.BadRequest), deviceEndpoint: true);
        var ex2 = await Assert.That(async () => await OidcLogin.DeviceLoginAsync(Options(notOk, new FakeTimeProvider()))).Throws<OidcException>();
        await Assert.That(ex2!.Message).Contains("HTTP 400");
    }

    [Test]
    public async Task Without_a_display_callback_the_code_goes_to_the_console()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(
            Issuer,
            token: _ => StubHandler.Json("""{"access_token":"a"}"""),
            device: _ => StubHandler.Json(DeviceJson()),
            deviceEndpoint: true);

        // The console branch writes through TUnit's redirected console; completing proves it ran.
        var task = OidcLogin.DeviceLoginAsync(Options(handler, time) with { DisplayDeviceCode = null });
        await time.AdvanceUntilDone(task);

        await Assert.That((await task).AccessToken).IsEqualTo("a");
    }

    [Test]
    public async Task Invalid_poll_responses_are_reported()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer, token: _ => StubHandler.Json("[1]"), device: _ => StubHandler.Json(DeviceJson()), deviceEndpoint: true);

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time));
        await time.AdvanceUntilDone(task);

        await Assert.That(async () => await task).Throws<OidcException>();
    }

    [Test]
    public async Task Cancellation_stops_polling()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Provider(Issuer, token: _ => Pending(), device: _ => StubHandler.Json(DeviceJson()), deviceEndpoint: true);
        using var cts = new CancellationTokenSource();

        var task = OidcLogin.DeviceLoginAsync(Options(handler, time), cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Configuration_is_validated()
    {
        await Assert.That(async () => await OidcLogin.DeviceLoginAsync(new OidcOptions())).Throws<OidcException>();
    }
}
