<!--
SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
SPDX-License-Identifier: Apache-2.0
-->

# OpenShell.Sdk

.NET client for the [OpenShell](https://github.com/NVIDIA/OpenShell) gateway. It targets `net8.0` and `net10.0`, is trimming and AOT compatible, and exposes a curated, async API over the gateway's gRPC contract.

Use the SDK and gateway from the same OpenShell release when possible. The generated protobuf bindings come from the `proto/` directory of that release, and curated methods stay compatible while the underlying RPC contracts do.

## Install

The package is published to the NVIDIA GitHub Packages NuGet feed. Add the feed to a `nuget.config` next to your solution:

```xml
<configuration>
  <packageSources>
    <add key="nvidia" value="https://nuget.pkg.github.com/NVIDIA/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <nvidia>
      <add key="Username" value="YOUR_GITHUB_USER" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </nvidia>
  </packageSourceCredentials>
</configuration>
```

Use a GitHub token with `read:packages`, then add the package:

```shell
dotnet add package OpenShell.Sdk
```

## Quick start

Create one `OpenShellClient` and reuse it. The client connects lazily, so call `Health.CheckAsync()` when startup must verify that the gateway is reachable.

```csharp
using OpenShell;

using var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://gateway.example.com"),
    Auth = Auth.StaticToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!),
});

var health = await client.Health.CheckAsync();
Console.WriteLine($"{health.Status}: {health.Version}");

var sandbox = await client.Sandboxes.CreateAsync(
    "sdk-example",
    new SandboxSpec { Image = "registry.example.com/team/python-agent:1.0" });

await client.Sandboxes.WaitReadyAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });

var result = await client.Exec.RunAsync(
    sandbox.Name,
    ["python", "-c", "print('hello from OpenShell')"]);
Console.WriteLine(result.StdoutText);

var deletion = await client.Sandboxes.DeleteAsync(sandbox.Name);
if (deletion.Outcome == DeletionOutcome.Accepted)
{
    await client.Sandboxes.WaitDeletedAsync(
        sandbox.Name,
        new WaitOptions { ExpectedSandboxId = deletion.ResourceId });
}
```

`DeleteAsync` returns a typed `DeletionResult`, not a boolean. `Accepted` means cleanup is still pending, `Unspecified` does not establish completion, and `IsComplete` is true only for `Completed` and `AlreadyAbsent`. A missing target is an error unless you pass `new DeleteOptions { AllowMissing = true }`.

## Connect and authenticate

`Auth` builds the authentication providers:

| Provider | Use |
| --- | --- |
| `Auth.None` | No credentials, for example a local plaintext gateway. |
| `Auth.StaticToken(token)` | A fixed OIDC bearer token. |
| `Auth.Bearer(tokenSource)` | A token fetched per request from a cache you manage. |
| `Auth.CloudflareAccess(edgeToken, inner)` | Cloudflare Access header and cookie, optionally on top of another provider. |
| `Auth.WithHeaders(inner, headers)` | Extra fixed headers on top of another provider. |
| `RefreshableToken.AsAuthProvider()` | Single-flight renewal before expiry and after an `Unauthenticated` response. |

For long-running service automation, use the OIDC client-credentials provider from `OpenShell.Oidc`. It discovers and validates the issuer, keeps secrets and tokens in memory, and renews the token before expiry:

```csharp
using OpenShell;
using OpenShell.Oidc;

using var auth = ClientCredentialsAuth.Create(new OidcOptions
{
    Issuer = "https://idp.example.com/realms/openshell",
    ClientId = "openshell-service",
    ClientSecretProvider = ct => ValueTask.FromResult(
        Environment.GetEnvironmentVariable("OPENSHELL_OIDC_CLIENT_SECRET")!),
    Audience = "openshell-gateway",
});

using var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://gateway.example.com"),
    Auth = auth,
});
```

`OidcLogin` also provides the interactive authorization-code flow with PKCE, the device-code flow, a paste-the-code keyboard flow, and refresh-token exchange.

### Mutual TLS

The default local gateway requires a client certificate. Pass it, and optionally a private CA, through `TlsOptions`:

```csharp
using System.Security.Cryptography.X509Certificates;
using OpenShell;

var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://localhost:8080"),
    Tls = new TlsOptions
    {
        CaCertificates = new X509Certificate2Collection(X509Certificate2.CreateFromPemFile("ca.crt")),
        ClientCertificate = X509Certificate2.CreateFromPemFile("client.crt", "client.key"),
    },
});
```

`TlsOptions.InsecureSkipVerify` disables server certificate validation. Use it only for local development.

### Use a registered gateway

`GatewayClient.Connect(name)` builds a client from a gateway the OpenShell CLI registered on disk, including its authentication mode, mTLS bundle, and cached tokens. Pass no name to use the active gateway. It renews an expired OIDC token with the stored refresh token and writes the rotation back so the CLI stays in sync.

```csharp
using OpenShell.Gateway;

using var client = GatewayClient.Connect();
```

### Token transport safety

The SDK refuses to send credentials over plaintext `http://` to a non-loopback host and throws an `OpenShellException` with `OpenShellErrorKind.InvalidConfig`. Set `AllowInsecureAuth` only when you accept that the credential crosses the network in clear text. Edge tokens must use cookie-safe JWT characters. A client certificate must include its private key.

For a gateway behind Cloudflare Access, `EdgeTunnelProxy.Start(gatewayUrl, edgeToken)` opens a local loopback proxy that tunnels connections over an authenticated WebSocket. Point `OpenShellClientOptions.Address` at `proxy.LocalUri`.

## Workspaces

Operations run in `OpenShellClientOptions.DefaultWorkspace`, which defaults to `default`. `InWorkspace` returns a view bound to another workspace that shares the same connection. Disposing a view does not close the shared connection.

```csharp
var team = client.InWorkspace("team-a");
var sandboxes = await team.Sandboxes.List().ToListAsync();

var workspace = await client.Workspaces.GetAsync("team-a");
```

## Pagination

List methods return a lazy `Pager<T>`. Enumerate it for items, or call `PagesAsync()` for pages. Set `ListOptions.AllWorkspaces` to list across workspaces where the resource supports it.

```csharp
await foreach (var sandbox in client.Sandboxes.List(new ListOptions { PageSize = 50 }))
{
    Console.WriteLine($"{sandbox.Name}: {sandbox.Phase}");
}
```

## Errors

Every operation throws `OpenShellException`. Switch on `Kind`, which covers `NotFound`, `AlreadyExists`, `PermissionDenied`, `Unauthenticated`, `InvalidArgument`, `Unavailable`, `DeadlineExceeded`, `Cancelled`, `Unimplemented`, `Conflict`, `Internal`, `Auth`, `Connect`, `Tls`, and `InvalidConfig`. The `IsNotFound`, `IsAlreadyExists`, `IsConflict`, and `IsTransient` shortcuts cover the common cases, and `GrpcStatusCode` exposes the underlying gRPC status. Cancelling a call through its `CancellationToken` throws `OperationCanceledException`.

```csharp
try
{
    await client.Sandboxes.GetAsync("missing");
}
catch (OpenShellException e) when (e.IsNotFound)
{
    // Handle the missing sandbox.
}
```

Waits that exceed their timeout throw `OpenShellErrorKind.DeadlineExceeded`.

## Streaming exec, watch, and logs

`Exec.StreamAsync` yields output as it arrives and always ends with an `ExecExit` event, so a failing command cannot be missed. `Exec.InteractiveAsync` opens an interactive session with stdin, resize, and cancel.

```csharp
await foreach (var e in client.Exec.StreamAsync("sdk-example", ["sh", "-c", "echo hi; exit 3"]))
{
    switch (e)
    {
        case ExecOutput o: Console.Write(o.Text); break;
        case ExecExit x: Console.WriteLine($"exit {x.ExitCode}"); break;
    }
}
```

`Watch.WatchAsync` streams sandbox status, log, platform, and draft-policy events. It reconnects with backoff on `Unavailable` and resumes from the last cursor. Pass `WatchOptions.ResumeAfterCursor` to resume across process restarts.

```csharp
var options = new WatchOptions { FollowLogs = true };
await foreach (var e in client.Watch.WatchAsync("sdk-example", options))
{
    if (e is LogReceived log)
    {
        Console.WriteLine(log.Line.Message);
    }
}
```

## Providers

`client.Providers` creates, updates, lists, and deletes providers, ensures one exists, and retrieves runtime credentials. `client.Providers.Profiles` and `Refresh` manage provider profiles and credential refresh. `client.SandboxProviders` attaches and detaches providers and waits for readiness.

`GetCredentialsAsync` requires an operator client certificate and an explicit workspace. Keep the returned values out of logs. `Provider`, `ProviderCredentialValue`, and `RefreshConfig` override `ToString` so secret values never print.

```csharp
var credentials = await operatorClient.Providers.GetCredentialsAsync(
    "my-graph",
    ["MS_GRAPH_ACCESS_TOKEN"],
    new ProviderCredentialsOptions { Workspace = "production" });
```

## Other APIs

| Property | Operations |
| --- | --- |
| `Services` | Expose, get, list, and delete sandbox services. |
| `Ssh` | Create and revoke SSH sessions, and open a tunnel. |
| `Tcp` | `ForwardAsync` returns a duplex `Stream`, and `ListenAsync` binds a local port that forwards to the sandbox. |
| `Files` | Upload and download files and directories using tar over exec. The sandbox image needs `tar` and `sh`. |
| `Config` | Read and update sandbox and gateway configuration, policy, and settings. |
| `Policy` | Review draft policy chunks and list policy revisions. |
| `Templates` | Create, get, list, and delete reusable workload templates. |
| `Workspaces` | Manage workspaces and their members. |
| `Rootfs` | Stage a local rootfs tar for drivers that boot from one. It works only when the client shares the gateway's filesystem. |

## Raw client

`OpenShellClient.Raw` exposes the generated gRPC client for RPCs the curated surface does not cover. Raw calls use protobuf wire types and do not map errors to `OpenShellException`.

## Testing

An in-memory fake client for unit tests is coming in `OpenShell.Sdk.Testing`. Until then, program against `IOpenShellClient` and its per-area interfaces, such as `ISandboxClient`, to substitute your own test double.

## Build from source

Protobuf bindings are generated at build time from the repository `proto/` directory and are never committed. From the repository root:

```shell
mise run sdk:dotnet:ci
```

The task runs the format check, builds both target frameworks, runs the TUnit tests with a coverage summary, and packs the NuGet package. It needs the .NET 8 and .NET 10 SDKs.
