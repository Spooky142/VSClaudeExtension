using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeForVS.Agent;

/// <summary>
/// A subagent's row in the chat while it works, among Claude's replies and tool calls, listing what it has done so far
/// (the latest steps). Disposing it removes the row; the finished subagent then gets a row of its own with its report.
/// </summary>
internal sealed class SubagentProgress : IDisposable
{
    internal const int ShownSteps = 10;

    private readonly ILiveActivity row;
    private readonly List<string> steps = [];

    public SubagentProgress(ILiveActivity row)
    {
        this.row = row;
        row.SetOutput(Format(this.steps));
    }

    public IReadOnlyList<string> Steps => this.steps;

    /// <summary>Adds a step, e.g. "Read Foo.cs", to the end of the list.</summary>
    public void Add(string step)
    {
        this.steps.Add(step.Replace('\r', ' ').Replace('\n', ' '));
        this.row.SetOutput(Format(this.steps));
    }

    public void Dispose() => this.row.Dispose();

    /// <summary>What the row shows: the latest steps, one per line, with a count of any earlier ones. Internal for tests.</summary>
    internal static string Format(IReadOnlyList<string> steps)
    {
        if (steps.Count == 0)
        {
            return "Starting…";
        }

        int earlier = Math.Max(0, steps.Count - ShownSteps);
        IEnumerable<string> shown = steps.Skip(earlier);
        return earlier == 0
            ? string.Join("\n", shown)
            : $"({(earlier == 1 ? "1 earlier step" : $"{earlier} earlier steps")})\n" + string.Join("\n", shown);
    }
}
