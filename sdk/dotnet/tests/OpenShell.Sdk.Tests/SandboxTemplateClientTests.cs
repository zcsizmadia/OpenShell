// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Datamodel = Openshell.Datamodel.V1;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SandboxTemplateClientTests
{
    private static Wire.SandboxWorkloadTemplate WireTemplate(string name) =>
        new() { Metadata = new Datamodel.ObjectMeta { Id = "t-" + name, Name = name, Workspace = "default" } };

    [Test]
    public async Task Create_maps_full_template_both_ways()
    {
        Wire.CreateSandboxTemplateRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.CreateSandboxTemplateRequest, Wire.SandboxTemplateResponse>("CreateSandboxTemplate", r =>
        {
            seen = r;
            var echoed = r.Template.Clone();
            echoed.Metadata.Id = "t-1";
            echoed.Metadata.Workspace = "ws1";
            echoed.Metadata.ResourceVersion = 7;
            echoed.Metadata.CreatedTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1));
            return new Wire.SandboxTemplateResponse { Template = echoed };
        });
        using var client = ClientTests.NewClient(invoker, "ws1");

        var created = await client.Templates.CreateAsync(
            new SandboxWorkloadTemplate
            {
                Name = "py",
                Labels = new Dictionary<string, string> { ["l"] = "1" },
                Annotations = new Dictionary<string, string> { ["a"] = "2" },
                Image = "python:3.12",
                Environment = new Dictionary<string, string> { ["E"] = "v" },
                Resources = new WorkloadResources { Cpu = "2", Memory = "2Gi", Gpu = new GpuRequirements(2) },
                DriverConfigJson = """{"docker":{"privileged":false}}""",
                Startup = new StartupServiceLevel(TimeSpan.FromSeconds(30), 4),
            },
            "req");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(seen.RequestId).IsEqualTo("req");
        await Assert.That(seen.Template.Metadata.Name).IsEqualTo("py");
        await Assert.That(seen.Template.Metadata.Labels["l"]).IsEqualTo("1");
        await Assert.That(seen.Template.Metadata.Annotations["a"]).IsEqualTo("2");
        var workload = seen.Template.Spec.Workload;
        await Assert.That(workload.Image).IsEqualTo("python:3.12");
        await Assert.That(workload.Environment["E"]).IsEqualTo("v");
        await Assert.That(workload.Resources.Cpu).IsEqualTo("2");
        await Assert.That(workload.Resources.Memory).IsEqualTo("2Gi");
        await Assert.That(workload.Resources.Gpu.Count).IsEqualTo(2u);
        await Assert.That(seen.Template.Spec.DriverConfig.Fields["docker"].StructValue.Fields["privileged"].BoolValue).IsFalse();
        await Assert.That(seen.Template.Spec.DesiredServiceLevel.Startup.ReadyWithin.Seconds).IsEqualTo(30L);
        await Assert.That(seen.Template.Spec.DesiredServiceLevel.Startup.MaxBurst).IsEqualTo(4u);

        await Assert.That(created.Name).IsEqualTo("py");
        await Assert.That(created.Id).IsEqualTo("t-1");
        await Assert.That(created.Workspace).IsEqualTo("ws1");
        await Assert.That(created.ResourceVersion).IsEqualTo(7ul);
        await Assert.That(created.CreatedAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(1));
        await Assert.That(created.Labels!["l"]).IsEqualTo("1");
        await Assert.That(created.Annotations!["a"]).IsEqualTo("2");
        await Assert.That(created.Image).IsEqualTo("python:3.12");
        await Assert.That(created.Environment!["E"]).IsEqualTo("v");
        await Assert.That(created.Resources!.Cpu).IsEqualTo("2");
        await Assert.That(created.Resources.Memory).IsEqualTo("2Gi");
        await Assert.That(created.Resources.Gpu!.Count).IsEqualTo(2u);
        await Assert.That(created.DriverConfigJson).Contains("privileged");
        await Assert.That(created.Startup!.ReadyWithin).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(created.Startup.MaxBurst).IsEqualTo(4u);
    }

    [Test]
    public async Task Minimal_template_omits_optional_wire_fields()
    {
        var wire = SandboxTemplateClient.ToWire(new SandboxWorkloadTemplate { Name = "min" });

        await Assert.That(wire.Spec.Workload.Resources).IsNull();
        await Assert.That(wire.Spec.DriverConfig).IsNull();
        await Assert.That(wire.Spec.DesiredServiceLevel).IsNull();
        await Assert.That(wire.Spec.Workload.Image).IsEqualTo("");
    }

    [Test]
    public async Task Gpu_without_count_and_startup_without_deadline()
    {
        var wire = SandboxTemplateClient.ToWire(new SandboxWorkloadTemplate
        {
            Name = "g",
            Resources = new WorkloadResources { Gpu = new GpuRequirements() },
            Startup = new StartupServiceLevel(MaxBurst: 2),
        });

        await Assert.That(wire.Spec.Workload.Resources.Gpu).IsNotNull();
        await Assert.That(wire.Spec.Workload.Resources.Gpu.HasCount).IsFalse();
        await Assert.That(wire.Spec.DesiredServiceLevel.Startup.ReadyWithin).IsNull();

        var back = SandboxTemplateClient.ToDomain(wire);
        await Assert.That(back.Resources!.Gpu!.Count).IsNull();
        await Assert.That(back.Resources.Cpu).IsNull();
        await Assert.That(back.Resources.Memory).IsNull();
        await Assert.That(back.Startup!.ReadyWithin).IsNull();
        await Assert.That(back.Startup.MaxBurst).IsEqualTo(2u);
    }

    [Test]
    public async Task Resources_without_gpu_map_back_without_gpu()
    {
        var wire = SandboxTemplateClient.ToWire(new SandboxWorkloadTemplate { Name = "r", Resources = new WorkloadResources { Cpu = "1" } });

        var back = SandboxTemplateClient.ToDomain(wire);

        await Assert.That(back.Resources!.Gpu).IsNull();
        await Assert.That(back.Resources.Cpu).IsEqualTo("1");
    }

    [Test]
    public async Task Template_with_missing_pieces_has_safe_defaults()
    {
        var back = SandboxTemplateClient.ToDomain(new Wire.SandboxWorkloadTemplate());

        await Assert.That(back.Name).IsEqualTo("");
        await Assert.That(back.Id).IsNull();
        await Assert.That(back.Workspace).IsNull();
        await Assert.That(back.Image).IsNull();
        await Assert.That(back.Resources).IsNull();
        await Assert.That(back.DriverConfigJson).IsNull();
        await Assert.That(back.Startup).IsNull();
        await Assert.That(back.CreatedAt).IsNull();
        await Assert.That(back.Labels!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Get_sends_scope_and_name()
    {
        Wire.GetSandboxTemplateRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxTemplateRequest, Wire.SandboxTemplateResponse>("GetSandboxTemplate", r =>
        {
            seen = r;
            return new Wire.SandboxTemplateResponse { Template = WireTemplate(r.Name) };
        });
        using var client = ClientTests.NewClient(invoker, "ws9");

        var t = await client.Templates.GetAsync("py");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws9");
        await Assert.That(seen.Name).IsEqualTo("py");
        await Assert.That(t.Name).IsEqualTo("py");
    }

    [Test]
    public async Task List_pages_and_supports_all_workspaces()
    {
        var requests = new List<Wire.ListSandboxTemplatesRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxTemplatesRequest, Wire.ListSandboxTemplatesResponse>("ListSandboxTemplates", r =>
        {
            requests.Add(r);
            var reply = new Wire.ListSandboxTemplatesResponse();
            if (r.PageToken == "")
            {
                reply.Templates.Add(WireTemplate("a"));
                reply.NextPageToken = "p2";
            }
            else
            {
                reply.Templates.Add(WireTemplate("b"));
            }

            return reply;
        });
        using var client = ClientTests.NewClient(invoker);

        var all = await client.Templates.List(new ListOptions { AllWorkspaces = true, PageSize = 3, LabelSelector = "k=v" }).ToListAsync();

        await Assert.That(all.Select(t => t.Name)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(requests[0].WorkspaceScope.SelectionCase).IsEqualTo(Datamodel.WorkspaceSelector.SelectionOneofCase.AllWorkspaces);
        await Assert.That(requests[0].PageSize).IsEqualTo(3);
        await Assert.That(requests[0].LabelSelector).IsEqualTo("k=v");
        await Assert.That(requests[1].PageToken).IsEqualTo("p2");
    }

    [Test]
    public async Task List_defaults_to_the_client_workspace()
    {
        Wire.ListSandboxTemplatesRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ListSandboxTemplatesRequest, Wire.ListSandboxTemplatesResponse>("ListSandboxTemplates", r =>
        {
            seen = r;
            return new Wire.ListSandboxTemplatesResponse();
        });
        using var client = ClientTests.NewClient(invoker, "ws2");

        _ = await client.Templates.List().ToListAsync();

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws2");
    }

    [Test]
    public async Task Delete_returns_typed_outcome()
    {
        Wire.DeleteSandboxTemplateRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.DeleteSandboxTemplateRequest, Wire.DeleteSandboxTemplateResponse>("DeleteSandboxTemplate", r =>
        {
            seen = r;
            return new Wire.DeleteSandboxTemplateResponse { Outcome = Wire.DeletionOutcome.AlreadyAbsent };
        });
        using var client = ClientTests.NewClient(invoker, "ws3");

        var withOptions = await client.Templates.DeleteAsync("t", new DeleteOptions { AllowMissing = true, RequestId = "r" });

        await Assert.That(withOptions.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(withOptions.IsComplete).IsTrue();
        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws3");
        await Assert.That(seen.AllowMissing).IsTrue();
        await Assert.That(seen.RequestId).IsEqualTo("r");

        await client.Templates.DeleteAsync("t");
        await Assert.That(seen.AllowMissing).IsFalse();
    }

    [Test]
    public async Task Argument_validation()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());
        var templates = client.Templates;

        await Assert.That(async () => await templates.CreateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await templates.CreateAsync(new SandboxWorkloadTemplate { Name = " " })).Throws<ArgumentException>();
        await Assert.That(async () => await templates.GetAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await templates.DeleteAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Gateway_errors_are_mapped()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxTemplateRequest, Wire.SandboxTemplateResponse>(
            "GetSandboxTemplate", StatusCode.NotFound, "missing");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Templates.GetAsync("x")).Throws<OpenShellException>();
        await Assert.That(ex!.IsNotFound).IsTrue();
    }

    [Test]
    public async Task InWorkspace_rescopes_templates()
    {
        Wire.GetSandboxTemplateRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxTemplateRequest, Wire.SandboxTemplateResponse>("GetSandboxTemplate", r =>
        {
            seen = r;
            return new Wire.SandboxTemplateResponse { Template = WireTemplate("a") };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.InWorkspace("team-b").Templates.GetAsync("a");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("team-b");
    }
}
