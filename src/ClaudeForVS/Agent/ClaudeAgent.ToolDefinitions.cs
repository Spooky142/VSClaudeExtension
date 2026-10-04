using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>The built-in tool definitions sent to the API.</summary>
internal sealed partial class ClaudeAgent
{
    /// <summary>The built-in tools, less the web tools any permission rule denies.</summary>
    private static List<ToolUnion> BuiltInTools(PermissionRules permissions)
    {
        var list = new List<ToolUnion>(CoreTools);
        if (permissions.ForTool("Task") == PermissionVerdict.Deny)
        {
            list.RemoveAll(t => ToolName(t) == "task");
        }

        if (permissions.ForTool("WebSearch") != PermissionVerdict.Deny)
        {
            list.Add(new WebSearchTool20250305 { MaxUses = MaxWebUses });
        }

        if (permissions.ForTool("WebFetch") != PermissionVerdict.Deny)
        {
            IReadOnlyList<string> blocked = permissions.DeniedWebDomains();
            list.Add(blocked.Count == 0
                ? new WebFetchTool20250910 { MaxUses = MaxWebUses, MaxContentTokens = 50_000 }
                : new WebFetchTool20250910 { MaxUses = MaxWebUses, MaxContentTokens = 50_000, BlockedDomains = blocked.ToList() });
        }

        return list;
    }

