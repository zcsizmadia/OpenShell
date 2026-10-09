// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace OpenShell.Gateway;

/// <summary>Overrides for <see cref="GatewayLocator"/>, mainly for tests and containers.</summary>
public sealed record GatewayLocatorOptions
{
    /// <summary>Replaces the per-user <c>openshell</c> configuration directory.</summary>
    public string? UserConfigDirectory { get; init; }

    /// <summary>Replaces the system-wide configuration directory (<c>/etc/openshell</c>). Null disables it on Windows.</summary>
    public string? SystemConfigDirectory { get; init; }

    /// <summary>Environment lookup; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }
}

/// <summary>Finds gateways registered on disk by the OpenShell CLI.</summary>
public sealed class GatewayLocator
{
    internal const string MetadataFile = "metadata.json";
    private const string AppName = "openshell";
    private const string GatewaySubdir = "gateways";
    private const string ActiveGatewayFile = "active_gateway";
    private const string ActiveGatewayVariable = "OPENSHELL_GATEWAY";

    private readonly GatewayLocatorOptions _options;
    private readonly Func<string, string?> _env;

    /// <summary>Creates a locator with the default OS layout.</summary>
    public GatewayLocator(GatewayLocatorOptions? options = null)
    {
        _options = options ?? new GatewayLocatorOptions();
        _env = _options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The per-user configuration directory (<c>$XDG_CONFIG_HOME/openshell</c> or the OS default).</summary>
    public string UserConfigDirectory
    {
        get
        {
            if (_options.UserConfigDirectory is { Length: > 0 } overridden)
            {
                return overridden;
            }

            if (_env("XDG_CONFIG_HOME") is { Length: > 0 } xdg)
            {
                if (!Path.IsPathRooted(xdg))
                {
                    throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "XDG_CONFIG_HOME must be an absolute path");
                }

                return Path.Combine(xdg, AppName);
            }

            if (OperatingSystem.IsWindows() && _env("APPDATA") is { Length: > 0 } appData)
            {
                return Path.Combine(appData, AppName);
            }

            var home = _env(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (string.IsNullOrEmpty(home))
            {
                throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "cannot determine home directory");
            }

            return Path.Combine(home, ".config", AppName);
        }
    }

    /// <summary>The system-wide configuration directory, or null when none applies.</summary>
    public string? SystemConfigDirectory =>
        _options.SystemConfigDirectory is { Length: > 0 } overridden
            ? overridden
            : OperatingSystem.IsWindows() ? null : "/etc/openshell";

    /// <summary>Loads a gateway by name; null selects the active gateway.</summary>
    public GatewayConfig Load(string? name = null)
    {
        name ??= ResolveActiveName();
        var (directory, source) = ResolveDirectory(name);
        return ParseMetadata(name, directory, source);
    }

    /// <summary>Lists registered gateways, user registrations shadowing system ones.</summary>
    public IReadOnlyList<GatewayInfo> List()
    {
        var active = TryResolveActiveName();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GatewayInfo>();

        foreach (var name in ListDirectories(UserConfigDirectory))
        {
            seen.Add(name);
            result.Add(new GatewayInfo(name, name == active, GatewayConfigSource.User));
        }

        if (SystemConfigDirectory is { } system)
        {
            foreach (var name in ListDirectories(system))
            {
                if (seen.Add(name))
                {
                    result.Add(new GatewayInfo(name, name == active, GatewayConfigSource.System));
                }
            }
        }

        return result;
    }

    /// <summary>Resolves the active gateway name from <c>$OPENSHELL_GATEWAY</c> or the <c>active_gateway</c> file.</summary>
    public string ResolveActiveName()
    {
        if (_env(ActiveGatewayVariable) is { Length: > 0 } fromEnv)
        {
            ValidateName(fromEnv);
            return fromEnv;
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(UserConfigDirectory, ActiveGatewayFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OpenShellException(OpenShellErrorKind.NotFound, "no active gateway configured", e);
        }

        var name = text.Trim();
        if (name.Length == 0)
        {
            throw new OpenShellException(OpenShellErrorKind.NotFound, "no active gateway configured: active_gateway file is empty");
        }

        ValidateName(name);
        return name;
    }

    /// <summary>Validates a gateway name: ASCII letters, digits, <c>-</c> and <c>_</c> only.</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw Invalid("gateway name must not be empty");
        }

