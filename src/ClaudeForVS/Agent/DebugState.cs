using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeForVS.Agent;

internal enum DebugMode
{
    NotDebugging,

    /// <summary>The program is running, so there's no stack or locals to read.</summary>
    Running,

    /// <summary>Stopped at a breakpoint, an exception or a step, so the stack and locals can be read.</summary>
    Paused,
}

/// <summary>One frame of the call stack.</summary>
/// <param name="File">The source file, if the frame has one (frames in framework code often don't).</param>
internal sealed record DebugFrame(string Function, string? File, int Line, string? Module);

/// <summary>A local variable or argument as the debugger shows it.</summary>
internal sealed record DebugValue(string Name, string Type, string Value);

/// <summary>What the debugger is doing: whether it's paused, why, and where.</summary>
/// <param name="Reason">Why it stopped, e.g. "Breakpoint hit" or "Unhandled exception".</param>
/// <param name="ExceptionType">The exception being handled when paused on one.</param>
internal sealed record DebugState(
    DebugMode Mode,
    string? Reason,
    string? ExceptionType,
    string? ExceptionMessage,
    IReadOnlyList<DebugFrame> Stack,
    IReadOnlyList<DebugValue> Arguments,
    IReadOnlyList<DebugValue> Locals,
    int? ThreadCount,
    string? Program)
{
    public static DebugState NotDebugging { get; } = new(DebugMode.NotDebugging, null, null, null, [], [], [], null, null);
}

/// <summary>Formats the debugger's state for Claude.</summary>
internal static class DebugReport
{
    public const int DefaultFrames = 15;
    public const int MaxFrames = 50;
    private const int MaxValues = 40;
    private const int MaxValueChars = 300;

    public static string Format(DebugState state, string repoRoot, int frames = DefaultFrames)
    {
        frames = Math.Max(1, Math.Min(MaxFrames, frames));
        switch (state.Mode)
        {
            case DebugMode.NotDebugging:
                return "Visual Studio isn't debugging anything right now.";
            case DebugMode.Running:
                return $"The program{(state.Program is null ? string.Empty : $" ({state.Program})")} is running, so its state can't be read. It has to be paused at a breakpoint or an exception first.";
        }

        var sb = new StringBuilder();
        sb.Append("Paused in the debugger");
        if (state.Program != null)
        {
            sb.Append($" ({state.Program})");
        }

        if (state.Reason != null)
        {
            sb.Append($": {state.Reason}");
        }

        if (state.ThreadCount is int threads)
        {
            sb.Append($" · {threads} thread{(threads == 1 ? string.Empty : "s")}");
        }

        sb.Append('.');
        if (state.ExceptionType != null || state.ExceptionMessage != null)
        {
            sb.Append("\n\nException: ").Append(state.ExceptionType ?? "(unknown type)");
            if (!string.IsNullOrWhiteSpace(state.ExceptionMessage))
            {
                sb.Append(": ").Append(Trim(state.ExceptionMessage!));
            }
        }

        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (state.Stack.Count > 0)
        {
            sb.Append("\n\nCall stack (current frame first):");
            for (int i = 0; i < Math.Min(frames, state.Stack.Count); i++)
            {
                DebugFrame frame = state.Stack[i];
                sb.Append($"\n  #{i} {frame.Function}");
                if (frame.File is string file)
                {
                    string shown = file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Substring(root.Length).Replace('\\', '/') : file;
                    sb.Append($" at {shown}{(frame.Line > 0 ? ":" + frame.Line : string.Empty)}");
                }
                else if (!string.IsNullOrEmpty(frame.Module))
                {
                    sb.Append($" [{frame.Module}]");
                }
            }

            if (state.Stack.Count > frames)
            {
                sb.Append($"\n  … {state.Stack.Count - frames} more frames");
            }
        }

        AppendValues(sb, "Arguments of the current frame", state.Arguments);
        AppendValues(sb, "Locals of the current frame", state.Locals);
        return sb.ToString();
    }

    private static void AppendValues(StringBuilder sb, string heading, IReadOnlyList<DebugValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        sb.Append($"\n\n{heading}:");
        foreach (DebugValue value in values.Take(MaxValues))
        {
            sb.Append($"\n  {value.Name}{(value.Type.Length > 0 ? $" ({value.Type})" : string.Empty)} = {Trim(value.Value)}");
        }

        if (values.Count > MaxValues)
        {
            sb.Append($"\n  … {values.Count - MaxValues} more");
        }
    }

    private static string Trim(string text)
    {
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return text.Length > MaxValueChars ? text.Substring(0, MaxValueChars) + "…" : text;
    }
}
