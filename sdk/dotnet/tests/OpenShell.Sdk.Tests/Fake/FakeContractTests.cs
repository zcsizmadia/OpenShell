// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using OpenShell.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>
/// Runs the same scenarios against the fake client and against the real client talking to a scripted gateway,
/// and requires the same exception type and <see cref="OpenShellErrorKind"/> from both.
/// </summary>
public class FakeContractTests
{
    private sealed record Scenario(
        string Name,
        Func<IOpenShellClient, Task> Act,
        Type Expected,
        OpenShellErrorKind? Kind,
        Action<FakeCallInvoker> ScriptReal,
        Func<FakeOpenShellClient, Task>? SetupFake = null);

    private static IEnumerable<Wire.ExecSandboxEvent> MissingSandboxStream()
    {
        throw new RpcException(new Status(StatusCode.NotFound, "sandbox not found"));
#pragma warning disable CS0162 // The iterator shape is what makes the failure surface on the first read.
        yield break;
#pragma warning restore CS0162
    }

    private static readonly Scenario[] Scenarios =
    [
        new(
            "get a missing sandbox",
            c => c.Sandboxes.GetAsync("missing").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "not found")),
        new(
            "create a duplicate sandbox",
            c => c.Sandboxes.CreateAsync("dup", FakeCoreTests.Spec()).AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.AlreadyExists,
            real => real.Throws<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", StatusCode.AlreadyExists, "exists"),
            async fake => await fake.Sandboxes.CreateAsync("dup", FakeCoreTests.Spec())),
        new(
            "stop a sandbox in an error phase",
            c => c.Sandboxes.StopAsync("broken").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.Conflict,
            real => real.Throws<Wire.StopSandboxRequest, Wire.SandboxResponse>("StopSandbox", StatusCode.FailedPrecondition, "bad phase"),
            async fake =>
            {
                await fake.Sandboxes.CreateAsync("broken", FakeCoreTests.Spec());
                fake.Gateway.SetPhase("broken", SandboxPhase.Error);
            }),
        new(
            "delete a missing sandbox",
            c => c.Sandboxes.DeleteAsync("missing").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Throws<Wire.DeleteSandboxRequest, Wire.DeleteSandboxResponse>("DeleteSandbox", StatusCode.NotFound, "not found")),
        new(
            "run a command in a missing sandbox",
            c => c.Exec.RunAsync("missing", ["true"]).AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => MissingSandboxStream())),
        new(
            "get a missing provider",
            c => c.Providers.GetAsync("missing").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Throws<Wire.GetProviderRequest, Wire.ProviderResponse>("GetProvider", StatusCode.NotFound, "not found")),
        new(
            "get a missing template",
            c => c.Templates.GetAsync("missing").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Throws<Wire.GetSandboxTemplateRequest, Wire.SandboxTemplateResponse>("GetSandboxTemplate", StatusCode.NotFound, "not found")),
        new(
            "create a workspace with an invalid name",
            c => c.Workspaces.CreateAsync("Bad_Name").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.InvalidArgument,
            real => real.Throws<Wire.CreateWorkspaceRequest, Wire.CreateWorkspaceResponse>("CreateWorkspace", StatusCode.InvalidArgument, "bad name")),
        new(
            "set a policy against a stale resource version",
            c => c.Config.SetPolicyAsync("s", FakePolicyData.Policy(), new SetPolicyOptions { ExpectedResourceVersion = 9_999 }).AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.Conflict,
            real => real.Throws<Wire.UpdateConfigRequest, Wire.UpdateConfigResponse>("UpdateConfig", StatusCode.Aborted, "version conflict"),
            async fake => await fake.Sandboxes.CreateAsync("s", new SandboxSpec { Image = "i", Policy = FakePolicyData.Policy() })),
        new(
            "attach an unknown provider",
            c => c.SandboxProviders.AttachAsync("s", "missing").AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.NotFound,
            real => real.Throws<Wire.AttachSandboxProviderRequest, Wire.AttachSandboxProviderResponse>("AttachSandboxProvider", StatusCode.NotFound, "no provider"),
            async fake => await fake.Sandboxes.CreateAsync("s", FakeCoreTests.Spec())),
        new(
            "expose a service on an invalid port",
            c => c.Services.ExposeAsync("s", "web", 0).AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.InvalidArgument,
            real => real.Throws<Wire.ExposeServiceRequest, Wire.ServiceEndpointResponse>("ExposeService", StatusCode.InvalidArgument, "bad port"),
            async fake => await fake.Sandboxes.CreateAsync("s", FakeCoreTests.Spec())),
        new(
            "list sandboxes with a bad page token",
            c => c.Sandboxes.List(new ListOptions { PageToken = "garbage" }).ToListAsync().AsTask(),
            typeof(OpenShellException),
            OpenShellErrorKind.InvalidArgument,
            real => real.Throws<Wire.ListSandboxesRequest, Wire.ListSandboxesResponse>("ListSandboxes", StatusCode.InvalidArgument, "bad token")),
        new(
            "get a sandbox with a blank name",
            c => c.Sandboxes.GetAsync(" ").AsTask(),
            typeof(ArgumentException),
            null,
            _ => { }),
        new(
            "run a command with no arguments",
            c => c.Exec.RunAsync("s", []).AsTask(),
            typeof(ArgumentException),
            null,
            _ => { }),
        new(
            "create a sandbox without a spec",
            c => c.Sandboxes.CreateAsync("s", null!).AsTask(),
            typeof(ArgumentNullException),
            null,
            _ => { }),
        new(
            "forward an out-of-range port",
            c => c.Tcp.ForwardAsync("s", 70_000).AsTask(),
            typeof(ArgumentOutOfRangeException),
            null,
            _ => { }),
    ];

    private static async Task<(Type? Type, OpenShellErrorKind? Kind)> OutcomeAsync(Func<Task> act)
    {
        try
        {
            await act();
            return (null, null);
        }
        catch (OpenShellException e)
        {
            return (e.GetType(), e.Kind);
        }
        catch (Exception e)
        {
            return (e.GetType(), null);
        }
    }

    [Test]
    public async Task The_fake_and_the_real_client_raise_the_same_errors()
    {
        var mismatches = new List<string>();
        foreach (var scenario in Scenarios)
        {
            using var fake = FakeCoreTests.New();
            if (scenario.SetupFake is not null)
            {
                await scenario.SetupFake(fake);
            }

            var invoker = new FakeCallInvoker();
            scenario.ScriptReal(invoker);
            using var real = OpenShellClient.ForTesting(
                new OpenShellClientOptions { Address = new Uri("http://localhost") },
                invoker);

            var fromFake = await OutcomeAsync(() => scenario.Act(fake));
            var fromReal = await OutcomeAsync(() => scenario.Act(real));

            var expected = (scenario.Expected, scenario.Kind);
            if (fromFake != expected || fromReal != expected)
            {
                mismatches.Add($"{scenario.Name}: expected {expected}, fake gave {fromFake}, real gave {fromReal}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Both_clients_agree_on_the_happy_path_shape()
    {
        using var fake = FakeCoreTests.New();
        var invoker = new FakeCallInvoker()
            .Unary<Wire.CreateSandboxRequest, Wire.SandboxResponse>("CreateSandbox", request => new Wire.SandboxResponse
            {
                Sandbox = new Wire.Sandbox
                {
                    Metadata = new Openshell.Datamodel.V1.ObjectMeta { Id = "id-1", Name = request.Name, Workspace = "default" },
                    Status = new Wire.SandboxStatus { Phase = Wire.SandboxPhase.Provisioning },
                },
            });
        using var real = OpenShellClient.ForTesting(new OpenShellClientOptions { Address = new Uri("http://localhost") }, invoker);

        var fromFake = await fake.Sandboxes.CreateAsync("same", FakeCoreTests.Spec());
        var fromReal = await real.Sandboxes.CreateAsync("same", FakeCoreTests.Spec());

        await Assert.That(fromFake.Name).IsEqualTo(fromReal.Name);
        await Assert.That(fromFake.Phase).IsEqualTo(fromReal.Phase);
        await Assert.That(fromFake.Workspace).IsEqualTo(fromReal.Workspace);
    }
}
