<!--
SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
SPDX-License-Identifier: Apache-2.0
-->

# .NET SDK feature parity plan

Target: the **union** of the Go, Rust, TypeScript, and Python SDK surfaces.
Go is the broadest (14 sub-clients plus `oidc`, `gateway`, `edge`, `fake`
packages), so the .NET shape follows it, with additions taken from the other
three. Legend: G = Go, R = Rust, T = TypeScript, P = Python.

Status values: `todo`, `wip`, `done`.

## Client surface (curated, `OpenShellClient`)

| Area | Operations | Seen in | Status |
|---|---|---|---|
| Health | `Check`, `GetGatewayInfo`, `GetCurrentUser` | G R T P | todo |
| Workspaces | create/get/list/listAll/delete, add/remove/list members, `Workspace(name)` scoped client | G R P | todo |
| Sandboxes | create, createFromTemplate, get, list/listAll, all-workspaces listing, list IDs, stop, start, delete (typed `DeletionResult`, `allowMissing`) | G R T P | todo |
| Sandbox waits | `WaitReady`, `WaitStopped`, `WaitDeleted` (with `expectedSandboxId`) | G R T P | todo |
| Sandbox watch | `Watch` status/log/event stream with cursor resume | G | todo |
| Sandbox logs | `GetLogs`, `WatchLogs` | G R | todo |
| Sandbox providers | attach/detach (resource-version guarded), list/listAll, status | G T | todo |
| Templates | create/get/list/listAll/delete, all-workspaces listing | G R T P | todo |
| Exec | `Run` (buffered), `Stream`, `Interactive` (stdin, resize, cancel), `ExecPython` helper, tty, workdir, env, timeout | G R T P | todo |
| Files | `Upload`, `Download` (tar over exec) | G | todo |
| Services | expose/get/list/listAll/delete, service exposures at create, auth modes | G R T P | todo |
| SSH | create/revoke session, `Tunnel`, host-key validation | G T | todo |
| TCP | `Forward` (duplex stream), `Listen` (local listener) | G T | todo |
| Config | sandbox config, gateway config, `Update` (policy/settings, global vs sandbox scope) | G T | todo |
| Policy | draft get/approve/reject/approveAll/edit/undo/clear/history, status, revisions list | G | todo |
| Providers | create/get/list/listAll/update/delete/`Ensure` | G P | todo |
| Provider credentials | `GetCredentials` (operator-only) | T P | todo |
| Provider profiles | list/get/import/update/lint/delete | G | todo |
| Provider refresh | status/configure/rotate/delete | G | todo |
| Provider readiness | `WaitForProvider`, status helpers | R | todo |
| Raw access | generated `OpenShell.OpenShellClient` escape hatch, `rawSpec`-style overrides | R T | todo |
| Pagination | `Pager<T>` (`IAsyncEnumerable` of pages and items), `ListAll` | G R T P | todo |
| Errors | typed `OpenShellException` with code, `ErrorInfo`, field violations, retry delay, `IsNotFound` etc. predicates | G R T P | todo |

## Transport and auth

| Feature | Seen in | Status |
|---|---|---|
| Plaintext h2c (loopback dev) | G R T P | todo |
| TLS with system roots or pinned CA | G R T P | todo |
| mTLS client certificate | G T P (not R) | todo |
| Insecure skip-verify (dev only) | G R T P | todo |
| Static bearer token, `NoAuth` | G R T P | todo |
| Refreshable token source (single-flight, proactive and on `Unauthenticated`) | G R P | todo |
| Extra headers wrapper | G | todo |
| Cloudflare Access header/cookie auth | G R T | todo |
| Edge WebSocket tunnel proxy | G R | todo |
| Refusal to send tokens over non-loopback plaintext | T | todo |
| Retry policy | G | todo |
| Pluggable `ILogger` | G | todo |
| Per-call options (deadline, cancellation, headers) | all | todo |

## OIDC package (`OpenShell.Oidc`)

| Flow | Seen in | Status |
|---|---|---|
| Discovery and validation | G R T P | todo |
| Client credentials (`ClientCredentialsAuth`) | G T P | todo |
| Refresh-token exchange | R G P | todo |
| Authorization code + PKCE with loopback callback and browser launch | G | todo |
| Device-code flow | G | todo |
| Keyboard (paste) flow | G | todo |
| Token cache / in-memory mode | G | todo |

## Gateway package (`OpenShell.Gateway`)

| Feature | Seen in | Status |
|---|---|---|
| Resolve on-disk gateway config (`metadata.json`, active gateway) | G P | todo |
| `ListGateways`, `LoadConfig` | G | todo |
| mTLS bundle and token loading | G P | todo |
| `FromActiveCluster` convenience | P | todo |

## Testing support

| Feature | Seen in | Status |
|---|---|---|
| In-memory fake client implementing the full interface | G | todo |
| `IOpenShellClient` interfaces for mocking | G | todo |
| Shared conformance fixtures (`sdk/conformance/*.json`) | G T P | todo |
| Integration test lane against a real gateway | G | todo |

## .NET specifics

- Targets `net8.0` and `net10.0`; AOT/trimmer compatible; no reflection-based
  serialization in the public path.
- Async only, `CancellationToken` everywhere, `IAsyncEnumerable<T>` for streams
  and pages, `ValueTask` where results are commonly synchronous.
- Records and `required`/`init` members for domain types; domain types do not
  expose generated protobuf types (they remain reachable through `Raw`).
- `AddOpenShell()` for `Microsoft.Extensions.DependencyInjection` in an
  optional companion package so the core stays dependency-light.
- Proto bindings are generated at build time from the repository `proto/`
  directory and are never committed, so no `proto:check` step is needed.
