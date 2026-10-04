using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

internal enum HookEvent
{
    /// <summary>Before a tool runs; a hook can block it.</summary>
    PreToolUse,

    /// <summary>After a tool ran; a hook can give Claude feedback about the result.</summary>
    PostToolUse,

    /// <summary>When the user sends a message; a hook can block it or add context.</summary>
    UserPromptSubmit,

    /// <summary>When Claude finishes answering; a hook can ask it to carry on.</summary>
    Stop,
}

/// <summary>One command from a settings file's <c>hooks</c>, with the tools it applies to.</summary>
/// <param name="Matcher">A regular expression for the tool name; null or empty means every tool.</param>
/// <param name="FromRepository">True if it came from the repository's own settings files, which need the user's trust.</param>
/// <param name="Bash">True if the hook says <c>"shell": "bash"</c>: it then runs in Git for Windows' bash, as in Claude Code, instead of PowerShell.</param>
internal sealed record HookCommand(HookEvent Event, string? Matcher, string Command, int TimeoutSeconds, bool FromRepository, bool Bash = false);

/// <summary>What the hooks for one event decided.</summary>
/// <param name="Blocked">PreToolUse and UserPromptSubmit: don't go ahead. Stop: carry on instead of finishing. PostToolUse: the feedback is for Claude.</param>
/// <param name="Message">The reason or feedback to give Claude; empty if the hooks said nothing.</param>
/// <param name="Context">Extra text for Claude from <c>additionalContext</c>.</param>
internal sealed record HookOutcome(bool Blocked, string Message, string Context)
{
    public static HookOutcome None { get; } = new(false, string.Empty, string.Empty);
}

/// <summary>
/// The <c>hooks</c> from Claude Code's settings files, so the Claude API engine can run them too:
/// <c>~/.claude/settings.json</c>, then the repository's <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c>.
/// Hooks are commands run with the event as JSON on their standard input. Exit code 2 blocks (the command's stderr goes
/// to Claude); a JSON answer on stdout can also block, with <c>decision</c> or <c>hookSpecificOutput.permissionDecision</c>.
/// </summary>
internal sealed class HookRules
{
    private readonly List<HookCommand> commands;

    private HookRules(List<HookCommand> commands, IReadOnlyList<string> files)
    {
        this.commands = commands;
        this.Files = files;
    }

    public static HookRules Empty { get; } = new([], []);

    /// <summary>The settings files that had hooks.</summary>
    public IReadOnlyList<string> Files { get; }

    public int Count => this.commands.Count;

    public IReadOnlyList<HookCommand> Commands => this.commands;

    /// <summary>True if some hook comes from the repository itself, so it needs the user's approval before it runs.</summary>
    public bool NeedsTrust => this.commands.Any(c => c.FromRepository);

