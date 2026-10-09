// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenShell;
using OpenShell.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the OpenShell client with <see cref="IServiceCollection"/>.</summary>
public static class OpenShellServiceCollectionExtensions
{
    /// <summary>Registers the default gateway, binding <see cref="OpenShellOptions"/> from <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddOpenShell(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddOpenShell(Options.Options.DefaultName, configuration);
    }

    /// <summary>Registers a named gateway, binding <see cref="OpenShellOptions"/> from <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddOpenShell(this IServiceCollection services, string name, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AddCore(services, name);
        services.AddOptions<OpenShellOptions>(name).Configure(o => OpenShellConfigurationBinder.Bind(configuration, o));
        return services;
    }

    /// <summary>Registers the default gateway, configured in code.</summary>
    public static IServiceCollection AddOpenShell(this IServiceCollection services, Action<OpenShellOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddOpenShell(Options.Options.DefaultName, configure);
    }

    /// <summary>Registers a named gateway, configured in code.</summary>
    public static IServiceCollection AddOpenShell(this IServiceCollection services, string name, Action<OpenShellOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        AddCore(services, name);
        services.AddOptions<OpenShellOptions>(name).Configure(configure);
        return services;
    }

    /// <summary>
    /// Registers a named gateway whose options need other services, for example a token cache or secret store
    /// that supplies <see cref="OpenShellOptions.Auth"/>.
    /// </summary>
    public static IServiceCollection AddOpenShell(this IServiceCollection services, string name, Action<IServiceProvider, OpenShellOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        AddCore(services, name);
        services.AddOptions<OpenShellOptions>(name).Configure<IServiceProvider>((o, sp) => configure(sp, o));
        return services;
    }

    internal static void AddCore(IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);

        services.TryAddSingleton<OpenShellClientFactory>();
        services.TryAddSingleton<IOpenShellClientFactory>(sp => sp.GetRequiredService<OpenShellClientFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OpenShellOptions>, OpenShellOptionsValidator>());

        // Fail at startup, not on the first call, when the configuration is unusable.
        services.AddOptions<OpenShellOptions>(name).ValidateOnStart();

        if (name == Options.Options.DefaultName)
        {
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClientFactory>().Create());
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Health);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Sandboxes);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Exec);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Workspaces);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Templates);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Providers);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Config);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Policy);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOpenShellClient>().Watch);
        }
        else
        {
            services.TryAddKeyedSingleton<IOpenShellClient>(name, (sp, key) => sp.GetRequiredService<IOpenShellClientFactory>().Create((string?)key));
        }
    }
}

internal sealed class OpenShellOptionsValidator : IValidateOptions<OpenShellOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenShellOptions options)
    {
        if (options.Address is null)
        {
            return ValidateOptionsResult.Fail($"OpenShell options '{name}': Address is required.");
        }

        try
        {
            // Builds the immutable options without connecting, then runs the same checks the client runs.
            options.ToClientOptions(loggerFactory: null).Validate();
        }
        catch (OpenShellException e)
        {
            return ValidateOptionsResult.Fail($"OpenShell options '{name}': {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return ValidateOptionsResult.Fail($"OpenShell options '{name}': certificate could not be loaded ({e.GetType().Name}).");
        }

        return ValidateOptionsResult.Success;
    }
}
