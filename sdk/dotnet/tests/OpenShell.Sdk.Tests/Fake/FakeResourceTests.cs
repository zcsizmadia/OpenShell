// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Testing;

namespace OpenShell.Tests;

public class FakeWorkspaceTests
{
    private static FakeOpenShellClient New(FakeGatewayOptions? options = null) => FakeCoreTests.New(options);

    [Test]
    public async Task Default_workspace_exists_and_new_ones_can_be_created()
    {
        using var client = New();

        var created = await client.Workspaces.CreateAsync("team-a", new Dictionary<string, string> { ["owner"] = "me" });
        var names = (await client.Workspaces.List().ToListAsync()).Select(w => w.Name);

        await Assert.That(names).IsEquivalentTo(new[] { "default", "team-a" });
        await Assert.That(created.Phase).IsEqualTo(WorkspacePhase.Active);
        await Assert.That(created.Labels["owner"]).IsEqualTo("me");
        await Assert.That(created.CreatedAt).IsNotNull();
        await Assert.That((await client.Workspaces.GetAsync("team-a")).Id).IsEqualTo(created.Id);
    }

    [Test]
    public async Task Create_validates_names_and_rejects_duplicates()
    {
        using var client = New();
        await client.Workspaces.CreateAsync("ok");

        var duplicate = await Assert.That(async () => await client.Workspaces.CreateAsync("ok")).Throws<OpenShellException>();
        var upper = await Assert.That(async () => await client.Workspaces.CreateAsync("Bad_Name")).Throws<OpenShellException>();
        var longName = await Assert.That(async () => await client.Workspaces.CreateAsync(new string('a', 64))).Throws<OpenShellException>();

        await Assert.That(duplicate!.Kind).IsEqualTo(OpenShellErrorKind.AlreadyExists);
        await Assert.That(upper!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(longName!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(async () => await client.Workspaces.CreateAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_with_a_request_id_is_idempotent()
    {
        using var client = New();

        var first = await client.Workspaces.CreateAsync("a", requestId: "r");
        var replay = await client.Workspaces.CreateAsync("b", requestId: "r");

        await Assert.That(replay.Id).IsEqualTo(first.Id);
        await Assert.That(async () => await client.Workspaces.GetAsync("b")).Throws<OpenShellException>();
    }

    [Test]
    public async Task Get_list_and_pagination()
    {
        using var client = New();
        foreach (var name in new[] { "c", "b", "a" })
        {
            await client.Workspaces.CreateAsync(name);
        }

        var pages = new List<int>();
        await foreach (var page in client.Workspaces.List(new ListOptions { PageSize = 3 }).PagesAsync())
        {
            pages.Add(page.Items.Count);
        }

        var missing = await Assert.That(async () => await client.Workspaces.GetAsync("zzz")).Throws<OpenShellException>();
        await Assert.That(pages).IsEquivalentTo(new[] { 3, 1 });
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Workspaces.GetAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Delete_removes_the_workspace_and_everything_in_it()
    {
        using var client = New();
        await client.Workspaces.CreateAsync("w");
        var scoped = client.InWorkspace("w");
        await scoped.Sandboxes.CreateAsync("s", FakeCoreTests.Spec());
        await scoped.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "t" });
        await scoped.Providers.CreateAsync(new Provider { Name = "p", Type = "x" });
        await scoped.Providers.Profiles.ImportAsync([new ProfileImportItem(new ProviderProfile { Id = "prof", DisplayName = "P" })]);

        var result = await client.Workspaces.DeleteAsync("w");

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(async () => await client.Workspaces.GetAsync("w")).Throws<OpenShellException>();
        await Assert.That(client.Gateway.Calls.Count).IsGreaterThan(0);
        var recreated = client.InWorkspace("w");
        await Assert.That(await recreated.Sandboxes.List().ToListAsync()).Count().IsEqualTo(0);
        await Assert.That(await recreated.Templates.List().ToListAsync()).Count().IsEqualTo(0);
        await Assert.That(await recreated.Providers.List().ToListAsync()).Count().IsEqualTo(0);
        await Assert.That(await recreated.Providers.Profiles.List().ToListAsync()).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Delete_of_a_missing_workspace_honours_allow_missing()
    {
        using var client = New();

        var tolerated = await client.Workspaces.DeleteAsync("nope", new DeleteOptions { AllowMissing = true });
        var ex = await Assert.That(async () => await client.Workspaces.DeleteAsync("nope")).Throws<OpenShellException>();

        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Workspaces.DeleteAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Members_can_be_added_updated_listed_and_removed()
    {
        using var client = New();

        var added = await client.Workspaces.AddMemberAsync("default", "alice", WorkspaceRole.User);
        var promoted = await client.Workspaces.AddMemberAsync("default", "alice", WorkspaceRole.Admin);
        await client.Workspaces.AddMemberAsync("default", "bob", WorkspaceRole.User);
        var members = await client.Workspaces.ListMembers("default", new ListOptions { PageSize = 1 }).ToListAsync();
        var removed = await client.Workspaces.RemoveMemberAsync("default", "alice");

        await Assert.That(promoted.Id).IsEqualTo(added.Id);
        await Assert.That(promoted.Role).IsEqualTo(WorkspaceRole.Admin);
        await Assert.That(promoted.CreatedAt).IsEqualTo(added.CreatedAt);
        await Assert.That(members.Select(m => m.PrincipalSubject)).IsEquivalentTo(new[] { "alice", "bob" });
        await Assert.That(removed.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That((await client.Workspaces.ListMembers("default").ToListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Membership_errors_are_typed()
    {
        using var client = New();

        var noWorkspace = await Assert.That(async () => await client.Workspaces.AddMemberAsync("nope", "a", WorkspaceRole.User)).Throws<OpenShellException>();
        var noRole = await Assert.That(async () => await client.Workspaces.AddMemberAsync("default", "a", WorkspaceRole.Unspecified)).Throws<OpenShellException>();
        var missing = await Assert.That(async () => await client.Workspaces.RemoveMemberAsync("default", "ghost")).Throws<OpenShellException>();
        var tolerated = await client.Workspaces.RemoveMemberAsync("default", "ghost", new DeleteOptions { AllowMissing = true });

        await Assert.That(noWorkspace!.IsNotFound).IsTrue();
        await Assert.That(noRole!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(async () => await client.Workspaces.AddMemberAsync(" ", "a", WorkspaceRole.User)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Workspaces.AddMemberAsync("default", " ", WorkspaceRole.User)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Workspaces.RemoveMemberAsync(" ", "a")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Workspaces.RemoveMemberAsync("default", " ")).Throws<ArgumentException>();
        await Assert.That(() => client.Workspaces.ListMembers(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Workspaces.ListMembers("nope").ToListAsync()).Throws<OpenShellException>();
    }
}

public class FakeTemplateTests
{
    private static FakeOpenShellClient New() => FakeCoreTests.New();

    [Test]
    public async Task Templates_are_created_read_listed_and_deleted()
    {
        using var client = New();

        var created = await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "t", Image = "i:1", Labels = new Dictionary<string, string> { ["k"] = "v" } });
        await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "u" });
        await client.InWorkspace("other").Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "o" });
        var fetched = await client.Templates.GetAsync("t");
        var here = await client.Templates.List().ToListAsync();
        var everywhere = await client.Templates.List(new ListOptions { AllWorkspaces = true, PageSize = 2 }).ToListAsync();
        var labelled = await client.Templates.List(new ListOptions { LabelSelector = "k=v" }).ToListAsync();
        var deleted = await client.Templates.DeleteAsync("t");

        await Assert.That(created.Id).IsNotNull();
        await Assert.That(created.Workspace).IsEqualTo("default");
        await Assert.That(created.CreatedAt).IsNotNull();
        await Assert.That(fetched.Image).IsEqualTo("i:1");
        await Assert.That(here.Select(t => t.Name)).IsEquivalentTo(new[] { "t", "u" });
        await Assert.That(everywhere.Count).IsEqualTo(3);
        await Assert.That(labelled.Select(t => t.Name)).IsEquivalentTo(new[] { "t" });
        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
    }

    [Test]
    public async Task Template_errors_are_typed()
    {
        using var client = New();
        await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "t" });

        var duplicate = await Assert.That(async () => await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "t" })).Throws<OpenShellException>();
        var missing = await Assert.That(async () => await client.Templates.GetAsync("nope")).Throws<OpenShellException>();
        var absent = await Assert.That(async () => await client.Templates.DeleteAsync("nope")).Throws<OpenShellException>();
        var tolerated = await client.Templates.DeleteAsync("nope", new DeleteOptions { AllowMissing = true });

        await Assert.That(duplicate!.Kind).IsEqualTo(OpenShellErrorKind.AlreadyExists);
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(absent!.IsNotFound).IsTrue();
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(async () => await client.Templates.CreateAsync(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = " " })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Templates.GetAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Templates.DeleteAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_with_a_request_id_is_idempotent()
    {
        using var client = New();

        var first = await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "a" }, "r");
        var replay = await client.Templates.CreateAsync(new SandboxWorkloadTemplate { Name = "b" }, "r");

        await Assert.That(replay.Id).IsEqualTo(first.Id);
    }
}

