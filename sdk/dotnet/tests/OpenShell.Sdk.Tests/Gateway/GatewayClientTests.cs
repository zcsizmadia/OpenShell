// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class GatewayClientTests
{
    private const string Idp = "https://idp.example.com";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static GatewayConfig Config(string directory, GatewayAuthMode mode = GatewayAuthMode.None, string endpoint = "https://gw.example.com", bool oidc = true) => new()
    {
        Name = "gw",
        Endpoint = new Uri(endpoint),
        Directory = directory,
        AuthMode = mode,
        OidcIssuer = oidc ? Idp : null,
        OidcClientId = oidc ? "cli" : null,
    };

    private static async Task<HttpRequestMessage> Apply(IAuthProvider auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://gw.example.com/x");
        await auth.ApplyAsync(request, CancellationToken.None);
        return request;
    }

    // ---- options derivation ----

    [Test]
    [Arguments(GatewayAuthMode.None)]
    [Arguments(GatewayAuthMode.Plaintext)]
    [Arguments(GatewayAuthMode.Mtls)]
    public async Task Modes_without_bearer_credentials_send_none(GatewayAuthMode mode)
    {
        using var dir = new TempDirectory();
        MakeMtls(dir.Path);

        var options = GatewayClient.CreateOptions(Config(dir.Path, mode, "http://127.0.0.1:1"));

        await Assert.That(options.Auth.SendsCredentials).IsFalse();
        await Assert.That(options.Address).IsEqualTo(new Uri("http://127.0.0.1:1"));
        await Assert.That(options.DefaultWorkspace).IsEqualTo("default");
        await Assert.That(options.Tls).IsNull();
    }

    [Test]
    public async Task Unsupported_auth_mode_is_rejected()
    {
        using var dir = new TempDirectory();

        var ex = await Assert.That(() => GatewayClient.CreateOptions(Config(dir.Path, (GatewayAuthMode)99))).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        await Assert.That(() => GatewayClient.CreateOptions(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Explicit_options_override_derived_settings()
    {
        using var dir = new TempDirectory();
        var tls = new TlsOptions { InsecureSkipVerify = true };
        var auth = Auth.StaticToken("override");
        using var handler = new HttpClientHandler();

        var options = GatewayClient.CreateOptions(
            Config(dir.Path, GatewayAuthMode.CloudflareJwt),
            new GatewayConnectOptions
            {
                Auth = auth,
                Tls = tls,
                DefaultWorkspace = "team",
                DefaultTimeout = TimeSpan.FromSeconds(7),
                HttpHandler = handler,
            });

        await Assert.That(options.Auth).IsSameReferenceAs(auth);
        await Assert.That(options.Tls).IsSameReferenceAs(tls);
        await Assert.That(options.DefaultWorkspace).IsEqualTo("team");
        await Assert.That(options.DefaultTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(options.HttpHandler).IsSameReferenceAs(handler);
    }

    [Test]
    public async Task Connect_resolves_the_named_gateway_and_builds_a_client()
    {
        using var root = new TempDirectory();
        GatewayLocatorTests.Register(root, "user", "local", """{"gateway_endpoint":"http://127.0.0.1:1","auth_mode":"plaintext"}""");
        var locator = new GatewayLocator(new GatewayLocatorOptions { UserConfigDirectory = root.Combine("user"), SystemConfigDirectory = root.Combine("sys") });

        using var client = GatewayClient.Connect("local", new GatewayConnectOptions { Locator = locator, DefaultWorkspace = "w" });

        await Assert.That(client.Workspace).IsEqualTo("w");
        await Assert.That(() => GatewayClient.Connect("ghost", new GatewayConnectOptions { Locator = locator })).Throws<OpenShellException>();
    }

    // ---- mTLS ----

    private static void MakeMtls(string gatewayDir, bool ca = true, bool pair = true)
    {
        var dir = Path.Combine(gatewayDir, "mtls");
        Directory.CreateDirectory(dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        if (ca)
        {
            File.WriteAllText(Path.Combine(dir, "ca.crt"), cert.ExportCertificatePem());
        }

        if (pair)
        {
            File.WriteAllText(Path.Combine(dir, "tls.crt"), cert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(dir, "tls.key"), key.ExportPkcs8PrivateKeyPem());
        }
    }

    [Test]
    public async Task Https_gateway_with_full_bundle_gets_ca_and_client_certificate()
    {
        using var dir = new TempDirectory();
        MakeMtls(dir.Path);

        var tls = GatewayClient.CreateOptions(Config(dir.Path, GatewayAuthMode.Mtls)).Tls!;

        await Assert.That(tls.CaCertificates!.Count).IsEqualTo(1);
        await Assert.That(tls.ClientCertificate!.HasPrivateKey).IsTrue();
        await Assert.That(tls.InsecureSkipVerify).IsFalse();
    }

    [Test]
    public async Task Https_gateway_uses_the_strongest_available_trust_profile()
    {
        using var caOnly = new TempDirectory();
        MakeMtls(caOnly.Path, ca: true, pair: false);
        var tls1 = GatewayClient.CreateOptions(Config(caOnly.Path, GatewayAuthMode.Oidc)).Tls!;
        await Assert.That(tls1.CaCertificates!.Count).IsEqualTo(1);
        await Assert.That(tls1.ClientCertificate).IsNull();

        using var pairOnly = new TempDirectory();
        MakeMtls(pairOnly.Path, ca: false, pair: true);
        var tls2 = GatewayClient.CreateOptions(Config(pairOnly.Path, GatewayAuthMode.Oidc)).Tls!;
        await Assert.That(tls2.CaCertificates).IsNull();
        await Assert.That(tls2.ClientCertificate).IsNotNull();

        using var nothing = new TempDirectory();
        var tls3 = GatewayClient.CreateOptions(Config(nothing.Path, GatewayAuthMode.Oidc)).Tls!;
        await Assert.That(tls3.CaCertificates).IsNull();
        await Assert.That(tls3.ClientCertificate).IsNull();
    }

    [Test]
    public async Task Mtls_mode_requires_the_client_certificate_and_key()
    {
        using var dir = new TempDirectory();
        MakeMtls(dir.Path, ca: true, pair: false);

        var ex = await Assert.That(() => GatewayClient.CreateOptions(Config(dir.Path, GatewayAuthMode.Mtls))).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Corrupt_mtls_material_is_reported_without_file_contents()
    {
        using var dir = new TempDirectory();
        MakeMtls(dir.Path);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "mtls", "tls.key"), "-----BEGIN PRIVATE KEY-----\nSECRET-GARBAGE\n-----END PRIVATE KEY-----");

        var ex = await Assert.That(() => GatewayClient.CreateOptions(Config(dir.Path, GatewayAuthMode.Mtls))).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Tls);
        await Assert.That(ex.Message).DoesNotContain("SECRET-GARBAGE");
    }

    // ---- Cloudflare edge token ----

    [Test]
    public async Task Edge_token_is_read_lazily_and_sent_as_bearer_header_and_cookie()
    {
        using var dir = new TempDirectory();
        var options = GatewayClient.CreateOptions(Config(dir.Path, GatewayAuthMode.CloudflareJwt));
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "edge_token"), "  edge.jwt-1\n");

        using var request = await Apply(options.Auth);

        await Assert.That(options.Auth.SendsCredentials).IsTrue();
        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("edge.jwt-1");
        await Assert.That(request.Headers.GetValues("cf-access-jwt-assertion").Single()).IsEqualTo("edge.jwt-1");
        await Assert.That(request.Headers.GetValues("cookie").Single()).IsEqualTo("CF_Authorization=edge.jwt-1");
        await Assert.That(await options.Auth.TryRecoverAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Edge_token_is_cached_after_first_use()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "edge_token");
        await File.WriteAllTextAsync(path, "first");
        var auth = GatewayClient.CreateOptions(Config(dir.Path, GatewayAuthMode.CloudflareJwt)).Auth;
        using var one = await Apply(auth);

        File.Delete(path);
        using var two = await Apply(auth);

        await Assert.That(two.Headers.Authorization!.Parameter).IsEqualTo("first");
    }

    [Test]
    public async Task Legacy_cf_token_is_a_fallback_and_edge_token_wins()
    {
        using var legacy = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(legacy.Path, "cf_token"), "legacy");
        using var request = await Apply(GatewayClient.CreateOptions(Config(legacy.Path, GatewayAuthMode.CloudflareJwt)).Auth);
        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("legacy");

        using var both = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(both.Path, "cf_token"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(both.Path, "edge_token"), "primary");
        using var request2 = await Apply(GatewayClient.CreateOptions(Config(both.Path, GatewayAuthMode.CloudflareJwt)).Auth);
        await Assert.That(request2.Headers.Authorization!.Parameter).IsEqualTo("primary");
    }

    [Test]
    public async Task Missing_empty_or_unsafe_edge_tokens_are_rejected()
    {
        using var missing = new TempDirectory();
        var auth1 = GatewayClient.CreateOptions(Config(missing.Path, GatewayAuthMode.CloudflareJwt)).Auth;
        var ex1 = await Assert.That(async () => await Apply(auth1)).Throws<OpenShellException>();
        await Assert.That(ex1!.Kind).IsEqualTo(OpenShellErrorKind.Auth);

        using var empty = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(empty.Path, "edge_token"), "  \n");
        var auth2 = GatewayClient.CreateOptions(Config(empty.Path, GatewayAuthMode.CloudflareJwt)).Auth;
        var ex2 = await Assert.That(async () => await Apply(auth2)).Throws<OpenShellException>();
        await Assert.That(ex2!.Message).Contains("empty");

        using var unsafeDir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(unsafeDir.Path, "edge_token"), "a; Path=/evil");
        var auth3 = GatewayClient.CreateOptions(Config(unsafeDir.Path, GatewayAuthMode.CloudflareJwt)).Auth;
        var ex3 = await Assert.That(async () => await Apply(auth3)).Throws<OpenShellException>();
        await Assert.That(ex3!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    // ---- OIDC from disk ----

    private sealed class OidcRig : IDisposable
    {
        public OidcRig(bool autoRefresh = true, bool writeBack = true, bool oidc = true)
        {
            Time = new FakeTimeProvider(Now);
            Dir = new TempDirectory();
            Handler = StubHandler.Provider(Idp, request =>
            {
                Rotation++;
                return StubHandler.Json($$"""{"access_token":"renewed-{{Rotation}}","expires_in":3600{{(RotateRefresh ? ",\"refresh_token\":\"rotated\"" : "")}}}""");
            });
            Options = GatewayClient.CreateOptions(
                Config(Dir.Path, GatewayAuthMode.Oidc, oidc: oidc),
                new GatewayConnectOptions
                {
                    OidcHttpClient = new HttpClient(Handler),
                    TimeProvider = Time,
                    AutoRefresh = autoRefresh,
                    WriteBack = writeBack,
                });
            Auth = Options.Auth;
        }

        public FakeTimeProvider Time { get; }

        public TempDirectory Dir { get; }

        public StubHandler Handler { get; }

        public OpenShellClientOptions Options { get; }

        public IAuthProvider Auth { get; }

        public int Rotation { get; private set; }

        public bool RotateRefresh { get; set; }

        public void WriteDisk(string access, string? refresh, DateTimeOffset? expiry) =>
            OidcTokenFile.Write(Dir.Path, new OidcTokens(access, refresh, "Bearer", expiry), Time);

        public async Task<string> Header()
        {
            using var request = await Apply(Auth);
            return request.Headers.Authorization!.Parameter!;
        }

        public void Dispose()
        {
            (Auth as IDisposable)?.Dispose();
            Dir.Dispose();
        }
    }

    [Test]
    public async Task A_valid_disk_token_is_served_without_network()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("disk-token", "r", Now.AddHours(1));

        await Assert.That(await rig.Header()).IsEqualTo("disk-token");
        await Assert.That(rig.Handler.Requests.Count).IsEqualTo(0);
        await Assert.That(rig.Auth.SendsCredentials).IsTrue();
    }

    [Test]
    public async Task A_token_without_expiry_is_served_as_is()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("forever", null, null);

        await Assert.That(await rig.Header()).IsEqualTo("forever");
    }

    [Test]
    public async Task An_expired_token_is_renewed_and_written_back_keeping_the_old_refresh_token()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("stale", "refresh-1", Now.AddSeconds(10)); // inside the 30 s leeway, so unusable

        await Assert.That(await rig.Header()).IsEqualTo("renewed-1");

        var form = rig.Handler.Requests.Single(r => r.Method == HttpMethod.Post).Form;
        await Assert.That(form["grant_type"]).IsEqualTo("refresh_token");
        await Assert.That(form["refresh_token"]).IsEqualTo("refresh-1");
        var disk = OidcTokenFile.ReadRaw(rig.Dir.Path)!;
        await Assert.That(disk.AccessToken).IsEqualTo("renewed-1");
        await Assert.That(disk.RefreshToken).IsEqualTo("refresh-1");
    }

    [Test]
    public async Task A_rotated_refresh_token_is_persisted()
    {
        using var rig = new OidcRig { RotateRefresh = true };
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));

        await rig.Header();

        await Assert.That(OidcTokenFile.ReadRaw(rig.Dir.Path)!.RefreshToken).IsEqualTo("rotated");
    }

    [Test]
    public async Task Write_back_can_be_disabled()
    {
        using var rig = new OidcRig(writeBack: false);
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));

        await Assert.That(await rig.Header()).IsEqualTo("renewed-1");

        await Assert.That(OidcTokenFile.ReadRaw(rig.Dir.Path)!.AccessToken).IsEqualTo("stale");
    }

    [Test]
    public async Task A_failed_write_back_does_not_fail_the_call()
    {
        using var rig = new OidcRig();
        var path = Path.Combine(rig.Dir.Path, "oidc_token.json");
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.That(await rig.Header()).IsEqualTo("renewed-1");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task Expired_tokens_without_refresh_are_reported_with_guidance()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("stale", null, Now.AddMinutes(-5));

        var ex = await Assert.That(async () => await rig.Header()).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("re-authenticate with the CLI");
        await Assert.That(rig.Handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Auto_refresh_can_be_disabled()
    {
        using var rig = new OidcRig(autoRefresh: false);
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));

        await Assert.That(async () => await rig.Header()).Throws<OidcException>();

        await Assert.That(rig.Handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_missing_token_file_is_reported()
    {
        using var rig = new OidcRig();

        var ex = await Assert.That(async () => await rig.Header()).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("sign in with the CLI");
    }

    [Test]
    public async Task Without_oidc_metadata_an_expired_token_cannot_be_renewed()
    {
        using var rig = new OidcRig(oidc: false);
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));

        await Assert.That(async () => await rig.Header()).Throws<OidcException>();

        await Assert.That(rig.Handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unauthenticated_with_an_unchanged_disk_token_forces_a_refresh()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("still-valid-but-rejected", "refresh-1", Now.AddHours(1));
        await Assert.That(await rig.Header()).IsEqualTo("still-valid-but-rejected");

        var recovered = await rig.Auth.TryRecoverAsync(CancellationToken.None);

        await Assert.That(recovered).IsTrue();
        await Assert.That(await rig.Header()).IsEqualTo("renewed-1");
    }

    [Test]
    public async Task Unauthenticated_picks_up_a_token_the_cli_rotated_on_disk_without_network()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("old", "refresh-1", Now.AddHours(1));
        await rig.Header();
        rig.WriteDisk("cli-renewed", "refresh-2", Now.AddHours(2));

        var recovered = await rig.Auth.TryRecoverAsync(CancellationToken.None);

        await Assert.That(recovered).IsTrue();
        await Assert.That(await rig.Header()).IsEqualTo("cli-renewed");
        await Assert.That(rig.Handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Refresh_failures_propagate_without_leaking_the_refresh_token()
    {
        using var rig = new OidcRig();
        rig.Handler.Respond = _ => StubHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        rig.WriteDisk("stale", "super-secret-refresh", Now.AddMinutes(-5));

        var ex = await Assert.That(async () => await rig.Header()).Throws<OpenShellException>();

        await Assert.That(ex!.ToString()).DoesNotContain("super-secret-refresh");
    }

    [Test]
    public async Task Renewed_token_is_cached_until_it_expires_again()
    {
        using var rig = new OidcRig();
        rig.WriteDisk("stale", "refresh-1", Now.AddMinutes(-5));
        await rig.Header();
        await rig.Header();
        await Assert.That(rig.Rotation).IsEqualTo(1);

        rig.Time.Advance(TimeSpan.FromHours(2));
        await Assert.That(await rig.Header()).IsEqualTo("renewed-2");
    }
}