    /// <summary>
    /// A name for the repository's hooks as they are now ("hooks:" and a hash), kept with the always-allowed commands
    /// once the user trusts them. Editing a hook changes it, so the user is asked again.
    /// </summary>
    public string TrustKey()
    {
        string text = string.Join("\n", this.commands.Where(c => c.FromRepository).Select(c => $"{c.Event}|{c.Matcher}|{c.TimeoutSeconds}|{c.Bash}|{c.Command}"));
        using var sha = SHA256.Create();
        return "hooks:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>The hooks without the repository's, for when the user hasn't trusted those.</summary>
    public HookRules WithoutRepository() => new(this.commands.Where(c => !c.FromRepository).ToList(), this.Files);

    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    public static HookRules Load(string repoRoot, string? userDirectory = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var commands = new List<HookCommand>();
        var files = new List<string>();
        bool first = true;
        foreach (string path in PermissionRules.SettingsFiles(repoRoot, userDirectory))
        {
            bool fromRepository = !first;
            first = false;
            if (ClaudeSettingsFile.Read(path) is not JsonElement root
                || !root.TryGetProperty("hooks", out JsonElement hooks)
                || hooks.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int before = commands.Count;
            foreach (HookEvent hookEvent in Enum.GetValues(typeof(HookEvent)))
            {
                if (!hooks.TryGetProperty(hookEvent.ToString(), out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string? matcher = group.TryGetProperty("matcher", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    foreach (JsonElement hook in list.EnumerateArray())
                    {
                        if (hook.ValueKind == JsonValueKind.Object
                            && (!hook.TryGetProperty("type", out JsonElement type) || type.GetString() == "command")
                            && hook.TryGetProperty("command", out JsonElement command) && command.ValueKind == JsonValueKind.String
                            && command.GetString()!.Trim().Length > 0)
                        {
                            int timeout = hook.TryGetProperty("timeout", out JsonElement t) && t.TryGetInt32(out int seconds) ? Math.Max(1, Math.Min(600, seconds)) : 60;
                            bool bash = hook.TryGetProperty("shell", out JsonElement shell) && shell.ValueKind == JsonValueKind.String && string.Equals(shell.GetString(), "bash", StringComparison.OrdinalIgnoreCase);
                            commands.Add(new HookCommand(hookEvent, matcher, command.GetString()!.Trim(), timeout, fromRepository, bash));
                        }
                    }
                }
            }

            if (commands.Count > before)
            {
                files.Add(path);
            }
        }

        return new HookRules(commands, files);
    }

    /// <summary>The hooks for an event that apply to a tool (every hook for events that aren't about a tool).</summary>
    /// <param name="toolNames">The tool's names: Claude Code's name for it (e.g. "Bash") and this extension's own ("run_command").</param>
    public IEnumerable<HookCommand> For(HookEvent hookEvent, params string[] toolNames) =>
        this.commands.Where(c => c.Event == hookEvent && (toolNames.Length == 0 || Matches(c.Matcher, toolNames)));

    internal static bool Matches(string? matcher, IEnumerable<string> toolNames)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*")
        {
            return true;
        }

        try
        {
            var regex = new Regex("^(?:" + matcher + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return toolNames.Any(regex.IsMatch);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false; // not a valid regular expression
        }
    }

    /// <summary>Claude Code's name for one of this extension's tools, which is what hook matchers are written against.</summary>
    public static string ClaudeCodeName(string tool) => tool switch
    {
        "git" or "run_command" => "Bash",
        "read_file" => "Read",
        "edit_file" => "Edit",
        "write_file" => "Write",
        "glob" => "Glob",
        "grep" => "Grep",
        "todo_write" => "TodoWrite",
        "ask_user" => "AskUserQuestion",
        _ => tool,
    };

    /// <summary>The tool's input in the shape Claude Code gives hooks: <c>file_path</c> for files, <c>command</c> for commands.</summary>
    public static Dictionary<string, JsonElement> HookInput(string tool, IReadOnlyDictionary<string, JsonElement> input)
    {
        Dictionary<string, JsonElement> result = input.ToDictionary(p => p.Key, p => p.Value);
        if (input.TryGetValue("path", out JsonElement path))
        {
            result["file_path"] = path;
        }

        if (tool == "git" && input.TryGetValue("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array)
        {
            result["command"] = JsonSerializer.SerializeToElement("git " + string.Join(" ", args.EnumerateArray().Select(a => a.ToString())));
        }

        if (tool == "edit_file")
        {
            foreach ((string from, string to) in new[] { ("old", "old_string"), ("new", "new_string"), ("old_text", "old_string"), ("new_text", "new_string") })
            {
                if (input.TryGetValue(from, out JsonElement value) && !result.ContainsKey(to))
                {
                    result[to] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Turns a hook's exit code and output into what it decided. Exit 0 with nothing blocking, or any exit code other
    /// than 2, lets things carry on (the latter is a hook that failed, which the caller reports separately).
    /// </summary>
    internal static HookOutcome Interpret(HookEvent hookEvent, int exitCode, string stdout, string stderr)
    {
        if (exitCode == 2)
        {
            return new HookOutcome(true, stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim(), string.Empty);
        }

        if (exitCode != 0)
        {
            return HookOutcome.None;
        }

        string trimmed = stdout.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            // Plain text on stdout is added for Claude to see by the events that allow it.
            return hookEvent is HookEvent.UserPromptSubmit && trimmed.Length > 0 ? new HookOutcome(false, string.Empty, trimmed) : HookOutcome.None;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            JsonElement root = doc.RootElement;
            string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

            string context = string.Empty;
            string reason = Text(root, "reason");
            bool blocked = Text(root, "decision") == "block";
            if (root.TryGetProperty("continue", out JsonElement cont) && cont.ValueKind == JsonValueKind.False && hookEvent != HookEvent.Stop)
            {
                blocked = true;
                reason = Text(root, "stopReason") is { Length: > 0 } stop ? stop : reason;
            }

            if (root.TryGetProperty("hookSpecificOutput", out JsonElement specific))
            {
                if (Text(specific, "permissionDecision") == "deny")
                {
                    blocked = true;
                    reason = Text(specific, "permissionDecisionReason") is { Length: > 0 } why ? why : reason;
                }

                context = Text(specific, "additionalContext");
            }

            return new HookOutcome(blocked, reason, context);
        }
        catch (JsonException)
        {
            return HookOutcome.None;
        }
    }
}

/// <summary>Runs the hooks for one conversation.</summary>
internal sealed class HookRunner(HookRules rules, string repoRoot, string sessionId)
{
    /// <param name="hookEvent">Which event this is.</param>
    /// <param name="payload">Event-specific fields for the JSON the hooks read (tool_name, tool_input, prompt…).</param>
    /// <param name="report">Called for a hook that failed, as a title and detail to show the user.</param>
    /// <param name="tool">The tool's names (Claude Code's and this extension's) for the matchers; empty for events that aren't about a tool.</param>
    public async Task<HookOutcome> RunAsync(
        HookEvent hookEvent,
        IReadOnlyDictionary<string, object?> payload,
        Action<string, string> report,
        CancellationToken cancellationToken,
        params string[] tool)
    {
        List<HookCommand> hooks = rules.For(hookEvent, tool).ToList();
        if (hooks.Count == 0)
        {
            return HookOutcome.None;
        }

        var fields = new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["cwd"] = repoRoot,
            ["hook_event_name"] = hookEvent.ToString(),
        };
        foreach (KeyValuePair<string, object?> field in payload)
        {
            fields[field.Key] = field.Value;
        }

        string json = JsonSerializer.Serialize(fields);
        var environment = new Dictionary<string, string> { ["CLAUDE_PROJECT_DIR"] = repoRoot };

        // Hooks for one event run at the same time, as in Claude Code; if any blocks, it's blocked. Not ConfigureAwait(false):
        // what follows reports to the chat, which has to happen on the thread the caller was on.
        ShellResult[] results = await Task.WhenAll(hooks.Select(async hook =>
        {
            try
            {
                return await ShellRunner.RunSeparatelyAsync(repoRoot, hook.Command, TimeSpan.FromSeconds(hook.TimeoutSeconds), cancellationToken, stdin: json, environment: environment, bash: hook.Bash);
            }
            catch (InvalidOperationException ex)
            {
                return new ShellResult(-1, ex.Message, string.Empty, ex.Message);
            }
        }));

        bool blocked = false;
        var messages = new List<string>();
        var context = new List<string>();
        for (int i = 0; i < hooks.Count; i++)
        {
            ShellResult result = results[i];
            if (result.ExitCode is not int code)
            {
                report($"Hook timed out: {hooks[i].Command}", $"It didn't finish within {hooks[i].TimeoutSeconds} seconds, so it was ignored.");
                continue;
            }

            if (code != 0 && code != 2)
            {
                report($"Hook failed: {hooks[i].Command}", $"Exit code {code}\n{result.Combined}".TrimEnd());
                continue;
            }

            HookOutcome outcome = HookRules.Interpret(hookEvent, code, result.Stdout, result.Stderr);
            blocked |= outcome.Blocked;
            if (outcome.Message.Length > 0)
            {
                messages.Add(outcome.Message);
            }

            if (outcome.Context.Length > 0)
            {
                context.Add(outcome.Context);
            }
        }

        return new HookOutcome(blocked, string.Join("\n", messages), string.Join("\n", context));
    }
}