public class FakeServiceTests
{
    private static async Task<FakeOpenShellClient> WithSandboxAsync()
    {
        var client = FakeCoreTests.New();
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        return client;
    }

    [Test]
    public async Task Services_are_exposed_listed_read_and_deleted()
    {
        using var client = await WithSandboxAsync();

        var web = await client.Services.ExposeAsync("a", "web", 8080, new ExposeServiceOptions { AuthorizationMode = ServiceAuthorizationMode.BearerPassthrough });
        var unnamed = await client.Services.ExposeAsync("a", "", 3000, new ExposeServiceOptions { Domain = true, AuthorizationMode = ServiceAuthorizationMode.Unspecified });
        var fetched = await client.Services.GetAsync("a", "web");
        var listed = await client.Services.List("a", new ListOptions { PageSize = 1 }).ToListAsync();
        var sandbox = await client.Sandboxes.GetAsync("a");
        var deleted = await client.Services.DeleteAsync("a", "web");

        await Assert.That(web.Url).Contains("web");
        await Assert.That(web.AuthorizationMode).IsEqualTo(ServiceAuthorizationMode.BearerPassthrough);
        await Assert.That(unnamed.Domain).IsTrue();
        await Assert.That(unnamed.Url).StartsWith("https://a.fake.example.com");
        await Assert.That(unnamed.AuthorizationMode).IsEqualTo(ServiceAuthorizationMode.Strip);
        await Assert.That(web.SandboxId.Length).IsGreaterThan(0);
        await Assert.That(fetched.TargetPort).IsEqualTo(8080u);
        await Assert.That(listed.Select(s => s.Name)).IsEquivalentTo(new[] { "", "web" });
        await Assert.That(sandbox.ServiceUrls.Count).IsEqualTo(2);
        await Assert.That(deleted.Outcome).IsEqualTo(DeletionOutcome.Completed);
    }

