// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.DependencyInjection.Tests;

public class ValidationTests
{
    private static OptionsValidationException? Resolve(Action<OpenShellOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddOpenShell(configure);
        using var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IOptionsMonitor<OpenShellOptions>>().CurrentValue;
            return null;
        }
        catch (OptionsValidationException e)
        {
            return e;
        }
    }

    [Test]
    public async Task Valid_options_pass()
    {
        await Assert.That(Resolve(o => o.Address = new Uri("http://localhost:1"))).IsNull();
    }

    [Test]
    public async Task Missing_address_fails_validation_with_a_clear_message()
    {
        var failure = Resolve(_ => { });

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("Address is required");
    }

    [Test]
    public async Task Credentials_over_plaintext_to_a_remote_host_fail_at_startup()
    {
        var failure = Resolve(o => { o.Address = new Uri("http://gateway.example.com"); o.Token = "secret-token"; });

        await Assert.That(failure!.Message).Contains("plaintext");
        await Assert.That(failure.Message).DoesNotContain("secret-token");
    }

    [Test]
    public async Task Missing_certificate_file_fails_validation_without_leaking_paths_or_contents()
    {
        var failure = Resolve(o => { o.Address = new Uri("https://g.example.com"); o.CaCertificatePath = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".pem"); });

        await Assert.That(failure!.Message).Contains("certificate could not be loaded");
    }

    [Test]
    public async Task Pem_certificates_load_into_tls_options()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openshell-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certPath = Path.Combine(dir, "cert.pem");
            var keyPath = Path.Combine(dir, "key.pem");
            await File.WriteAllTextAsync(certPath, cert.ExportCertificatePem());
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());

            var client = new OpenShellOptions
            {
                Address = new Uri("https://g.example.com"),
                CaCertificatePath = certPath,
                ClientCertificatePath = certPath,
                ClientKeyPath = keyPath,
            }.ToClientOptions(null);

            await Assert.That(client.Tls!.CaCertificates!.Count).IsEqualTo(1);
            await Assert.That(client.Tls.ClientCertificate!.HasPrivateKey).IsTrue();
            client.Tls.ClientCertificate.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Certificate_with_key_in_the_same_file_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openshell-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var combined = Path.Combine(dir, "both.pem");
            await File.WriteAllTextAsync(combined, cert.ExportCertificatePem() + Environment.NewLine + key.ExportPkcs8PrivateKeyPem());

            var client = new OpenShellOptions { Address = new Uri("https://g.example.com"), ClientCertificatePath = combined }.ToClientOptions(null);

            await Assert.That(client.Tls!.ClientCertificate!.HasPrivateKey).IsTrue();
            await Assert.That(client.Tls.CaCertificates).IsNull();
            client.Tls.ClientCertificate.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class HealthCheckTests
{
    private static HealthCheckContext Context(HealthStatus failure = HealthStatus.Unhealthy) =>
        new() { Registration = new HealthCheckRegistration("openshell", _ => null!, failure, null) };

    private static OpenShellClient Client(FakeCallInvoker invoker) =>
        OpenShellClient.ForTesting(new OpenShellClientOptions { Address = new Uri("http://localhost") }, invoker);

    [Test]
    [Arguments(Wire.ServiceStatus.Healthy, HealthStatus.Healthy)]
    [Arguments(Wire.ServiceStatus.Degraded, HealthStatus.Degraded)]
    [Arguments(Wire.ServiceStatus.Unhealthy, HealthStatus.Unhealthy)]
    [Arguments(Wire.ServiceStatus.Unspecified, HealthStatus.Unhealthy)]
    public async Task Gateway_status_maps_to_health_status(Wire.ServiceStatus wire, HealthStatus expected)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse { Status = wire, Version = "1.2" });
        using var client = Client(invoker);

        var result = await new OpenShellHealthCheck(client).CheckHealthAsync(Context());

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Data["version"]).IsEqualTo("1.2");
    }

    [Test]
    public async Task Gateway_failure_uses_the_configured_failure_status()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "down");
        using var client = Client(invoker);

        var result = await new OpenShellHealthCheck(client).CheckHealthAsync(Context(HealthStatus.Degraded));

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
        await Assert.That(result.Exception).IsTypeOf<OpenShellException>();
    }

    [Test]
    public async Task Registered_check_resolves_the_named_gateway_and_reports_unreachable_as_unhealthy()
    {
        var services = new ServiceCollection();
        services.AddOpenShell("prod", o => { o.Address = new Uri("http://127.0.0.1:1"); o.Retry = new RetryOptions { MaxAttempts = 1 }; o.DefaultTimeout = TimeSpan.FromSeconds(5); });
        services.AddHealthChecks().AddOpenShellHealthCheck("gateway", "prod", tags: ["ready"]);
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Single();
        var check = registration.Factory(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        await Assert.That(registration.Name).IsEqualTo("gateway");
        await Assert.That(registration.Tags).Contains("ready");
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    [Test]
    public async Task Null_builder_is_rejected()
    {
        await Assert.That(() => ((IHealthChecksBuilder)null!).AddOpenShellHealthCheck()).Throws<ArgumentNullException>();
    }
}
