<!--
SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
SPDX-License-Identifier: Apache-2.0
-->

# .NET SDK feature parity

Target: the union of the Go, Rust, TypeScript, and Python SDK surfaces. Go is the broadest, so the .NET
shape follows it, with additions from the other three. Statuses were checked against the code, not carried
over from the plan.

Legend: ✅ done, 🚧 in progress or written but not yet verified end to end, ⬜ todo.
Sources: G Go, R Rust, T TypeScript, P Python.

## Gateway API coverage

| Measure | Result |
|---|---|
| User-facing RPCs in the `OpenShell` service | ✅ 65 of 65 called by the curated client |
| Supervisor and sandbox-internal RPCs (`ConnectSupervisor`, `RelayStream`, `PeerRelay`, `Peer*`, `PushSandboxLogs`, `Report*`, `FinalizeMainProcessExit`, `GetSandboxProviderEnvironment`, `ExchangeProviderSubjectToken`, `IssueSandboxToken`, `RefreshSandboxToken`, `SubmitPolicyAnalysis`) | Excluded by design; no SDK implements them. Reachable through `OpenShellClient.Raw`. |
| Extension services (`ComputeDriver`, `CredentialDriver`, `GatewayInterceptor`, `SupervisorMiddleware`) | Excluded: they are contracts to implement, not call |

"Called" means the RPC is wired and unit tested against an in-memory gRPC invoker. See Testing for what has
and has not run against a real gateway.

## Client surface (`IOpenShellClient`)

| Area | Operations | Seen in | Status |
|---|---|---|---|
| Health | `CheckAsync`, `GetGatewayInfoAsync`, `GetCurrentUserAsync` | G R T P | ✅ |
| Workspaces | create, get, list, delete, add/remove/list members, `InWorkspace(name)` scoped view | G R P | ✅ |
| Sandboxes | create, create from template, get, list, list IDs, stop, start, delete (typed `DeletionResult`) | G R T P | ✅ |
| Sandbox waits | `WaitReadyAsync`, `WaitStoppedAsync`, `WaitDeletedAsync` with `ExpectedSandboxId` | G R T P | ✅ |
| Sandbox watch and logs | `WatchAsync`, `WatchLogsAsync`, `GetLogsAsync`, cursor resume, reconnect with backoff | G R | ✅ |
| Sandbox providers | attach, detach, list, status, readiness waits (`WaitAsync`, `WaitAllAsync`) | G R T | ✅ |
| Templates | create, get, list, delete | G R T P | ✅ |
| Exec | `RunAsync`, `StreamAsync`, `InteractiveAsync` (stdin, resize, cancel), `ExecPythonAsync` | G R T P | ✅ |
| Files | upload and download for files, streams, and directories (tar over exec) | G | ✅ |
| Services | expose, get, list, delete, service exposures at create | G R T P | ✅ |
| SSH | create and revoke session, `TunnelAsync` | G T | ✅ |
| TCP | `ForwardAsync` (duplex stream), `ListenAsync` (local listener) | G T | ✅ |
| Config | sandbox and gateway config, policy and setting updates, global scope | G T | ✅ |
| Policy | draft get, approve, reject, approve all, edit, undo, clear, history, status, revisions | G | ✅ |
| Providers | create, get, list, update, delete, ensure | G P | ✅ |
| Provider credentials | `GetCredentialsAsync` (operator-only, values redacted in `ToString`) | T P | ✅ |
| Provider profiles | list, get, import, update, lint, delete, platform view | G | ✅ |
| Provider refresh | status, configure, rotate, delete | G | ✅ |
| Rootfs staging | `BeginStagingAsync`, `StageAsync`, `RootfsDriverConfig.WithStagingToken` | none | ✅ |
| Raw access | generated gRPC client on `OpenShellClient.Raw`; `SandboxSpec.RawSpec` hook | R T | ✅ |
| Pagination | `Pager<T>` (items and pages), `ToListAsync` | G R T P | ✅ |
| Errors | `OpenShellException` with kind, gRPC status, and predicates | G R T P | ✅ |
| Error details | `ErrorInfo`, field violations, retry delay from `google.rpc` details | G R T P | ⬜ |

## Transport and auth