    [Test]
    public async Task Service_errors_are_typed()
    {
        using var client = await WithSandboxAsync();
        await client.Services.ExposeAsync("a", "web", 80);

        var duplicate = await Assert.That(async () => await client.Services.ExposeAsync("a", "web", 81)).Throws<OpenShellException>();
        var badPort = await Assert.That(async () => await client.Services.ExposeAsync("a", "x", 0)).Throws<OpenShellException>();
        var highPort = await Assert.That(async () => await client.Services.ExposeAsync("a", "y", 70000)).Throws<OpenShellException>();
        var noSandbox = await Assert.That(async () => await client.Services.ExposeAsync("nope", "x", 80)).Throws<OpenShellException>();
        var missing = await Assert.That(async () => await client.Services.GetAsync("a", "ghost")).Throws<OpenShellException>();
        var noSandboxGet = await Assert.That(async () => await client.Services.GetAsync("nope", "x")).Throws<OpenShellException>();
        var absent = await Assert.That(async () => await client.Services.DeleteAsync("a", "ghost")).Throws<OpenShellException>();
        var tolerated = await client.Services.DeleteAsync("a", "ghost", new DeleteOptions { AllowMissing = true });
        var noSandboxList = await Assert.That(async () => await client.Services.List("nope").ToListAsync()).Throws<OpenShellException>();
        var noSandboxDelete = await Assert.That(async () => await client.Services.DeleteAsync("nope", "x")).Throws<OpenShellException>();

        await Assert.That(duplicate!.Kind).IsEqualTo(OpenShellErrorKind.AlreadyExists);
        await Assert.That(badPort!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(highPort!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(noSandbox!.IsNotFound).IsTrue();
        await Assert.That(missing!.IsNotFound).IsTrue();
        await Assert.That(noSandboxGet!.IsNotFound).IsTrue();
        await Assert.That(absent!.IsNotFound).IsTrue();
        await Assert.That(tolerated.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        await Assert.That(noSandboxList!.IsNotFound).IsTrue();
        await Assert.That(noSandboxDelete!.IsNotFound).IsTrue();
        await Assert.That(async () => await client.Services.ExposeAsync(" ", "x", 80)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.ExposeAsync("a", null!, 80)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Services.GetAsync(" ", "x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.GetAsync("a", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => client.Services.List(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.DeleteAsync(" ", "x")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Services.DeleteAsync("a", null!)).Throws<ArgumentNullException>();
    }
}

public class FakeRootfsTests
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "fake-rootfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task Staging_allocates_a_slot_and_validates_the_request()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { RootfsMaxBytes = 100 });

        var slot = await client.Rootfs.BeginStagingAsync("root.tar", 50);
        await Assert.That(slot.UploadPath).EndsWith("root.tar");
        await Assert.That(slot.MaxBytes).IsEqualTo(100ul);
        await Assert.That(slot.ExpiresAt).IsNotNull();

        foreach (var name in new[] { "a/b.tar", "a\\b.tar", "..", ".", "a\0b" })
        {
            var ex = await Assert.That(async () => await client.Rootfs.BeginStagingAsync(name, 1)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        }

        var tooBig = await Assert.That(async () => await client.Rootfs.BeginStagingAsync("big.tar", 101)).Throws<OpenShellException>();
        await Assert.That(tooBig!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(async () => await client.Rootfs.BeginStagingAsync(" ", 1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Stage_stores_the_archive_in_memory()
    {
        var dir = Temp();
        try
        {
            using var client = FakeCoreTests.New();
            var file = Path.Combine(dir, "root.tar");
            await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });

            var token = await client.Rootfs.StageAsync(file);

            await Assert.That(client.Gateway.GetStagedRootfs(token)).IsEquivalentTo(new byte[] { 1, 2, 3 });
            var unfilled = (await client.Rootfs.BeginStagingAsync("x.tar", 1)).StagingToken;
            await Assert.That(client.Gateway.GetStagedRootfs(unfilled)).IsNull();
            await Assert.That(client.Gateway.GetStagedRootfs("unknown")).IsNull();
            await Assert.That(() => client.Gateway.GetStagedRootfs(" ")).Throws<ArgumentException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Stage_enforces_the_limit_the_gateway_reports()
    {
        var dir = Temp();
        try
        {
            using var client = FakeCoreTests.New(new FakeGatewayOptions { RootfsMaxBytes = 2 });
            var file = Path.Combine(dir, "big.tar");
            await File.WriteAllBytesAsync(file, new byte[10]);

            var ex = await Assert.That(async () => await client.Rootfs.StageAsync(file)).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
            await Assert.That(async () => await client.Rootfs.StageAsync(Path.Combine(dir, "missing.tar"))).Throws<FileNotFoundException>();
            await Assert.That(async () => await client.Rootfs.StageAsync(" ")).Throws<ArgumentException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Reading_enforces_the_limit_even_if_the_file_grew_after_the_size_check()
    {
        var dir = Temp();
        try
        {
            var file = Path.Combine(dir, "grow.tar");
            await File.WriteAllBytesAsync(file, new byte[10]);

            var ex = await Assert.That(async () => await FakeRootfsClient.ReadLimitedAsync(file, 5, CancellationToken.None)).Throws<OpenShellException>();
            var unlimited = await FakeRootfsClient.ReadLimitedAsync(file, 0, CancellationToken.None);

            await Assert.That(ex!.Message).Contains("exceeds");
            await Assert.That(unlimited.Length).IsEqualTo(10);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
