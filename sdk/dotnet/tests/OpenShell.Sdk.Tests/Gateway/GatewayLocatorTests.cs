// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Gateway;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class GatewayLocatorTests
{
    private static GatewayLocator Locator(TempDirectory root, Dictionary<string, string>? env = null) =>
        new(new GatewayLocatorOptions
        {
            UserConfigDirectory = root.Combine("user"),
            SystemConfigDirectory = root.Combine("sys"),
            GetEnvironmentVariable = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
        });

    internal static string Register(TempDirectory root, string area, string name, string metadata)
    {
        var dir = root.Make(area, "gateways", name);
        File.WriteAllText(Path.Combine(dir, "metadata.json"), metadata);
        return dir;
    }

    private static string Metadata(string endpoint = "https://gw.example.com", string? authMode = null, string extra = "") =>
        "{" + $"\"gateway_endpoint\":\"{endpoint}\"" + (authMode is null ? "" : $",\"auth_mode\":\"{authMode}\"") + extra + "}";

    [Test]
    public async Task Loads_a_user_gateway_by_name()
    {
        using var root = new TempDirectory();
        var dir = Register(root, "user", "dev", Metadata(extra: """
            ,"oidc_issuer":"https://idp.example.com","oidc_client_id":"cli","oidc_audience":"aud","oidc_scopes":"a b"
            """));

        var config = Locator(root).Load("dev");

        await Assert.That(config.Name).IsEqualTo("dev");
        await Assert.That(config.Endpoint).IsEqualTo(new Uri("https://gw.example.com"));
        await Assert.That(config.Source).IsEqualTo(GatewayConfigSource.User);
        await Assert.That(config.Directory).IsEqualTo(dir);
        await Assert.That(config.AuthMode).IsEqualTo(GatewayAuthMode.None);
        await Assert.That(config.OidcIssuer).IsEqualTo("https://idp.example.com");
        await Assert.That(config.OidcClientId).IsEqualTo("cli");
        await Assert.That(config.OidcAudience).IsEqualTo("aud");
        await Assert.That(config.OidcScopes).IsEqualTo("a b");
    }

    [Test]
    [Arguments(null, GatewayAuthMode.None)]
    [Arguments("", GatewayAuthMode.None)]
    [Arguments("none", GatewayAuthMode.None)]
    [Arguments("plaintext", GatewayAuthMode.Plaintext)]
    [Arguments("cloudflare_jwt", GatewayAuthMode.CloudflareJwt)]
    [Arguments("oidc", GatewayAuthMode.Oidc)]
    [Arguments("mtls", GatewayAuthMode.Mtls)]
    public async Task Parses_every_auth_mode(string? mode, GatewayAuthMode expected)
    {
        using var root = new TempDirectory();
        Register(root, "user", "gw", Metadata(authMode: mode));

        await Assert.That(Locator(root).Load("gw").AuthMode).IsEqualTo(expected);
    }

    [Test]
    public async Task Unknown_auth_mode_is_rejected()
    {
        using var root = new TempDirectory();
        Register(root, "user", "gw", Metadata(authMode: "kerberos"));

        var ex = await Assert.That(() => Locator(root).Load("gw")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task System_registrations_are_found_and_user_ones_shadow_them()
    {
        using var root = new TempDirectory();
        Register(root, "sys", "shared", Metadata("https://system.example.com"));
        Register(root, "sys", "only-sys", Metadata("https://only-sys.example.com"));
        Register(root, "user", "shared", Metadata("https://user.example.com"));

        var locator = Locator(root);

        await Assert.That(locator.Load("shared").Endpoint.Host).IsEqualTo("user.example.com");
        var system = locator.Load("only-sys");
        await Assert.That(system.Source).IsEqualTo(GatewayConfigSource.System);
        await Assert.That(system.Endpoint.Host).IsEqualTo("only-sys.example.com");
    }

    [Test]
    public async Task Unknown_gateway_is_not_found()
    {
        using var root = new TempDirectory();

        var ex = await Assert.That(() => Locator(root).Load("ghost")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
    }

    [Test]
    public async Task Metadata_problems_are_reported()
    {
        using var root = new TempDirectory();
        Register(root, "user", "badjson", "{nope");
        Register(root, "user", "noendpoint", "{}");
        Register(root, "user", "nullbody", "null");
        Register(root, "user", "relative", """{"gateway_endpoint":"/just/a/path"}""");
        Register(root, "user", "scheme", """{"gateway_endpoint":"ftp://gw.example.com"}""");
        root.Make("user", "gateways", "nometa");
        var locator = Locator(root);

        foreach (var name in new[] { "badjson", "noendpoint", "nullbody", "relative", "scheme", "nometa" })
        {
            var ex = await Assert.That(() => locator.Load(name)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("..")]
    [Arguments("a.b")]
    [Arguments("with space")]
    [Arguments("üñí")]
    [Arguments("semi;colon")]
    public async Task Gateway_names_cannot_escape_the_registry(string name)
    {
        using var root = new TempDirectory();

        var ex = await Assert.That(() => Locator(root).Load(name)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Valid_names_are_accepted()
    {
        GatewayLocator.ValidateName("Prod-1_eu");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Active_gateway_comes_from_the_environment_first()
    {
        using var root = new TempDirectory();
        Register(root, "user", "from-env", Metadata("https://env.example.com"));
        Register(root, "user", "from-file", Metadata("https://file.example.com"));
        File.WriteAllText(Path.Combine(root.Make("user"), "active_gateway"), "from-file\n");
        var locator = Locator(root, new() { ["OPENSHELL_GATEWAY"] = "from-env" });

        await Assert.That(locator.ResolveActiveName()).IsEqualTo("from-env");
        await Assert.That(locator.Load().Endpoint.Host).IsEqualTo("env.example.com");
    }

    [Test]
    public async Task Active_gateway_falls_back_to_the_file()
    {
        using var root = new TempDirectory();
        Register(root, "user", "from-file", Metadata("https://file.example.com"));
        File.WriteAllText(Path.Combine(root.Make("user"), "active_gateway"), "  from-file \n");

        await Assert.That(Locator(root).Load().Name).IsEqualTo("from-file");
    }

    [Test]
    public async Task Missing_empty_or_invalid_active_gateway_is_reported()
    {
        using var root = new TempDirectory();
        var locator = Locator(root);
        var missing = await Assert.That(locator.ResolveActiveName).Throws<OpenShellException>();
        await Assert.That(missing!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);

        var file = Path.Combine(root.Make("user"), "active_gateway");
        File.WriteAllText(file, "  \n");
        var empty = await Assert.That(locator.ResolveActiveName).Throws<OpenShellException>();
        await Assert.That(empty!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);

        File.WriteAllText(file, "../escape");
        var invalid = await Assert.That(locator.ResolveActiveName).Throws<OpenShellException>();
        await Assert.That(invalid!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);

        var badEnv = Locator(root, new() { ["OPENSHELL_GATEWAY"] = "a/b" });
        await Assert.That(badEnv.ResolveActiveName).Throws<OpenShellException>();
    }

    [Test]
    public async Task List_merges_user_and_system_marks_active_and_skips_invalid_directories()
    {
        using var root = new TempDirectory();
        Register(root, "user", "bravo", Metadata());
        Register(root, "user", "alpha", Metadata());
        Register(root, "sys", "alpha", Metadata());
        Register(root, "sys", "charlie", Metadata());
        root.Make("user", "gateways", "bad.name");
        File.WriteAllText(Path.Combine(root.Make("user"), "active_gateway"), "bravo");

        var list = Locator(root).List();

        await Assert.That(list.Select(i => (i.Name, i.Active, i.Source))).IsEquivalentTo(new[]
        {
            ("alpha", false, GatewayConfigSource.User),
            ("bravo", true, GatewayConfigSource.User),
            ("charlie", false, GatewayConfigSource.System),
        });
    }

    [Test]
    public async Task List_is_empty_when_nothing_is_registered()
    {
        using var root = new TempDirectory();

        await Assert.That(Locator(root).List().Count).IsEqualTo(0);
    }

    [Test]
    public async Task User_directory_honors_xdg_config_home()
    {
        var xdg = Path.Combine(Path.GetTempPath(), "xdg-home");
        var locator = new GatewayLocator(new GatewayLocatorOptions { GetEnvironmentVariable = n => n == "XDG_CONFIG_HOME" ? xdg : null });

        await Assert.That(locator.UserConfigDirectory).IsEqualTo(Path.Combine(xdg, "openshell"));
    }

    [Test]
    public async Task Relative_xdg_config_home_is_rejected()
    {
        var locator = new GatewayLocator(new GatewayLocatorOptions { GetEnvironmentVariable = n => n == "XDG_CONFIG_HOME" ? "relative/dir" : null });

        await Assert.That(() => locator.UserConfigDirectory).Throws<OpenShellException>();
    }

    [Test]
    public async Task User_directory_falls_back_to_the_platform_default()
    {
        var home = Path.Combine(Path.GetTempPath(), "fake-home");
        var appData = Path.Combine(Path.GetTempPath(), "fake-appdata");
        var locator = new GatewayLocator(new GatewayLocatorOptions
        {
            GetEnvironmentVariable = n => n switch
            {
                "APPDATA" => appData,
                "HOME" or "USERPROFILE" => home,
                _ => null,
            },
        });

        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(appData, "openshell")
            : Path.Combine(home, ".config", "openshell");
        await Assert.That(locator.UserConfigDirectory).IsEqualTo(expected);
    }

    [Test]
    public async Task User_directory_without_any_environment_still_resolves()
    {
        var locator = new GatewayLocator(new GatewayLocatorOptions { GetEnvironmentVariable = _ => null });

        await Assert.That(locator.UserConfigDirectory.EndsWith("openshell", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task System_directory_defaults_by_platform_and_can_be_overridden()
    {
        var expected = OperatingSystem.IsWindows() ? null : "/etc/openshell";
        await Assert.That(new GatewayLocator().SystemConfigDirectory).IsEqualTo(expected);
        await Assert.That(new GatewayLocator(new GatewayLocatorOptions { SystemConfigDirectory = "/custom" }).SystemConfigDirectory).IsEqualTo("/custom");
    }

    [Test]
    public async Task Default_locator_reads_the_process_environment()
    {
        var locator = new GatewayLocator();

        await Assert.That(locator.UserConfigDirectory.Length).IsGreaterThan(0);
    }
}
