using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeForVS.ToolWindows;

/// <summary>A row in the prompt box's completion list.</summary>
/// <param name="Display">What the row shows, e.g. "/model" or "src/Foo.cs".</param>
/// <param name="Detail">A muted description beside it.</param>
/// <param name="Insert">Text that replaces what was typed.</param>
internal sealed record Suggestion(string Display, string Detail, string Insert);

/// <summary>A command typed in the prompt box, e.g. "/model sonnet".</summary>
internal sealed record SlashCommand(string Name, string Argument);

/// <summary>The prompt box's own commands. Anything else starting with "/" is sent to Claude as typed.</summary>
internal static class SlashCommands
{
    public static readonly IReadOnlyList<(string Name, string Usage, string Description)> All =
    [
        ("/help", "/help", "List these commands"),
        ("/clear", "/clear", "Start a new chat"),
        ("/compact", "/compact", "Summarize the conversation to free up context"),
        ("/model", "/model opus|sonnet|haiku", "Switch model (starts a new chat)"),
        ("/effort", "/effort low|medium|high|xhigh|max", "Change how hard Claude thinks"),
        ("/mode", "/mode auto|plan|coding", "Change what Claude may do without asking"),
        ("/retry", "/retry", "Send your last message again"),
        ("/undo", "/undo", "Undo the file changes made by your last message"),
        ("/export", "/export", "Save this chat as a Markdown file"),
        ("/history", "/history", "Show previous chats"),
    ];

    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/model"] = ["opus", "sonnet", "haiku"],
        ["/effort"] = ["low", "medium", "high", "xhigh", "max"],
        ["/mode"] = ["auto", "plan", "coding"],
    };

    /// <summary>The command a prompt is, or null if it's an ordinary prompt (or a command for Claude Code).</summary>
    public static SlashCommand? Parse(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("/") || text.Contains('\n'))
        {
            return null;
        }

        int space = text.IndexOf(' ');
        string name = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
        if (name == "/new")
        {
            name = "/clear";
        }

        return All.Any(c => c.Name == name) ? new SlashCommand(name, space < 0 ? string.Empty : text.Substring(space + 1).Trim().ToLowerInvariant()) : null;
    }

    /// <summary>Completions while a command is being typed at the start of the prompt.</summary>
    public static IReadOnlyList<Suggestion> Suggest(string typed)
    {
        int space = typed.IndexOf(' ');
        if (space < 0)
        {
            return All
                .Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Select(c => new Suggestion(c.Usage, c.Description, Arguments.ContainsKey(c.Name) ? c.Name + " " : c.Name))
                .ToList();
        }

        string name = typed.Substring(0, space);
        string partial = typed.Substring(space + 1);
        return Arguments.TryGetValue(name, out string[]? values)
            ? values.Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).Select(v => new Suggestion(v, string.Empty, $"{name} {v}")).ToList()
            : [];
    }

    public static string HelpText() =>
        "**Commands**\n\n" + string.Join("\n", All.Select(c => $"- `{c.Usage}`: {c.Description}"))
        + "\n\nType `@` to mention a file. Up and Down recall earlier prompts. Paste or drop images to attach them.";
}
