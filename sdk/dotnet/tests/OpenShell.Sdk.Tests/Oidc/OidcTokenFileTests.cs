// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class OidcTokenFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Round_trips_through_the_shared_cli_format()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", "ref", "Bearer", Now.AddHours(1)), time);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"));
        var read = OidcTokenFile.ReadRaw(dir.Path)!;

        await Assert.That(json).Contains("\"access_token\":\"acc\"");
        await Assert.That(json).Contains("\"refresh_token\":\"ref\"");
        await Assert.That(json).Contains("\"expiry\":\"2026-01-01T13:00:00Z\"");
        await Assert.That(json).Contains("\"expires_in\":3600");
        await Assert.That(read.AccessToken).IsEqualTo("acc");
        await Assert.That(read.RefreshToken).IsEqualTo("ref");
        await Assert.That(read.ExpiresAt).IsEqualTo(Now.AddHours(1));
        await Assert.That(read.TokenType).IsEqualTo("Bearer");
    }

    [Test]
    public async Task Tokens_without_expiry_or_refresh_are_written_minimally_and_never_expire()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc"), time);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"));
        await Assert.That(json).DoesNotContain("expiry");
        await Assert.That(json).DoesNotContain("refresh_token");

        time.Advance(TimeSpan.FromDays(3650));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, time)!.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task Already_expired_tokens_record_no_remaining_lifetime()
    {
        using var dir = new TempDirectory();
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", ExpiresAt: Now.AddHours(-1)), new FakeTimeProvider(Now));

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"))).Contains("\"expires_in\":0");
    }

    [Test]
    public async Task ReadValid_applies_a_ten_second_leeway_but_ReadRaw_does_not()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", ExpiresAt: Now.AddSeconds(60)), time);

        time.Advance(TimeSpan.FromSeconds(49));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, time)).IsNotNull();
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, time)).IsNull();
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)).IsNotNull();
    }

    [Test]
    public async Task Missing_file_directory_and_empty_access_token_read_as_null()
    {
        using var dir = new TempDirectory();
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)).IsNull();
        await Assert.That(OidcTokenFile.ReadRaw(dir.Combine("nope"))).IsNull();

        await File.WriteAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"), """{"access_token":""}""");
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)).IsNull();

        await File.WriteAllTextAsync(Path.Combine(dir.Path, "oidc_token.json"), "null");
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)).IsNull();
    }

    [Test]
    public async Task Corrupt_files_are_reported()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "oidc_token.json");

        await File.WriteAllTextAsync(path, "{nope");
        var ex1 = await Assert.That(() => OidcTokenFile.ReadRaw(dir.Path)).Throws<OidcException>();
        await Assert.That(ex1!.Failure).IsEqualTo(OidcFailure.TokenPersist);

        await File.WriteAllTextAsync(path, """{"access_token":"a","expiry":"not-a-date"}""");
        var ex2 = await Assert.That(() => OidcTokenFile.ReadRaw(dir.Path)).Throws<OidcException>();
        await Assert.That(ex2!.Message).Contains("expiry");
    }

    [Test]
    public async Task Unreadable_path_is_reported()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "oidc_token.json"));

        var ex = await Assert.That(() => OidcTokenFile.ReadRaw(dir.Path)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.TokenPersist);
    }

    [Test]
    public async Task Write_failures_are_reported_and_leave_no_temp_files()
    {
        using var dir = new TempDirectory();
        var blocker = Path.Combine(dir.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "x");

        var ex = await Assert.That(() => OidcTokenFile.Write(Path.Combine(blocker, "sub"), new OidcTokens("a"), TimeProvider.System)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.TokenPersist);
        await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Write_replaces_atomically_and_creates_the_directory()
    {
        using var dir = new TempDirectory();
        var nested = dir.Combine("a", "b");
        var time = new FakeTimeProvider(Now);

        OidcTokenFile.Write(nested, new OidcTokens("one"), time);
        OidcTokenFile.Write(nested, new OidcTokens("two"), time);

        await Assert.That(OidcTokenFile.ReadRaw(nested)!.AccessToken).IsEqualTo("two");
        await Assert.That(Directory.GetFiles(nested).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Write_waits_out_a_briefly_locked_token_file()
    {
        // Windows only: a rename over an open file succeeds on Unix, so there is nothing to wait out.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("one"), time);
        var released = FileLocks.HoldExclusively(dir.Combine("oidc_token.json"), TimeSpan.FromMilliseconds(150));

        OidcTokenFile.Write(dir.Path, new OidcTokens("two"), time);
        await released;

        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)!.AccessToken).IsEqualTo("two");
        await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Write_gives_up_on_a_token_file_that_stays_locked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("one"), time);
        var released = FileLocks.HoldExclusively(dir.Combine("oidc_token.json"), TimeSpan.FromSeconds(5));

        var ex = await Assert.That(() => OidcTokenFile.Write(dir.Path, new OidcTokens("two"), time)).Throws<OidcException>();
        await released;

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.TokenPersist);
        await Assert.That(OidcTokenFile.ReadRaw(dir.Path)!.AccessToken).IsEqualTo("one");
        await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        OidcTokenFile.Write(dir.Path, new OidcTokens("a"), TimeProvider.System);

        await Assert.That(File.GetUnixFileMode(Path.Combine(dir.Path, "oidc_token.json"))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public class OidcBrowserTests
{
    [Test]
    [Arguments("macos", "open")]
    [Arguments("linux", "xdg-open")]
    [Arguments("freebsd", "xdg-open")]
    public async Task Unix_like_commands(string os, string expected)
    {
        var (file, args) = OidcBrowser.CommandFor(os, "https://x.example.com/a?b=c");

        await Assert.That(file).IsEqualTo(expected);
        await Assert.That(args).IsEquivalentTo(new[] { "https://x.example.com/a?b=c" });
    }

    [Test]
    public async Task Windows_uses_the_url_protocol_handler_without_a_shell()
    {
        var (file, args) = OidcBrowser.CommandFor("windows", "https://x.example.com/a?b=c&d=e");

        await Assert.That(file).IsEqualTo("rundll32");
        await Assert.That(args).IsEquivalentTo(new[] { "url.dll,FileProtocolHandler", "https://x.example.com/a?b=c&d=e" });
    }

    [Test]
    public async Task Only_http_and_https_urls_may_be_opened()
    {
        OidcBrowser.Require(new Uri("https://ok.example.com"));
        OidcBrowser.Require(new Uri("http://localhost:1"));

        await Assert.That(() => OidcBrowser.Require(new Uri("file:///etc/passwd"))).Throws<InvalidOperationException>();
        await Assert.That(() => OidcBrowser.Require(new Uri("javascript:alert(1)"))).Throws<InvalidOperationException>();
        await Assert.That(OidcBrowser.CurrentOs()).IsNotNull();
    }
}
