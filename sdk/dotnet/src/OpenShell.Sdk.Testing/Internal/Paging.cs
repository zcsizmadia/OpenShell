// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace OpenShell.Testing;

internal static class FakePaging
{
    /// <summary>
    /// Builds a pager whose every page is one recorded call. <paramref name="snapshot"/> runs under the gateway lock
    /// and must return the full, stably ordered result; the pager slices it with opaque page tokens.
    /// </summary>
    public static Pager<T> Create<T>(
        FakeGateway gateway,
        string operation,
        string workspace,
        string? target,
        int pageSize,
        string? startToken,
        Func<List<T>> snapshot)
    {
        var size = pageSize > 0 ? pageSize : gateway.Options.DefaultPageSize;
        return new Pager<T>(
            (token, cancellationToken) =>
                ValueTask.FromResult(gateway.Run(operation, workspace, target, cancellationToken, () => Slice(snapshot(), token, size))),
            startToken);
    }

    public static Page<T> Slice<T>(List<T> items, string? token, int size)
    {
        var offset = 0;
        if (!string.IsNullOrEmpty(token)
            && (token[0] != 'o' || !int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > items.Count))
        {
            throw new OpenShellException(OpenShellErrorKind.InvalidArgument, "invalid page token");
        }

        var page = items.Skip(offset).Take(size).ToArray();
        var next = offset + page.Length;
        return new Page<T>(page, next < items.Count ? "o" + next.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Selects the workspaces a list call covers: one workspace, or every workspace for all-workspace listings.</summary>
    public static bool InScope(string itemWorkspace, string workspace, bool allWorkspaces) =>
        allWorkspaces || string.Equals(itemWorkspace, workspace, StringComparison.Ordinal);
}

/// <summary>Evaluates Kubernetes-style equality label selectors: <c>k=v</c>, <c>k==v</c>, <c>k!=v</c>, <c>k</c>, <c>!k</c>.</summary>
internal static class LabelSelector
{
    public static Func<IReadOnlyDictionary<string, string>, bool> Compile(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return static _ => true;
        }

        var terms = new List<Func<IReadOnlyDictionary<string, string>, bool>>();
        foreach (var raw in selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            terms.Add(CompileTerm(raw));
        }

        return labels => terms.All(term => term(labels));
    }

    private static Func<IReadOnlyDictionary<string, string>, bool> CompileTerm(string term)
    {
        string key;
        if (term.Contains("!=", StringComparison.Ordinal))
        {
            var parts = term.Split("!=", 2, StringSplitOptions.TrimEntries);
            key = Require(parts[0], term);
            var value = parts[1];
            return labels => !labels.TryGetValue(key, out var actual) || actual != value;
        }

        var eq = term.IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0)
        {
            key = Require(term[..eq].Trim(), term);
            var value = term[(eq + 1)..].TrimStart('=').Trim();
            return labels => labels.TryGetValue(key, out var actual) && actual == value;
        }

        if (term[0] == '!')
        {
            key = Require(term[1..].Trim(), term);
            return labels => !labels.ContainsKey(key);
        }

        key = Require(term, term);
        return labels => labels.ContainsKey(key);
    }

    private static string Require(string key, string term) =>
        key.Length > 0 && !key.Any(char.IsWhiteSpace)
            ? key
            : throw new OpenShellException(OpenShellErrorKind.InvalidArgument, $"invalid label selector term '{term}'");
}