| Feature | Seen in | Status |
|---|---|---|
| Plaintext h2c (loopback dev) | G R T P | ✅ |
| TLS with system roots or a pinned CA | G R T P | ✅ |
| mTLS client certificate | G T P | ✅ |
| Insecure skip-verify (development only) | G R T P | ✅ |
| Static bearer token, no auth | G R T P | ✅ |
| Refreshable token (single flight, proactive, and on `Unauthenticated`) | G R P | ✅ |
| Extra headers wrapper | G | ✅ |
| Cloudflare Access header and cookie auth | G R T | ✅ |
| Edge WebSocket tunnel proxy (`EdgeTunnelProxy`) | G R | ✅ |
| Edge tunnel started automatically for `cloudflare_jwt` gateways | G | ⬜ |
| Refusal to send credentials over plaintext to a non-loopback host | T | ✅ |
| Retry policy (reads only by default, backoff with jitter, deadline aware) | G | ✅ |
| Logger option (`ILoggerFactory`) | G | 🚧 accepted and passed to the gRPC channel; the SDK itself logs little |
| Per-call deadline and cancellation | all | ✅ |

## OIDC (`OpenShell.Oidc`)

| Flow | Seen in | Status |
|---|---|---|
| Discovery and issuer validation | G R T P | ✅ |
| Client credentials | G T P | ✅ |
| Refresh-token exchange | G R P | ✅ |
| Authorization code with PKCE, loopback callback, browser launch | G | ✅ |
| Device-code flow | G | ✅ |
| Keyboard (paste) flow | G | ✅ |
| Token cache, in-memory or on disk | G | ✅ |

## Gateway configuration (`OpenShell.Gateway`)

| Feature | Seen in | Status |
|---|---|---|
| Resolve the on-disk gateway config and active gateway | G P | ✅ |
| List gateways and load metadata | G | ✅ |
| mTLS bundle and token loading | G P | ✅ |
| `GatewayClient.Connect` convenience | P | ✅ |

## Testing support

| Feature | Seen in | Status |
|---|---|---|
| Interfaces for every client area, for mocking | G | ✅ |
| In-memory fake client (`OpenShell.Sdk.Testing`) | G | 🚧 |
| Shared conformance fixtures consumed by tests (`sdk/conformance/*.json`) | G T P | ✅ |
| Unit tests on an in-memory gRPC invoker, net8.0, net10.0 and net11.0 | G T P | ✅ |
| Integration tests against a real gateway (`OPENSHELL_GATEWAY_ADDRESS`) | G | 🚧 written, skipped without a gateway, never run against a real one |
| Testcontainers fixture for a one-command local gateway | none | ⬜ |

## Packaging, docs, and CI

| Feature | Status |
|---|---|
| `OpenShell.Sdk` NuGet package (net8.0, net10.0) | ✅ packs locally |
| `OpenShell.Sdk.DependencyInjection` (`AddOpenShell`, options binding, startup validation, keyed clients, factory, health check) | ✅ |
| Fluent builder for the DI registration | ⬜ |
| OpenTelemetry `ActivitySource` and `Meter` | ⬜ |
| README and published docs page | ✅ |
| `tasks/dotnet.toml`, `sdk-dotnet` branch job, NuGet publish job | 🚧 written; no workflow has run in CI |
| .NET SDK in the CI image | ⬜ the job installs it with `actions/setup-dotnet` instead |
| Editorconfig with analyzer severities and SPDX header enforcement | ✅ |

## Known differences from the other SDKs

- Policy types in `SandboxSpec` and the policy client are the generated wire types, as in TypeScript. There is
  no policy builder because neither Go nor TypeScript has one.
- `ExecPythonAsync` sends script source (`python -c`), not a pickled callable, because .NET code cannot be pickled.
- File transfer uses tar over exec, so the sandbox image needs `tar` and `sh`. Go's file transfer is a stub, so
  there is nothing to compare against.
- `TunnelAsync` has no `port` argument; the relay ignores it.
- For `cloudflare_jwt` gateways the edge token is sent as a bearer token and as the Access header and cookie,
  which is the union of what Go, Rust, and TypeScript send. `mtls` gateways load certificates from the `mtls/`
  directory, where Go rejects them.
- Go's added, modified, and deleted watch event types are not modelled; `Sandbox.Phase` carries that state.
- Streaming calls are not retried by the retry policy; `WatchAsync` reconnects on its own.

## Out of scope

- `netstandard2.0`: needs polyfills and a different transport, and gRPC over HTTP/2 on .NET Framework is limited.
  Revisit if there is a concrete consumer.
- A .NET framework for implementing the extension services.

## .NET specifics

- Targets `net8.0` and `net10.0`; `net11.0` is listed temporarily to surface new analyzer warnings and is
  removed before the pull request if .NET 11 is not generally available. The list lives in
  `OpenShellSdkTargetFrameworks` in `Directory.Build.props`.
- Async only, with `CancellationToken` everywhere and `IAsyncEnumerable<T>` for streams and pages.
- Records and `required` or `init` members for domain types; the generated protobuf types stay reachable
  through `Raw`.
- Proto bindings are generated at build time from the repository `proto/` directory and are never committed, so
  there is no proto-check step.
