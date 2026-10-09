// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ServiceClientTests
{
    private static Wire.ServiceEndpointResponse Endpoint(string name, string url = "https://svc.example.com") =>
        new()
        {
            Url = url,
            Endpoint = new Wire.ServiceEndpoint
            {
                Name = name,
                Sandbox = "sb",
                SandboxId = "sid",
                TargetPort = 8080,
                Domain = true,
                AuthorizationMode = Wire.ServiceAuthorizationMode.BearerPassthrough,
                Metadata = new Openshell.Datamodel.V1.ObjectMeta
                {
                    ResourceVersion = 3,
                    CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1)),
                },
            },
        };

    [Test]
    public async Task Expose_sends_request_and_maps_endpoint()
    {
        Wire.ExposeServiceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ExposeServiceRequest, Wire.ServiceEndpointResponse>("ExposeService", r =>
        {
            seen = r;
            return Endpoint("web");
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var endpoint = await client.Services.ExposeAsync(
            "sb",
            "web",
            8080,
            new ExposeServiceOptions { Domain = true, AuthorizationMode = ServiceAuthorizationMode.BearerPassthrough, RequestId = "rid" });

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.Name).IsEqualTo("web");
        await Assert.That(seen.TargetPort).IsEqualTo(8080u);
        await Assert.That(seen.Domain).IsTrue();
        await Assert.That(seen.AuthorizationMode).IsEqualTo(Wire.ServiceAuthorizationMode.BearerPassthrough);
        await Assert.That(seen.RequestId).IsEqualTo("rid");
        await Assert.That(endpoint.Name).IsEqualTo("web");
        await Assert.That(endpoint.SandboxId).IsEqualTo("sid");
        await Assert.That(endpoint.TargetPort).IsEqualTo(8080u);
        await Assert.That(endpoint.Domain).IsTrue();
        await Assert.That(endpoint.Url).IsEqualTo("https://svc.example.com");
        await Assert.That(endpoint.AuthorizationMode).IsEqualTo(ServiceAuthorizationMode.BearerPassthrough);
        await Assert.That(endpoint.ResourceVersion).IsEqualTo(3ul);
        await Assert.That(endpoint.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
    }

    [Test]
    public async Task Expose_defaults_to_strip_and_allows_unnamed_service()
    {
        Wire.ExposeServiceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ExposeServiceRequest, Wire.ServiceEndpointResponse>("ExposeService", r =>
        {
            seen = r;
            return Endpoint("");
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Services.ExposeAsync("sb", "", 80);

        await Assert.That(seen!.AuthorizationMode).IsEqualTo(Wire.ServiceAuthorizationMode.Strip);
        await Assert.That(seen.Name).IsEqualTo("");
    }

    [Test]
    public async Task Endpoint_without_metadata_has_safe_defaults()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRequest, Wire.ServiceEndpointResponse>(
            "GetService", _ => new Wire.ServiceEndpointResponse { Url = "u" });
        using var client = ClientTests.NewClient(invoker);

        var endpoint = await client.Services.GetAsync("sb", "x");

        await Assert.That(endpoint.Name).IsEqualTo("");
        await Assert.That(endpoint.AuthorizationMode).IsEqualTo(ServiceAuthorizationMode.Unspecified);
        await Assert.That(endpoint.CreatedAt).IsNull();
        await Assert.That(endpoint.Url).IsEqualTo("u");
    }

    [Test]
    public async Task Get_sends_selector()
    {
        Wire.GetServiceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRequest, Wire.ServiceEndpointResponse>("GetService", r =>
        {
            seen = r;
            return Endpoint("web");
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Services.GetAsync("sb", "web");

        await Assert.That(seen!.Sandbox).IsEqualTo("sb");
        await Assert.That(seen.Name).IsEqualTo("web");
    }

    [Test]
    public async Task List_pages_lazily()
    {
        var requests = new List<Wire.ListServicesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListServicesRequest, Wire.ListServicesResponse>("ListServices", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListServicesResponse();
            if (r.PageToken == "")
            {
                reply.Services.Add(Endpoint("a"));
                reply.NextPageToken = "p2";
            }
            else
            {
                reply.Services.Add(Endpoint("b"));
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Services.List("sb", new ListOptions { PageSize = 1, AllWorkspaces = true }).ToListAsync();

        await Assert.That(all.Select(s => s.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].PageSize).IsEqualTo(1);
        await Assert.That(requests[0].Sandbox).IsEqualTo("sb");
        await Assert.That(requests[0].WorkspaceScope.SelectionCase).IsEqualTo(Openshell.Datamodel.V1.WorkspaceSelector.SelectionOneofCase.AllWorkspaces);
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task Delete_returns_typed_outcome()
    {
        Wire.DeleteServiceRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteServiceRequest, Wire.DeleteServiceResponse>("DeleteService", r =>
        {
            seen = r;
            return new Wire.DeleteServiceResponse { Outcome = Wire.DeletionOutcome.AlreadyAbsent };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Services.DeleteAsync("sb", "web", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(seen!.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Services.ExposeAsync("", "n", 1)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.ExposeAsync("sb", null!, 1)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Services.GetAsync("", "n")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.GetAsync("sb", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Services.List("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.DeleteAsync("", "n")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.DeleteAsync("sb", null!)).Throws<ArgumentNullException>();
    }
}