    private static readonly List<ToolUnion> CoreTools =
    [
        new Tool
        {
            Name = "task",
            Description =
                "Hand a research job to a subagent: a separate Claude that explores the repository (glob, grep, read_file, " +
                "read-only git) and the web in its own conversation, then returns one report. Use it for broad questions " +
                "(\"where is X handled?\", \"how do these modules fit together?\", \"what does this library's docs say about Y?\") " +
                "whose searching and file reading would otherwise fill your context. Several tasks in one response run at " +
                "the same time. The subagent can't see this conversation, so give it everything it needs; it can't change " +
                "anything or ask the user. Don't use it for a lookup you can do in one or two tool calls yourself.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["description"] = JsonSerializer.SerializeToElement(new { type = "string", description = "A short label for the job, 3 to 6 words." }),
                    ["prompt"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The complete brief: what to find out, where to look, and what the report should contain." }),
                },
                Required = ["description", "prompt"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "todo_write",
            Description =
                "Replace the task list shown to the user next to the chat. Use it to plan and track work with several steps: " +
                "send the whole list every time, keep exactly one task in_progress while you work, and mark each task " +
                "completed as soon as it's done. Skip it for quick, single-step requests.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["todos"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                content = new { type = "string", description = "The task, in the imperative, e.g. \"Run the tests\"." },
                                status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
                                activeForm = new { type = "string", description = "The task while it's being done, e.g. \"Running the tests\"." },
                            },
                            required = new[] { "content", "status", "activeForm" },
                        },
                    }),
                },
                Required = ["todos"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "get_diagnostics",
            Description =
                "List the errors and warnings in Visual Studio's Error List: errors from the last build, and the live " +
                "(IntelliSense) errors in files open in the editor, which update a moment after a file changes. Each line " +
                "is \"path:line: severity code: message [project] (source)\". Use it after editing code to check for " +
                "mistakes, before or instead of a full build.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Only list problems in this file or folder (relative to the repository root). Default: everything." }),
                    ["severity"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "error", "warning" }, description = "\"error\" lists errors only; \"warning\" (default) lists errors and warnings." }),
                },
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "get_debug_state",
            Description =
                "Read Visual Studio's debugger. When the user has paused a program at a breakpoint or an exception it returns " +
                "the exception (type and message), the call stack (function, file and line) and the current frame's arguments " +
                "and locals; otherwise it says the program isn't paused. Use it when the user is stuck on a bug they're " +
                "debugging, then read the source files in the stack. It only reads; it never steps or resumes the program.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["frames"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "How many stack frames to list. Default 15, max 50." }),
                },
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "build_solution",
            Description =
                "Build the solution open in Visual Studio (Build > Build Solution) and wait for it to finish, then return " +
                "the result and the Error List's errors and warnings. Works for any project Visual Studio can build, " +
                "including C++ and older .NET Framework projects that `dotnet build` can't. The user approves each build " +
                "unless they've allowed builds already.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = "ask_user",
            Description =
                "Ask the user 1-4 multiple-choice questions and wait for their answers. Each question has a short " +
                "header (max 12 characters), the question text, and 2-4 options with a label and a one-line description. " +
                "Set multiSelect when several options can apply. The user can always type their own answer instead. " +
                "Only ask when the decision is genuinely the user's; otherwise use your judgement.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["questions"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 4,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                question = new { type = "string", description = "The full question." },
                                header = new { type = "string", description = "Short label, max 12 characters." },
                                options = new
                                {
                                    type = "array",
                                    minItems = 2,
                                    maxItems = 4,
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            label = new { type = "string" },
                                            description = new { type = "string" },
                                        },
                                        required = new[] { "label", "description" },
                                    },
                                },
                                multiSelect = new { type = "boolean" },
                            },
                            required = new[] { "question", "header", "options", "multiSelect" },
                        },
                    }),
                },
                Required = ["questions"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "git",
            Description =
                "Run a git command in the repository. Pass the arguments after `git` as an array, e.g. " +
                "[\"log\", \"--oneline\", \"-n\", \"20\"] or [\"diff\", \"--staged\"]. Read-only commands (status, diff, log, " +
                "show, blame, grep, ls-files, branch listing, ...) run immediately; anything that modifies the repository " +
                "or contacts a remote requires the user's approval. There is no interactive terminal: never use commands " +
                "that open an editor or prompt (pass -m for commits, avoid -i/-p). Output over 60k characters is truncated.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["args"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Arguments to git, without the leading `git`.",
                    }),
                },
                Required = ["args"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "glob",
            Description =
                "Find files by path pattern. `*` and `?` match within one folder, `**` across folders, `{a,b}` either. " +
                "A pattern without a slash matches file names at any depth (\"*.cs\"); one with a slash matches the path " +
                "relative to `path` (\"src/**/*Tests.cs\"). Matching is case-insensitive. Covers tracked and untracked " +
                $"files but not git-ignored ones. Returns up to {RepoSearch.MaxGlobResults} paths, sorted.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The glob, e.g. \"*.csproj\" or \"src/**/*.xaml\"." }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Folder to search in, relative to the repository root. Default: the root." }),
                },
                Required = ["pattern"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "grep",
            Description =
                "Search file contents line by line with a .NET regular expression (escape regex characters such as " +
                "`(`, `.` or `{` to match them literally). Covers tracked and untracked files but not git-ignored, binary " +
                "or very large ones. Narrow the search with `path` (a folder or a file) and `glob` (e.g. \"*.cs\"). " +
                "output_mode \"content\" (default) returns \"path:line:text\" for each match, with `context` lines around " +
                "it marked \"path-line-text\"; \"files_with_matches\" returns only paths; \"count\" returns matches per file.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The regular expression." }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Folder or file to search, relative to the repository root. Default: the whole repository." }),
                    ["glob"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Only search files matching this glob, as in the glob tool." }),
                    ["case_insensitive"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Ignore case. Default false." }),
                    ["output_mode"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "content", "files_with_matches", "count" } }),
                    ["context"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Lines of context before and after each match (content mode), 0-10. Default 0." }),
                    ["max_results"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Most matching lines (or files) to return. Default 200." }),
                },
                Required = ["pattern"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "read_file",
            Description =
                "Read a text file from the working tree. Each line is prefixed with its line number and a tab; the prefix " +
                "is not part of the file. Use start_line/end_line (1-based, inclusive) for large files.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["start_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                    ["end_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                },
                Required = ["path"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "edit_file",
            Description =
                "Replace an exact string in an existing file. `old_string` must match the file's text exactly (including " +
                "indentation, without read_file's line-number prefixes) and must be unique in the file unless replace_all " +
                "is true; include surrounding lines to make it unique. Read the file before editing it. The user approves " +
                "each edit before it's applied.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["old_string"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Exact text to replace." }),
                    ["new_string"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Replacement text." }),
                    ["replace_all"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Replace every occurrence. Default false." }),
                },
                Required = ["path", "old_string", "new_string"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "write_file",
            Description =
                "Create a new file, or replace the entire contents of an existing one, inside the repository. Parent " +
                "directories are created as needed. Prefer edit_file for changing existing files. The user approves each " +
                "write before it's applied.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["content"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The complete file contents." }),
                },
                Required = ["path", "content"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "delete_file",
            Description =
                "Delete a file inside the repository (not a directory). The user approves each deletion before it happens.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                },
                Required = ["path"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "move_file",
            Description =
                "Move or rename a file inside the repository. Parent directories of the destination are created as needed; " +
                "the destination must not exist. Nothing is staged in git. The user approves each move before it happens.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["from"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Current path, relative to the repository root." }),
                    ["to"] = JsonSerializer.SerializeToElement(new { type = "string", description = "New path, relative to the repository root." }),
                },
                Required = ["from", "to"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "run_tests",
            Description =
                "Run the tests and get a compact report: the counts, then each failing test with its message and the top of " +
                "its stack trace (paths relative to the repository). Prefer this to run_command with `dotnet test`, whose " +
                "console output is long. With no target it runs `dotnet test` in the repository root; give a solution, " +
                "project or folder when there are several, or a test assembly (.dll) to run it with Visual Studio's own " +
                "test runner, which also handles MSTest and native C++ tests that `dotnet test` can't. The user approves " +
                "each run unless they've allowed test runs already.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["target"] = JsonSerializer.SerializeToElement(new { type = "string", description = "A solution, project, folder or test assembly, relative to the repository root. Default: the root." }),
                    ["filter"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Only run matching tests, e.g. \"FullyQualifiedName~ParserTests\" (dotnet test --filter syntax; for an assembly, vstest's /TestCaseFilter)." }),
                    ["configuration"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Build configuration, e.g. Debug or Release. Default: the project's." }),
                    ["no_build"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Skip building; only if it was built since the last change." }),
                    ["timeout_seconds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = $"Stop the run after this long. Default {DefaultTestSeconds}, max {MaxCommandSeconds}." }),
                },
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "run_command",
            Description =
                "Run a command line in Windows PowerShell 5.1 (not bash) in the repository, e.g. `dotnet build`, " +
                "`dotnet test`, `npm test`. Use it to build and test your changes; use the git tool for git and read_file " +
                "to read files. There is no terminal: the command can't prompt for input, and stdin is closed. Returns the " +
                "exit code and combined stdout/stderr (long output keeps the start and the end). The user approves each " +
                "command before it runs unless they've allowed it already; plain commands without pipes, redirects or " +
                "chaining can be allowed permanently, so prefer those.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The PowerShell command line." }),
                    ["cwd"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Directory to run in, relative to the repository root. Default: the root." }),
                    ["timeout_seconds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = $"Stop the command after this long. Default {DefaultCommandSeconds}, max {MaxCommandSeconds}." }),
                },
                Required = ["command"],
            },
            EagerInputStreaming = true,
        },
    ];
}
