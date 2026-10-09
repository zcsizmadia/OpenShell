// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenShell.Oidc;

namespace OpenShell.DependencyInjection.Tests;

public class ClientBuilderTests
{
    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    private static OpenShellOptions Options(ServiceProvider provider, string name = "") =>
        provider.GetRequiredService<IOptionsMonitor<OpenShellOptions>>().Get(name);

    [Test]
    public async Task Default_builder_registers_a_resolvable_client_with_every_setting_applied()
    {
        using var provider = Build(s => s.AddOpenShellClient()
            .WithAddress("http://localhost:1")
            .WithWorkspace("team-a")
            .WithTimeout(TimeSpan.FromSeconds(7)));

        var client = provider.GetRequiredService<IOpenShellClient>();
        var options = Options(provider);

        await Assert.That(client.Workspace).IsEqualTo("team-a");
        await Assert.That(options.Address).IsEqualTo(new Uri("http://localhost:1"));
        await Assert.That(options.DefaultTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(provider.GetRequiredService<ISandboxClient>()).IsSameReferenceAs(client.Sandboxes);
    }

    [Test]
    public async Task Named_builder_registers_a_keyed_client_and_no_default()
    {
        using var provider = Build(s => s.AddOpenShellClient("prod").WithAddress(new Uri("http://localhost:2")).WithWorkspace("prod-ws"));

        var keyed = provider.GetRequiredKeyedService<IOpenShellClient>("prod");

        await Assert.That(keyed.Workspace).IsEqualTo("prod-ws");
        await Assert.That(provider.GetRequiredService<IOpenShellClientFactory>().Create("prod")).IsSameReferenceAs(keyed);
        await Assert.That(provider.GetService<IOpenShellClient>()).IsNull();
    }

    [Test]
    public async Task The_builder_exposes_its_services_and_name()
    {
        var services = new ServiceCollection();

        var builder = services.AddOpenShellClient("n");

        await Assert.That(builder.Services).IsSameReferenceAs(services);
        await Assert.That(builder.Name).IsEqualTo("n");
        await Assert.That(services.AddOpenShellClient().Name).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Bearer_token_becomes_a_static_bearer_provider()
    {
        using var provider = Build(s => s.AddOpenShellClient().WithAddress("https://g.example.com").WithBearerToken("tok"));

        var client = Options(provider).ToClientOptions(null);
        using var request = new HttpRequestMessage();
        await client.Auth.ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("tok");
    }

    [Test]
    public async Task Auth_providers_can_be_given_directly_or_built_from_services()
    {
        var direct = Auth.StaticToken("direct");
        using var provider = Build(s =>
        {
            s.AddSingleton(new Secret("from-di"));
            s.AddOpenShellClient("a").WithAddress("https://g.example.com").WithAuth(direct);
            s.AddOpenShellClient("b").WithAddress("https://g.example.com").WithAuth(sp => Auth.StaticToken(sp.GetRequiredService<Secret>().Value));
        });

        using var first = new HttpRequestMessage();
        using var second = new HttpRequestMessage();
        await Options(provider, "a").Auth!.ApplyAsync(first, CancellationToken.None);
        await Options(provider, "b").Auth!.ApplyAsync(second, CancellationToken.None);

        await Assert.That(first.Headers.Authorization!.Parameter).IsEqualTo("direct");
        await Assert.That(second.Headers.Authorization!.Parameter).IsEqualTo("from-di");
    }

    [Test]
    public async Task Tls_settings_map_to_options()
    {
        var tls = new TlsOptions { InsecureSkipVerify = true };
        using var provider = Build(s =>
        {
            s.AddOpenShellClient("paths").WithAddress("https://g.example.com").WithCaCertificate("/ca.pem").WithClientCertificate("/c.pem", "/k.pem");
            s.AddOpenShellClient("code").WithAddress("https://g.example.com").WithTls(tls);
            s.AddOpenShellClient("insecure").WithAddress("https://g.example.com").WithInsecureTls().AllowInsecureAuth();
            s.AddOpenShellClient("same-file").WithAddress("https://g.example.com").WithClientCertificate("/both.pem");
        });

        // Certificate paths are validated at startup, so unreadable files surface as a validation error, which
        // proves the builder applied them.
        var pathsFailure = await Assert.That(() => Options(provider, "paths")).Throws<OptionsValidationException>();
        await Assert.That(pathsFailure!.Message).Contains("certificate could not be loaded");
        await Assert.That(Options(provider, "code").Tls).IsSameReferenceAs(tls);
        await Assert.That(Options(provider, "insecure").InsecureSkipVerify).IsTrue();
        await Assert.That(Options(provider, "insecure").AllowInsecureAuth).IsTrue();
        await Assert.That(() => Options(provider, "same-file")).Throws<OptionsValidationException>();
    }

    [Test]
    public async Task Retry_can_be_tuned_or_turned_off()
    {
        using var provider = Build(s =>
        {
            s.AddOpenShellClient("tuned").WithAddress("http://localhost:1").WithRetry(r => r.MaxAttempts = 6).WithRetry(r => r.Jitter = 0.5);
            s.AddOpenShellClient("off").WithAddress("http://localhost:1").WithoutRetry();
        });

        var tuned = Options(provider, "tuned").Retry!.ToPolicy();
        await Assert.That(tuned.MaxAttempts).IsEqualTo(6);
        await Assert.That(tuned.Jitter).IsEqualTo(0.5);
        await Assert.That(tuned.InitialBackoff).IsEqualTo(RetryPolicy.Default.InitialBackoff);
        await Assert.That(Options(provider, "off").Retry!.ToPolicy().IsEnabled).IsFalse();
    }

    [Test]
    public async Task Configuration_can_be_bound_and_then_overridden_fluently()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Address"] = "http://localhost:1", ["Workspace"] = "from-config" })
            .Build();
        using var provider = Build(s => s.AddOpenShellClient().BindConfiguration(config).WithWorkspace("override"));

        var options = Options(provider);

        await Assert.That(options.Address).IsEqualTo(new Uri("http://localhost:1"));
        await Assert.That(options.Workspace).IsEqualTo("override");
    }

    [Test]
    public async Task Configure_overloads_apply_in_registration_order_and_can_use_services()
    {
        using var provider = Build(s =>
        {
            s.AddSingleton(new Secret("svc"));
            s.AddOpenShellClient()
                .Configure(o => o.Address = new Uri("http://localhost:1"))
                .Configure((sp, o) => o.Workspace = sp.GetRequiredService<Secret>().Value);
        });

        await Assert.That(Options(provider).Workspace).IsEqualTo("svc");
    }

    [Test]
    public async Task Client_credentials_provider_is_a_container_singleton_disposed_with_the_container()
    {
        var provider = Build(s => s.AddOpenShellClient()
            .WithAddress("https://g.example.com")
            .WithClientCredentials("https://idp.example.com", "svc", _ => ValueTask.FromResult("s3cret"), "sandbox:read", "sandbox:write"));

        var auth = Options(provider).Auth;
        await Assert.That(auth).IsTypeOf<ClientCredentialsAuth>();
        await Assert.That(Options(provider).Auth).IsSameReferenceAs(auth);

        provider.Dispose();

        await Assert.That(async () => await auth!.TryRecoverAsync(CancellationToken.None)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Client_credentials_scopes_are_optional_and_options_can_come_from_services()
    {
        using var provider = Build(s =>
        {
            s.AddSingleton(new Secret("https://idp.example.com"));
            s.AddOpenShellClient("noscopes").WithAddress("https://g.example.com")
                .WithClientCredentials("https://idp.example.com", "svc", _ => ValueTask.FromResult("x"));
            s.AddOpenShellClient("custom").WithAddress("https://g.example.com")
                .WithClientCredentials(sp => new OidcOptions
                {
                    Issuer = sp.GetRequiredService<Secret>().Value,
                    ClientId = "svc",
                    ClientSecretProvider = _ => ValueTask.FromResult("y"),
                });
        });

        await Assert.That(Options(provider, "noscopes").Auth).IsTypeOf<ClientCredentialsAuth>();
        await Assert.That(Options(provider, "custom").Auth).IsTypeOf<ClientCredentialsAuth>();
        await Assert.That(Options(provider, "noscopes").Auth).IsNotSameReferenceAs(Options(provider, "custom").Auth);
    }

    [Test]
    public async Task Arguments_are_validated()
    {
        var builder = new ServiceCollection().AddOpenShellClient();

        await Assert.That(() => ((IServiceCollection)null!).AddOpenShellClient()).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Configure((Action<OpenShellOptions>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Configure((Action<IServiceProvider, OpenShellOptions>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.BindConfiguration(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithAddress((Uri)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithAddress("not a url")).Throws<UriFormatException>();
        await Assert.That(() => builder.WithWorkspace(" ")).Throws<ArgumentException>();
        await Assert.That(() => builder.WithBearerToken("")).Throws<ArgumentException>();
        await Assert.That(() => builder.WithAuth((IAuthProvider)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithAuth((Func<IServiceProvider, IAuthProvider>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithClientCredentials((Func<IServiceProvider, OidcOptions>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithClientCredentials("", "c", _ => ValueTask.FromResult("s"))).Throws<ArgumentException>();
        await Assert.That(() => builder.WithClientCredentials("https://i", "", _ => ValueTask.FromResult("s"))).Throws<ArgumentException>();
        await Assert.That(() => builder.WithClientCredentials("https://i", "c", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithCaCertificate("")).Throws<ArgumentException>();
        await Assert.That(() => builder.WithClientCertificate(" ")).Throws<ArgumentException>();
        await Assert.That(() => builder.WithTls(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithTimeout(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => builder.WithRetry(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Builder_and_classic_registration_share_one_set_of_core_services()
    {
        var services = new ServiceCollection();
        services.AddOpenShell("classic", o => o.Address = new Uri("http://localhost:1"));
        services.AddOpenShellClient("fluent").WithAddress("http://localhost:2");

        await Assert.That(services.Count(d => d.ServiceType == typeof(IOpenShellClientFactory))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.IsKeyedService && d.ServiceType == typeof(IOpenShellClient))).IsEqualTo(2);
    }

    private sealed record Secret(string Value);
}
