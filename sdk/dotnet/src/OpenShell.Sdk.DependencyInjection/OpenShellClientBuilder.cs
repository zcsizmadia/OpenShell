// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using OpenShell;
using OpenShell.DependencyInjection;
using OpenShell.Oidc;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Configures one gateway registration fluently. Obtain it from <c>AddOpenShellClient</c>.</summary>
public interface IOpenShellClientBuilder
{
    /// <summary>The service collection being configured.</summary>
    IServiceCollection Services { get; }

    /// <summary>The registration name; empty for the default gateway.</summary>
    string Name { get; }

    /// <summary>Adjusts the options in code.</summary>
    IOpenShellClientBuilder Configure(Action<OpenShellOptions> configure);

    /// <summary>Adjusts the options with access to other registered services, such as a token cache or secret store.</summary>
    IOpenShellClientBuilder Configure(Action<IServiceProvider, OpenShellOptions> configure);
}

internal sealed class OpenShellClientBuilder(IServiceCollection services, string name) : IOpenShellClientBuilder
{
    public IServiceCollection Services => services;

    public string Name => name;

    public IOpenShellClientBuilder Configure(Action<OpenShellOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<OpenShellOptions>(name).Configure(configure);
        return this;
    }

    public IOpenShellClientBuilder Configure(Action<IServiceProvider, OpenShellOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<OpenShellOptions>(name).Configure<IServiceProvider>((options, provider) => configure(provider, options));
        return this;
    }
}

/// <summary>Entry point and fluent settings for the builder-style registration.</summary>
public static class OpenShellClientBuilderExtensions
{
    /// <summary>
    /// Registers a gateway and returns a builder for it. Omit <paramref name="name"/> for the default gateway; a name
    /// registers a keyed <see cref="IOpenShellClient"/> and makes it available from <see cref="IOpenShellClientFactory"/>.
    /// </summary>
    public static IOpenShellClientBuilder AddOpenShellClient(this IServiceCollection services, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var key = name ?? Options.Options.DefaultName;
        OpenShellServiceCollectionExtensions.AddCore(services, key);
        return new OpenShellClientBuilder(services, key);
    }

    /// <summary>Binds settings from configuration, for example the <c>OpenShell</c> section.</summary>
    public static IOpenShellClientBuilder BindConfiguration(this IOpenShellClientBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return builder.Configure(options => OpenShellConfigurationBinder.Bind(configuration, options));
    }

    /// <summary>Sets the gateway address.</summary>
    public static IOpenShellClientBuilder WithAddress(this IOpenShellClientBuilder builder, Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return builder.Configure(options => options.Address = address);
    }

    /// <summary>Sets the gateway address from an absolute URL.</summary>
    public static IOpenShellClientBuilder WithAddress(this IOpenShellClientBuilder builder, string address) =>
        builder.WithAddress(new Uri(address, UriKind.Absolute));

    /// <summary>Sets the workspace used when a call does not name one.</summary>
    public static IOpenShellClientBuilder WithWorkspace(this IOpenShellClientBuilder builder, string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        return builder.Configure(options => options.Workspace = workspace);
    }

    /// <summary>Authenticates with a fixed bearer token. Prefer <see cref="WithClientCredentials(IOpenShellClientBuilder, Func{IServiceProvider, OidcOptions})"/> for tokens that expire.</summary>
    public static IOpenShellClientBuilder WithBearerToken(this IOpenShellClientBuilder builder, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return builder.Configure(options => options.Token = token);
    }

    /// <summary>Authenticates with a provider built in code.</summary>
    public static IOpenShellClientBuilder WithAuth(this IOpenShellClientBuilder builder, IAuthProvider auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return builder.Configure(options => options.Auth = auth);
    }

    /// <summary>Authenticates with a provider that needs other registered services.</summary>
    public static IOpenShellClientBuilder WithAuth(this IOpenShellClientBuilder builder, Func<IServiceProvider, IAuthProvider> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return builder.Configure((provider, options) => options.Auth = create(provider));
    }

    /// <summary>
    /// Authenticates with the OAuth2 client-credentials grant, renewing the token before it expires. The provider is a
    /// container singleton, so it is disposed with the container.
    /// </summary>
    public static IOpenShellClientBuilder WithClientCredentials(this IOpenShellClientBuilder builder, Func<IServiceProvider, OidcOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = $"OpenShell.ClientCredentials:{builder.Name}";
        builder.Services.AddKeyedSingleton<IAuthProvider>(key, (provider, _) => ClientCredentialsAuth.Create(options(provider)));
        return builder.Configure((provider, settings) => settings.Auth = provider.GetRequiredKeyedService<IAuthProvider>(key));
    }

    /// <summary>Authenticates with client credentials, reading the secret from <paramref name="secretProvider"/> on every renewal.</summary>
    public static IOpenShellClientBuilder WithClientCredentials(
        this IOpenShellClientBuilder builder,
        string issuer,
        string clientId,
        Func<CancellationToken, ValueTask<string>> secretProvider,
        params string[] scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(secretProvider);
        return builder.WithClientCredentials(_ => new OidcOptions
        {
            Issuer = issuer,
            ClientId = clientId,
            ClientSecretProvider = secretProvider,
            Scopes = scopes.Length > 0 ? scopes : null,
        });
    }

    /// <summary>Trusts the CA certificates in a PEM file instead of the system roots.</summary>
    public static IOpenShellClientBuilder WithCaCertificate(this IOpenShellClientBuilder builder, string pemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        return builder.Configure(options => options.CaCertificatePath = pemPath);
    }

    /// <summary>Authenticates with a client certificate (mTLS). Omit <paramref name="keyPath"/> when the key is in the certificate file.</summary>
    public static IOpenShellClientBuilder WithClientCertificate(this IOpenShellClientBuilder builder, string certificatePath, string? keyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        return builder.Configure(options =>
        {
            options.ClientCertificatePath = certificatePath;
            options.ClientKeyPath = keyPath;
        });
    }

    /// <summary>Uses TLS settings built in code. They take precedence over the certificate paths.</summary>
    public static IOpenShellClientBuilder WithTls(this IOpenShellClientBuilder builder, TlsOptions tls)
    {
        ArgumentNullException.ThrowIfNull(tls);
        return builder.Configure(options => options.Tls = tls);
    }

    /// <summary>Disables server certificate validation. Development only.</summary>
    public static IOpenShellClientBuilder WithInsecureTls(this IOpenShellClientBuilder builder) =>
        builder.Configure(options => options.InsecureSkipVerify = true);

    /// <summary>Permits credentials over plaintext <c>http://</c> to a non-loopback host. They cross the network in the clear.</summary>
    public static IOpenShellClientBuilder AllowInsecureAuth(this IOpenShellClientBuilder builder) =>
        builder.Configure(options => options.AllowInsecureAuth = true);

    /// <summary>Applies a deadline to unary calls that do not set their own.</summary>
    public static IOpenShellClientBuilder WithTimeout(this IOpenShellClientBuilder builder, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return builder.Configure(options => options.DefaultTimeout = timeout);
    }

    /// <summary>Adjusts the retry settings; unset values keep their defaults.</summary>
    public static IOpenShellClientBuilder WithRetry(this IOpenShellClientBuilder builder, Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.Configure(options =>
        {
            options.Retry ??= new RetryOptions();
            configure(options.Retry);
        });
    }

    /// <summary>Turns retries off.</summary>
    public static IOpenShellClientBuilder WithoutRetry(this IOpenShellClientBuilder builder) =>
        builder.Configure(options => options.Retry = new RetryOptions { MaxAttempts = 1 });
}
