// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace OpenShell;

/// <summary>One page of results.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextPageToken)
{
    /// <summary>True when another page follows.</summary>
    public bool HasMore => !string.IsNullOrEmpty(NextPageToken);
}

/// <summary>
/// A lazily evaluated listing. Enumerate it for items, or call <see cref="PagesAsync"/> for pages.
/// </summary>
public sealed class Pager<T> : IAsyncEnumerable<T>
{
    private readonly Func<string?, CancellationToken, ValueTask<Page<T>>> _fetch;
    private readonly string? _startToken;

    internal Pager(Func<string?, CancellationToken, ValueTask<Page<T>>> fetch, string? startToken = null)
    {
        _fetch = fetch;
        _startToken = string.IsNullOrEmpty(startToken) ? null : startToken;
    }

    /// <summary>Enumerates page by page.</summary>
    public async IAsyncEnumerable<Page<T>> PagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var token = _startToken;
        do
        {
            var page = await _fetch(token, cancellationToken).ConfigureAwait(false);
            yield return page;
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));
    }

    /// <inheritdoc />
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var page in PagesAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page.Items)
            {
                yield return item;
            }
        }
    }

    /// <summary>Collects every item into a list.</summary>
    public async ValueTask<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<T>();
        await foreach (var item in this.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            all.Add(item);
        }

        return all;
    }
}

/// <summary>Paging and filtering options shared by list operations.</summary>
public sealed record ListOptions
{
    /// <summary>Items per page; 0 lets the gateway choose.</summary>
    public int PageSize { get; init; }

    /// <summary>Resume token from an earlier page.</summary>
    public string? PageToken { get; init; }

    /// <summary>Label selector, when the listed resource supports it.</summary>
    public string? LabelSelector { get; init; }

    /// <summary>List across every workspace the caller can see (sandboxes, templates, providers and services only).</summary>
    public bool AllWorkspaces { get; init; }
}
