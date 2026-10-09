// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;

namespace OpenShell.Tests;

public class ErrorMappingTests
{
    [Test]
    [Arguments(StatusCode.NotFound, OpenShellErrorKind.NotFound)]
    [Arguments(StatusCode.AlreadyExists, OpenShellErrorKind.AlreadyExists)]
    [Arguments(StatusCode.PermissionDenied, OpenShellErrorKind.PermissionDenied)]
    [Arguments(StatusCode.Unauthenticated, OpenShellErrorKind.Unauthenticated)]
    [Arguments(StatusCode.InvalidArgument, OpenShellErrorKind.InvalidArgument)]
    [Arguments(StatusCode.OutOfRange, OpenShellErrorKind.InvalidArgument)]
    [Arguments(StatusCode.Unavailable, OpenShellErrorKind.Unavailable)]
    [Arguments(StatusCode.DeadlineExceeded, OpenShellErrorKind.DeadlineExceeded)]
    [Arguments(StatusCode.Cancelled, OpenShellErrorKind.Cancelled)]
    [Arguments(StatusCode.Unimplemented, OpenShellErrorKind.Unimplemented)]
    [Arguments(StatusCode.Aborted, OpenShellErrorKind.Conflict)]
    [Arguments(StatusCode.FailedPrecondition, OpenShellErrorKind.Conflict)]
    [Arguments(StatusCode.Internal, OpenShellErrorKind.Internal)]
    [Arguments(StatusCode.DataLoss, OpenShellErrorKind.Internal)]
    [Arguments(StatusCode.ResourceExhausted, OpenShellErrorKind.Unknown)]
    [Arguments(StatusCode.Unknown, OpenShellErrorKind.Unknown)]
    public async Task Maps_grpc_status_to_kind(StatusCode code, OpenShellErrorKind expected)
    {
        var ex = OpenShellException.FromRpc(new RpcException(new Status(code, "boom")));

        await Assert.That(ex.Kind).IsEqualTo(expected);
        await Assert.That(ex.GrpcStatusCode).IsEqualTo(code);
        await Assert.That(ex.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task Empty_detail_falls_back_to_rpc_message()
    {
        var ex = OpenShellException.FromRpc(new RpcException(new Status(StatusCode.NotFound, "")));

        await Assert.That(ex.Message.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Convenience_predicates_follow_kind()
    {
        var notFound = new OpenShellException(OpenShellErrorKind.NotFound, "x");
        var exists = new OpenShellException(OpenShellErrorKind.AlreadyExists, "x");
        var conflict = new OpenShellException(OpenShellErrorKind.Conflict, "x");
        var unavailable = new OpenShellException(OpenShellErrorKind.Unavailable, "x");
        var deadline = new OpenShellException(OpenShellErrorKind.DeadlineExceeded, "x");
        var denied = new OpenShellException(OpenShellErrorKind.PermissionDenied, "x");

        await Assert.That(notFound.IsNotFound).IsTrue();
        await Assert.That(exists.IsAlreadyExists).IsTrue();
        await Assert.That(conflict.IsConflict).IsTrue();
        await Assert.That(unavailable.IsTransient).IsTrue();
        await Assert.That(deadline.IsTransient).IsTrue();
        await Assert.That(denied.IsTransient).IsFalse();
        await Assert.That(denied.GrpcStatusCode).IsNull();
    }
}

public class PagerTests
{
    private static Pager<int> Make(int pages, List<string?> seenTokens) =>
        new((token, _) =>
        {
            seenTokens.Add(token);
            var index = token is null ? 0 : int.Parse(token);
            var items = new[] { index * 10, (index * 10) + 1 };
            return ValueTask.FromResult(new Page<int>(items, index + 1 < pages ? (index + 1).ToString() : null));
        });

    [Test]
    public async Task Enumerates_every_item_across_pages_lazily()
    {
        var tokens = new List<string?>();
        var all = await Make(3, tokens).ToListAsync();

        await Assert.That(all).IsEquivalentTo([0, 1, 10, 11, 20, 21]);
        await Assert.That(tokens).IsEquivalentTo(new string?[] { null, "1", "2" });
    }

    [Test]
    public async Task Does_not_fetch_until_enumerated()
    {
        var tokens = new List<string?>();
        _ = Make(3, tokens);

        await Assert.That(tokens.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Early_exit_stops_fetching()
    {
        var tokens = new List<string?>();
        await foreach (var _ in Make(5, tokens))
        {
            break;
        }

        await Assert.That(tokens.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Pages_expose_has_more()
    {
        var pages = new List<Page<int>>();
        await foreach (var page in Make(2, []).PagesAsync())
        {
            pages.Add(page);
        }

        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That(pages[0].HasMore).IsTrue();
        await Assert.That(pages[1].HasMore).IsFalse();
    }

    [Test]
    public async Task Starts_from_supplied_token_and_treats_empty_as_start()
    {
        var tokens = new List<string?>();
        var resumed = new Pager<int>(
            (token, _) =>
            {
                tokens.Add(token);
                return ValueTask.FromResult(new Page<int>([1], null));
            },
            "abc");
        var fresh = new Pager<int>(
            (token, _) =>
            {
                tokens.Add(token);
                return ValueTask.FromResult(new Page<int>([1], null));
            },
            "");
        _ = await resumed.ToListAsync();
        _ = await fresh.ToListAsync();

        await Assert.That(tokens).IsEquivalentTo(new string?[] { "abc", null });
    }

    [Test]
    public async Task Cancellation_propagates_to_fetch()
    {
        using var cts = new CancellationTokenSource();
        var pager = new Pager<int>((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new Page<int>([1], null));
        });
        await cts.CancelAsync();

        await Assert.That(async () => await pager.ToListAsync(cts.Token)).Throws<OperationCanceledException>();
    }
}
