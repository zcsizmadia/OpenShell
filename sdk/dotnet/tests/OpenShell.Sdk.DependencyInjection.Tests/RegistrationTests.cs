// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenShell.DependencyInjection.Tests;

public class RegistrationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Default_registration_resolves_client_and_sub_clients_as_singletons()
    {
        using var provider = Build(s => s.AddOpenShell(o => o.Address = new Uri("http://localhost:1")));

        var client = provider.GetRequiredService<IOpenShellClient>();

        await Assert.That(provider.GetRequiredService<IOpenShellClient>()).IsSameReferenceAs(client);
        await Assert.That(provider.GetRequiredService<IHealthClient>()).IsSameReferenceAs(client.Health);
        await Assert.That(provider.GetRequiredService<ISandboxClient>()).IsSameReferenceAs(client.Sandboxes);
        await Assert.That(provider.GetRequiredService<IExecClient>()).IsSameReferenceAs(client.Exec);
        await Assert.That(provider.GetRequiredService<IWorkspaceClient>()).IsSameReferenceAs(client.Workspaces);
        await Assert.That(provider.GetRequiredService<ISandboxTemplateClient>()).IsSameReferenceAs(client.Templates);
        await Assert.That(provider.GetRequiredService<IProviderClient>()).IsSameReferenceAs(client.Providers);
        await Assert.That(provider.GetRequiredService<IConfigClient>()).IsSameReferenceAs(client.Config);
        await Assert.That(provider.GetRequiredService<IPolicyClient>()).IsSameReferenceAs(client.Policy);
        await Assert.That(provider.GetRequiredService<ISandboxWatchClient>()).IsSameReferenceAs(client.Watch);
        await Assert.That(client.Workspace).IsEqualTo("default");
    }

    [Test]
    public async Task Factory_returns_the_same_default_instance()
    {
        using var provider = Build(s => s.AddOpenShell(o => o.Address = new Uri("http://localhost:1")));
        var factory = provider.GetRequiredService<IOpenShellClientFactory>();

        await Assert.That(factory.Create()).IsSameReferenceAs(factory.Create(null));
        await Assert.That(factory.Create()).IsSameReferenceAs(provider.GetRequiredService<IOpenShellClient>());
    }

    [Test]
    public async Task Configuration_binding_reads_every_field()
    {
        var config = Config(
            ("Address", "https://gateway.example.com"),
            ("Workspace", "team-a"),
            ("Token", "tok"),
            ("CaCertificatePath", "/ca.pem"),
            ("ClientCertificatePath", "/c.pem"),
            ("ClientKeyPath", "/k.pem"),
            ("InsecureSkipVerify", "true"),
            ("AllowInsecureAuth", "true"),
            ("DefaultTimeout", "00:00:30"),
            ("Retry:MaxAttempts", "6"),
            ("Retry:InitialBackoff", "00:00:00.250"),
            ("Retry:MaxBackoff", "00:00:10"),
            ("Retry:BackoffMultiplier", "3"),
            ("Retry:Jitter", "0.5"),
            ("Retry:RetryableStatusCodes:0", "Unavailable"),
            ("Retry:RetryableStatusCodes:1", "resourceexhausted"));
        var options = new OpenShellOptions();

        OpenShellConfigurationBinder.Bind(config, options);

        await Assert.That(options.Address).IsEqualTo(new Uri("https://gateway.example.com"));
        await Assert.That(options.Workspace).IsEqualTo("team-a");
        await Assert.That(options.Token).IsEqualTo("tok");
        await Assert.That(options.CaCertificatePath).IsEqualTo("/ca.pem");
        await Assert.That(options.ClientCertificatePath).IsEqualTo("/c.pem");
        await Assert.That(options.ClientKeyPath).IsEqualTo("/k.pem");
        await Assert.That(options.InsecureSkipVerify).IsTrue();
        await Assert.That(options.AllowInsecureAuth).IsTrue();
        await Assert.That(options.DefaultTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        var policy = options.Retry!.ToPolicy();
        await Assert.That(policy.MaxAttempts).IsEqualTo(6);
        await Assert.That(policy.InitialBackoff).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(policy.MaxBackoff).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(policy.BackoffMultiplier).IsEqualTo(3d);
        await Assert.That(policy.Jitter).IsEqualTo(0.5);
        await Assert.That(policy.RetryableStatusCodes).IsEquivalentTo(new[] { StatusCode.Unavailable, StatusCode.ResourceExhausted });
    }

    [Test]
    public async Task Missing_configuration_keys_keep_defaults()
    {
        var options = new OpenShellOptions();

        OpenShellConfigurationBinder.Bind(Config(), options);

        await Assert.That(options.Address).IsNull();
        await Assert.That(options.Workspace).IsEqualTo("default");
        await Assert.That(options.Retry).IsNull();
        await Assert.That(options.InsecureSkipVerify).IsFalse();
        await Assert.That(options.DefaultTimeout).IsNull();
    }

    [Test]
    public async Task Partial_retry_section_keeps_the_other_defaults()
    {
        var options = new OpenShellOptions();
        OpenShellConfigurationBinder.Bind(Config(("Retry:MaxAttempts", "2")), options);

        var policy = options.Retry!.ToPolicy();

        await Assert.That(policy.MaxAttempts).IsEqualTo(2);
        await Assert.That(policy.InitialBackoff).IsEqualTo(RetryPolicy.Default.InitialBackoff);
        await Assert.That(policy.RetryableStatusCodes).IsEquivalentTo(RetryPolicy.Default.RetryableStatusCodes);
    }

    [Test]
    [Arguments("Address", "not a url")]
    [Arguments("InsecureSkipVerify", "maybe")]
    [Arguments("AllowInsecureAuth", "2")]
    [Arguments("DefaultTimeout", "soon")]
    [Arguments("Retry:MaxAttempts", "many")]
    [Arguments("Retry:InitialBackoff", "later")]
    [Arguments("Retry:Jitter", "lots")]
    [Arguments("Retry:RetryableStatusCodes:0", "Bogus")]
    public async Task Malformed_configuration_values_are_reported(string key, string value)
    {
        var ex = await Assert.That(() => OpenShellConfigurationBinder.Bind(Config((key, value)), new OpenShellOptions())).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Configuration_overload_registers_a_working_client()
    {
        using var provider = Build(s => s.AddOpenShell(Config(("Address", "http://localhost:1"), ("Workspace", "ws"))));

        await Assert.That(provider.GetRequiredService<IOpenShellClient>().Workspace).IsEqualTo("ws");
    }

    [Test]
    public async Task Named_registrations_are_distinct_keyed_clients()
    {
        using var provider = Build(s =>
        {
            s.AddOpenShell("prod", o => { o.Address = new Uri("http://localhost:1"); o.Workspace = "prod-ws"; });
            s.AddOpenShell("staging", Config(("Address", "http://localhost:2"), ("Workspace", "stg-ws")));
        });

        var prod = provider.GetRequiredKeyedService<IOpenShellClient>("prod");
        var staging = provider.GetRequiredKeyedService<IOpenShellClient>("staging");
        var factory = provider.GetRequiredService<IOpenShellClientFactory>();

        await Assert.That(prod).IsNotSameReferenceAs(staging);
        await Assert.That(prod.Workspace).IsEqualTo("prod-ws");
        await Assert.That(staging.Workspace).IsEqualTo("stg-ws");
        await Assert.That(factory.Create("prod")).IsSameReferenceAs(prod);
        await Assert.That(provider.GetService<IOpenShellClient>()).IsNull();
    }

    [Test]
    public async Task Service_provider_overload_can_use_other_services()
    {
        using var provider = Build(s =>
        {
            s.AddSingleton(new TokenSource("from-di"));
            s.AddOpenShell("p", (sp, o) =>
            {
                o.Address = new Uri("http://localhost:1");
                o.Auth = Auth.StaticToken(sp.GetRequiredService<TokenSource>().Value);
            });
        });

        var options = provider.GetRequiredService<IOptionsMonitor<OpenShellOptions>>().Get("p");
        using var request = new HttpRequestMessage();
        await options.Auth!.ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("from-di");
    }

    [Test]
    public async Task Code_set_auth_beats_the_configured_token()
    {
        var options = new OpenShellOptions { Address = new Uri("https://g.example.com"), Token = "ignored", Auth = Auth.StaticToken("code") };

        var client = options.ToClientOptions(null);
        using var request = new HttpRequestMessage();
        await client.Auth.ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("code");
    }

    [Test]
    public async Task Token_becomes_static_bearer_and_no_token_means_no_auth()
    {
        var withToken = new OpenShellOptions { Address = new Uri("https://g.example.com"), Token = "t" }.ToClientOptions(null);
        var without = new OpenShellOptions { Address = new Uri("https://g.example.com"), Token = " " }.ToClientOptions(null);

        await Assert.That(withToken.Auth.SendsCredentials).IsTrue();
        await Assert.That(without.Auth.SendsCredentials).IsFalse();
        await Assert.That(without.Retry).IsSameReferenceAs(RetryPolicy.Default);
    }

    [Test]
    public async Task Tls_flags_without_files_are_mapped_and_empty_tls_is_null()
    {
        var plain = new OpenShellOptions { Address = new Uri("https://g.example.com") }.ToClientOptions(null);
        var insecure = new OpenShellOptions { Address = new Uri("https://g.example.com"), InsecureSkipVerify = true }.ToClientOptions(null);
        var code = new TlsOptions { InsecureSkipVerify = true };
        var explicitTls = new OpenShellOptions { Address = new Uri("https://g.example.com"), Tls = code }.ToClientOptions(null);

        await Assert.That(plain.Tls).IsNull();
        await Assert.That(insecure.Tls!.InsecureSkipVerify).IsTrue();
        await Assert.That(explicitTls.Tls).IsSameReferenceAs(code);
    }

    [Test]
    public async Task Missing_address_fails_conversion()
    {
        await Assert.That(() => new OpenShellOptions().ToClientOptions(null)).Throws<OpenShellException>();
    }

    [Test]
    public async Task Disposing_the_provider_disposes_clients_and_blocks_new_ones()
    {
        var provider = Build(s => s.AddOpenShell(o => o.Address = new Uri("http://localhost:1")));
        var factory = provider.GetRequiredService<IOpenShellClientFactory>();
        _ = factory.Create();

        provider.Dispose();

        await Assert.That(() => factory.Create()).Throws<ObjectDisposedException>();
        await Assert.That(async () => await Task.Run(() => ((OpenShellClientFactory)factory).Dispose())).ThrowsNothing();
    }

    [Test]
    public async Task Registering_twice_does_not_duplicate_core_services()
    {
        var services = new ServiceCollection();
        services.AddOpenShell(o => o.Address = new Uri("http://localhost:1"));
        services.AddOpenShell("other", o => o.Address = new Uri("http://localhost:2"));

        await Assert.That(services.Count(d => d.ServiceType == typeof(IOpenShellClientFactory))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IOpenShellClient) && !d.IsKeyedService)).IsEqualTo(1);
    }

    [Test]
    public async Task Null_arguments_are_rejected()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddOpenShell((IConfiguration)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => services.AddOpenShell((Action<OpenShellOptions>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => services.AddOpenShell("n", (Action<IServiceProvider, OpenShellOptions>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => services.AddOpenShell(null!, o => { })).Throws<ArgumentNullException>();
        await Assert.That(() => ((IServiceCollection)null!).AddOpenShell(o => { })).Throws<ArgumentNullException>();
    }

    private sealed record TokenSource(string Value);
}
