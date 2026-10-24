using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeForVS.ToolWindows;

/// <summary>A row in the prompt box's completion list.</summary>
/// <param name="Display">What the row shows, e.g. "/model" or "src/Foo.cs".</param>
/// <param name="Detail">A muted description beside it.</param>
/// <param name="Insert">Text that replaces what was typed.</param>
internal sealed record Suggestion(string Display, string Detail, string Insert);

/// <summary>A command typed in the prompt box, e.g. "/model sonnet". The argument keeps the case it was typed in.</summary>
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
        ("/review", "/review [focus]", "Review your uncommitted changes"),
        ("/review-staged", "/review-staged [focus]", "Review the changes staged in git"),
        ("/review-branch", "/review-branch [focus]", "Review everything on this branch"),
        ("/conflicts", "/conflicts", "Resolve the current git merge conflicts"),
        ("/commit-message", "/commit-message", "Write a short commit message for your changes"),
        ("/pr-message", "/pr-message", "Write a short pull request message for this branch"),
        ("/pulled", "/pulled", "Summarize what came in with your last pull"),
        ("/init", "/init", "Create or update CLAUDE.md for this repository"),
        ("/retry", "/retry", "Send your last message again"),
        ("/undo", "/undo", "Undo the file changes made by your last message"),
        ("/diff", "/diff", "Attach your uncommitted changes to your next message"),
        ("/output", "/output", "Attach the Output window's text to your next message"),
        ("/openfiles", "/openfiles", "Attach the list of files open in the editor"),
        ("/checkpoint", "/checkpoint [name]", "Save a restore point for the files"),
        ("/restore", "/restore [name]", "Put the files back to a restore point"),
        ("/copy", "/copy", "Copy Claude's last reply"),
        ("/cost", "/cost", "Show token usage, cost and plan limits"),
        ("/context", "/context", "Show how full the context window is"),
        ("/export", "/export", "Save this chat as a Markdown file"),
        ("/history", "/history", "Show previous chats"),
    ];

    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/model"] = ["opus", "sonnet", "haiku"],
        ["/effort"] = ["low", "medium", "high", "xhigh", "max"],
        ["/mode"] = ["auto", "plan", "coding"],
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/new"] = "/clear",
        ["/usage"] = "/cost",
    };

    // Commands whose argument is free text, so completion leaves a space after them.
    private static readonly HashSet<string> TakesText = new(StringComparer.OrdinalIgnoreCase) { "/review", "/review-staged", "/review-branch", "/checkpoint", "/restore" };

    /// <summary>The command a prompt is, or null if it's an ordinary prompt (or a command for Claude Code).</summary>
    public static SlashCommand? Parse(string text)
    {
        if (Split(text) is not (string name, string argument))
        {
            return null;
        }

        name = Aliases.TryGetValue(name, out string? target) ? target : name.ToLowerInvariant();
        return All.Any(c => c.Name == name) ? new SlashCommand(name, argument) : null;
    }

    /// <summary>The custom command a prompt invokes, with its argument, or null.</summary>
    public static (CustomCommand Command, string Argument)? ParseCustom(string text, IReadOnlyList<CustomCommand> custom)
    {
        if (Split(text) is not (string name, string argument))
        {
            return null;
        }

        CustomCommand? command = custom.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return command is null ? null : (command, argument);
    }

    /// <summary>"/name rest" on a single line, split into the name and the trimmed rest.</summary>
    private static (string Name, string Argument)? Split(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("/") || text.Contains('\n'))
        {
            return null;
        }

        int space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text.Substring(0, space), text.Substring(space + 1).Trim());
    }

    /// <summary>Completions while a command is being typed at the start of the prompt.</summary>
    public static IReadOnlyList<Suggestion> Suggest(string typed, IReadOnlyList<CustomCommand>? custom = null)
    {
        custom ??= [];
        int space = typed.IndexOf(' ');
        if (space < 0)
        {
            IEnumerable<Suggestion> builtIn = All
                .Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Select(c => new Suggestion(c.Usage, c.Description, Arguments.ContainsKey(c.Name) || TakesText.Contains(c.Name) ? c.Name + " " : c.Name));
            IEnumerable<Suggestion> own = custom
                .Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !All.Any(b => b.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => new Suggestion(c.ArgumentHint.Length > 0 ? $"{c.Name} {c.ArgumentHint}" : c.Name, c.Description, c.TakesArguments ? c.Name + " " : c.Name));
            return builtIn.Concat(own).ToList();
        }

        string name = typed.Substring(0, space);
        string partial = typed.Substring(space + 1);
        return Arguments.TryGetValue(name, out string[]? values)
            ? values.Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).Select(v => new Suggestion(v, string.Empty, $"{name} {v}")).ToList()
            : [];
    }

    public static string HelpText(IReadOnlyList<CustomCommand>? custom = null)
    {
        string text = "**Commands**\n\n" + string.Join("\n", All.Select(c => $"- `{c.Usage}`: {c.Description}"));
        List<CustomCommand> own = (custom ?? []).Where(c => !All.Any(b => b.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (own.Count > 0)
        {
            text += "\n\n**Your commands** (from `.claude/commands`)\n\n"
                + string.Join("\n", own.Select(c => $"- `{c.Name}{(c.ArgumentHint.Length > 0 ? " " + c.ArgumentHint : string.Empty)}`: {c.Description}"));
        }

        return text + "\n\nType `@` to mention a file. Up and Down recall earlier prompts. Paste or drop images to attach them. Ctrl+F searches the chat.";
    }

    /// <summary>What <c>/review</c> asks Claude to do.</summary>
    public static string ReviewPrompt(string focus) =>
        "Review the uncommitted changes in this repository: staged, unstaged and untracked files (use `git status` and "
        + "`git diff HEAD`, and read any new files in full). Look for bugs, unhandled edge cases, security problems, and "
        + "anything inconsistent with the surrounding code. List your findings most serious first, each with the file and "
        + "line and a suggested fix. Don't change any files. If there are no uncommitted changes, say so."
        + (focus.Length > 0 ? $"\n\nFocus on: {focus}" : string.Empty);

    /// <summary>What <c>/review-staged</c> asks Claude to do.</summary>
    public static string ReviewStagedPrompt(string focus) =>
        "Review the changes staged in git in this repository (use `git diff --cached`, and read any newly added files in "
        + "full). Ignore unstaged and untracked changes. Look for bugs, unhandled edge cases, security problems, and "
        + "anything inconsistent with the surrounding code. List your findings most serious first, each with the file and "
        + "line and a suggested fix. Don't change any files. If nothing is staged, say so."
        + (focus.Length > 0 ? $"\n\nFocus on: {focus}" : string.Empty);

    /// <summary>What <c>/review-branch</c> asks Claude to do.</summary>
    public static string ReviewBranchPrompt(string focus) =>
        "Review all the changes on the current branch in this repository: the commits that aren't on the branch it was "
        + "made from (usually main or master; use `git merge-base` and `git diff <base>...HEAD`), plus any uncommitted "
        + "changes (`git status` and `git diff HEAD`, and read any new files in full). Look for bugs, unhandled edge cases, "
        + "security problems, and anything inconsistent with the surrounding code. List your findings most serious first, "
        + "each with the file and line and a suggested fix. Don't change any files. If the branch has no changes of its "
        + "own, say so."
        + (focus.Length > 0 ? $"\n\nFocus on: {focus}" : string.Empty);

    /// <summary>What <c>/conflicts</c> asks Claude to do.</summary>
    public const string ConflictsPrompt =
        "Resolve the current git merge conflicts in this repository. Use `git status` to find the conflicted files (from a "
        + "merge, rebase, cherry-pick or stash), and `git log` on both sides if it helps to understand what each change was "
        + "for. In each file, resolve every conflict so the result keeps the intent of both sides where possible, and "
        + "remove all the conflict markers. Don't stage, commit or continue the merge or rebase; leave that to me. Finish "
        + "with a short summary of how you resolved each file, calling out any conflict where you had to choose one side "
        + "over the other. If there are no conflicts, say so.";

    /// <summary>What <c>/commit-message</c> asks Claude to do.</summary>
    public const string CommitMessagePrompt =
        "Write a very short, concise commit message for the current changes on this branch. Use the uncommitted changes "
        + "(`git status` and `git diff HEAD`, including new files); if there are none, use the commits on this branch that "
        + "aren't on the branch it was made from. Match the style of the recent history in `git log`. Reply with just the "
        + "message as plain text, ready to paste into git, with no Markdown formatting: a one-line summary, then a few "
        + "short bullet points only if the changes need them. Don't change any files, and don't stage or commit anything.";

    /// <summary>What <c>/pr-message</c> asks Claude to do.</summary>
    public const string PrMessagePrompt =
        "Write a very short, concise pull request message for the current branch. Use the commits that aren't on the "
        + "branch it was made from (usually main or master; use `git merge-base`, `git log <base>..HEAD` and "
        + "`git diff <base>...HEAD`), plus any uncommitted changes. Reply with just the message, formatted in Markdown: a "
        + "one-line title, then a few short bullet points covering what changed and why, only as many as the changes "
        + "need. Don't change any files, and don't stage, commit or push anything.";

    /// <summary>What <c>/pulled</c> asks Claude to do.</summary>
    public const string PulledPrompt =
        "Summarize what came into the current branch with my last pull. Use `git reflog` to find the most recent pull "
        + "(or merge or rebase from upstream) and where HEAD was before it, then `git log` and `git diff --stat` between "
        + "the two to see what came in. Don't fetch or pull again. Group the summary by area or feature, most significant "
        + "first, and call out anything likely to affect my work: changed APIs, new dependencies, build or configuration "
        + "changes, and files I have uncommitted changes in (check `git status`). Don't change any files. If there's no "
        + "pull in the reflog, say so.";

    /// <summary>What <c>/init</c> asks Claude to do.</summary>
    public const string InitPrompt =
        "Create a CLAUDE.md file at the root of this repository, or improve the existing one, to guide future work here. "
        + "Look at the repository first: its layout, how to build, test and run it, the languages and frameworks used, the "
        + "conventions the code follows, and anything non-obvious that someone new would trip over. Keep it concise and "
        + "specific to this repository, with no generic advice. If a CLAUDE.md already exists, keep what's still right "
        + "and improve the rest rather than starting over.";
}
