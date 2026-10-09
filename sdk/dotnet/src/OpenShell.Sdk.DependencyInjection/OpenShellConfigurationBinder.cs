// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace OpenShell.DependencyInjection;

/// <summary>
/// Reads <see cref="OpenShellOptions"/> from configuration by hand. The reflection-based configuration binder is
/// not trimming or AOT safe, and the option set is small enough that explicit parsing is simpler than a generator.
/// </summary>
internal static class OpenShellConfigurationBinder
{
    public static void Bind(IConfiguration configuration, OpenShellOptions options)
    {
        if (Read(configuration, "Address") is { } address)
        {
            options.Address = Uri.TryCreate(address, UriKind.Absolute, out var uri)
                ? uri
                : throw Invalid("Address", "must be an absolute URL");
        }

        options.Workspace = Read(configuration, "Workspace") ?? options.Workspace;
        options.Token = Read(configuration, "Token") ?? options.Token;
        options.CaCertificatePath = Read(configuration, "CaCertificatePath") ?? options.CaCertificatePath;
        options.ClientCertificatePath = Read(configuration, "ClientCertificatePath") ?? options.ClientCertificatePath;
        options.ClientKeyPath = Read(configuration, "ClientKeyPath") ?? options.ClientKeyPath;
        options.InsecureSkipVerify = ReadBool(configuration, "InsecureSkipVerify") ?? options.InsecureSkipVerify;
        options.AllowInsecureAuth = ReadBool(configuration, "AllowInsecureAuth") ?? options.AllowInsecureAuth;
        options.DefaultTimeout = ReadTimeSpan(configuration, "DefaultTimeout") ?? options.DefaultTimeout;

        var retry = configuration.GetSection("Retry");
        if (retry.Exists())
        {
            options.Retry = BindRetry(retry);
        }
    }

    private static RetryOptions BindRetry(IConfigurationSection section)
    {
        var retry = new RetryOptions
        {
            MaxAttempts = ReadInt(section, "MaxAttempts"),
            InitialBackoff = ReadTimeSpan(section, "InitialBackoff"),
            MaxBackoff = ReadTimeSpan(section, "MaxBackoff"),
            BackoffMultiplier = ReadDouble(section, "BackoffMultiplier"),
            Jitter = ReadDouble(section, "Jitter"),
        };

        var codes = section.GetSection("RetryableStatusCodes").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        if (codes.Length > 0)
        {
            retry.RetryableStatusCodes = codes.Select(c => Enum.TryParse<StatusCode>(c, ignoreCase: true, out var code)
                ? code
                : throw Invalid("Retry:RetryableStatusCodes", $"'{c}' is not a gRPC status code")).ToArray();
        }

        return retry;
    }

    private static string? Read(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : null;

    private static bool? ReadBool(IConfiguration configuration, string key) =>
        Read(configuration, key) is { } value
            ? bool.TryParse(value, out var parsed) ? parsed : throw Invalid(key, "must be true or false")
            : null;

    private static int? ReadInt(IConfiguration configuration, string key) =>
        Read(configuration, key) is { } value
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid(key, "must be an integer")
            : null;

    private static double? ReadDouble(IConfiguration configuration, string key) =>
        Read(configuration, key) is { } value
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid(key, "must be a number")
            : null;

    private static TimeSpan? ReadTimeSpan(IConfiguration configuration, string key) =>
        Read(configuration, key) is { } value
            ? TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid(key, "must be a time span such as 00:00:30")
            : null;

    private static OpenShellException Invalid(string key, string problem) =>
        new(OpenShellErrorKind.InvalidConfig, $"OpenShell configuration '{key}' {problem}.");
}