        foreach (var c in name)
        {
            if (c is '/' or '\\')
            {
                throw Invalid("gateway name must not contain path separators");
            }

            if (c == '.')
            {
                throw Invalid("gateway name must not contain dots");
            }

            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                throw Invalid("gateway name contains an invalid character");
            }
        }
    }

    private string? TryResolveActiveName()
    {
        try
        {
            return ResolveActiveName();
        }
        catch (OpenShellException)
        {
            return null;
        }
    }

    private (string Directory, GatewayConfigSource Source) ResolveDirectory(string name)
    {
        ValidateName(name);

        var user = Path.Combine(UserConfigDirectory, GatewaySubdir, name);
        if (Directory.Exists(user))
        {
            return (user, GatewayConfigSource.User);
        }

        if (SystemConfigDirectory is { } system)
        {
            var sys = Path.Combine(system, GatewaySubdir, name);
            if (Directory.Exists(sys))
            {
                return (sys, GatewayConfigSource.System);
            }
        }

        throw new OpenShellException(OpenShellErrorKind.NotFound, $"gateway '{name}' not found");
    }

    private static GatewayConfig ParseMetadata(string name, string directory, GatewayConfigSource source)
    {
        var path = Path.Combine(directory, MetadataFile);
        GatewayMetadataDto? meta;
        try
        {
            meta = JsonSerializer.Deserialize(File.ReadAllText(path), SdkJsonContext.Default.GatewayMetadataDto);
        }
        catch (JsonException e)
        {
            throw Invalid($"invalid JSON in {MetadataFile} for gateway '{name}'", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Invalid($"cannot read {MetadataFile} for gateway '{name}'", e);
        }

        if (meta is null || string.IsNullOrEmpty(meta.GatewayEndpoint))
        {
            throw Invalid($"missing gateway_endpoint in {MetadataFile} for gateway '{name}'");
        }

        if (!Uri.TryCreate(meta.GatewayEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw Invalid($"gateway_endpoint for gateway '{name}' must be an absolute http:// or https:// URL");
        }

        return new GatewayConfig
        {
            Name = name,
            Endpoint = endpoint,
            AuthMode = ParseAuthMode(meta.AuthMode),
            Source = source,
            Directory = directory,
            OidcIssuer = NullIfEmpty(meta.OidcIssuer),
            OidcClientId = NullIfEmpty(meta.OidcClientId),
            OidcAudience = NullIfEmpty(meta.OidcAudience),
            OidcScopes = NullIfEmpty(meta.OidcScopes),
        };
    }

    private static GatewayAuthMode ParseAuthMode(string? raw) => raw switch
    {
        null or "" or "none" => GatewayAuthMode.None,
        "plaintext" => GatewayAuthMode.Plaintext,
        "cloudflare_jwt" => GatewayAuthMode.CloudflareJwt,
        "oidc" => GatewayAuthMode.Oidc,
        "mtls" => GatewayAuthMode.Mtls,
        _ => throw Invalid("unsupported auth_mode in gateway metadata"),
    };

    private static List<string> ListDirectories(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, GatewaySubdir);
        var names = new List<string>();
        if (!Directory.Exists(root))
        {
            return names;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            try
            {
                ValidateName(name);
                names.Add(name);
            }
            catch (OpenShellException)
            {
                // Skip directories that are not valid gateway registrations.
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static OpenShellException Invalid(string message, Exception? inner = null) =>
        new(OpenShellErrorKind.InvalidConfig, message, inner);
}
