// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>Options for the Python exec helpers.</summary>
public sealed record PythonExecOptions
{
    /// <summary>Interpreter to invoke inside the sandbox. Defaults to <c>python</c>.</summary>
    public string Interpreter { get; init; } = "python";

    /// <summary>Arguments appended after the script; visible to it as <c>sys.argv[1:]</c>.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>Common exec options (working directory, environment, timeout, stdin).</summary>
    public ExecOptions? Exec { get; init; }
}

/// <summary>Python convenience helpers over <see cref="IExecClient"/>.</summary>
public static class ExecPythonExtensions
{
    /// <summary>
    /// Runs a Python source string with <c>python -c</c> and buffers the output. The Python SDK ships a pickled callable
    /// instead; .NET code cannot be pickled, so the script is sent as source.
    /// </summary>
    public static ValueTask<ExecResult> ExecPythonAsync(
        this IExecClient exec,
        string sandbox,
        string script,
        PythonExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        return exec.RunAsync(sandbox, BuildCommand(script, options), options?.Exec, cancellationToken);
    }

    /// <summary>Like <see cref="ExecPythonAsync"/> but streams output events as they arrive.</summary>
    public static IAsyncEnumerable<ExecEvent> ExecPythonStreamAsync(
        this IExecClient exec,
        string sandbox,
        string script,
        PythonExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        return exec.StreamAsync(sandbox, BuildCommand(script, options), options?.Exec, cancellationToken);
    }

    internal static List<string> BuildCommand(string script, PythonExecOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var command = new List<string>(3 + (options?.Arguments?.Count ?? 0))
        {
            options?.Interpreter is { Length: > 0 } interpreter ? interpreter : "python",
            "-c",
            script,
        };
        if (options?.Arguments is { } args)
        {
            command.AddRange(args);
        }

        return command;
    }
}
