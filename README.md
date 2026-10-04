# Claude for Visual Studio

A Visual Studio extension (Visual Studio 2022 17.14 or later, including Visual Studio 2026) that lets you chat with Claude about a cloned git repository and have it edit files, run builds and tests there.
It works with either your own Anthropic API key, calling the API directly, or the Claude Code CLI signed in with your Pro or Max plan.

## What it does

- **Tools > Claude Chat** (or **Ctrl+Shift+Alt+C**) opens a tool window with a prompt box and a streaming transcript.
  - **Several chats at once:** **Tools > New Claude Chat Window** (or the window icon in the header) opens another chat window, "Claude (2)" and so on. Each has its own chat, repository and Claude, so they can work at the same time. Commands from the editor, Error List, Test Explorer and Solution Explorer go to the chat window you used last. Only the first window reopens your last chat when Visual Studio starts, and a chat can only be open in one window.
  - **Enter** sends the prompt. **Shift+Enter** adds a new line. **Up** and **Down** recall earlier prompts.
  - Answers render as Markdown (headings, lists, code blocks with syntax highlighting, tables, links) while they stream, and you can select and copy them. Code blocks and replies have **Copy** buttons.
  - File paths in replies are links that open the file in the editor, at the line if one is given.
  - The transcript follows new output. Scroll up to read earlier messages; scrolling back to the bottom resumes following.
  - When Claude finishes, asks a question or needs approval while Visual Studio is in the background, its taskbar button flashes and the status bar says why.
- **Context for your message**, shown as chips above the prompt box. Click × on a chip to leave it out.
  - The file open in the editor, with its selection or caret line, is attached automatically.
  - Type `@` to mention a repository file, with completion.
  - Paste or drop images to attach them.
  - The editor's right-click **Claude** menu has **Ask Claude About This**, **Explain**, **Fix** and **Write Tests** for the selection.
  - The Error List's right-click **Fix with Claude** sends the selected errors (or all of them) to Claude.
  - Test Explorer's right-click **Fix with Claude** sends the selected tests (names, outcomes, messages and stack traces, as Test Explorer's Copy gives them) and asks Claude to fix them. The clipboard is put back afterwards.
  - Solution Explorer's right-click **Add to Claude** attaches the selected files (with their contents, up to about 60,000 characters each), folders and projects (with the files in them) to your next message.
  - **Debug > Ask Claude About This Break** (also in the editor's **Claude** menu) attaches the paused debugger's exception, call stack and the current frame's arguments and locals, and asks what's going wrong.
  - /diff attaches your uncommitted changes (git diff HEAD, plus the names of untracked files), /output the text of the Output window's active pane (the end of it, if it's long), and /openfiles the list of files open in the editor, with unsaved ones marked.
- **Commit messages:** **Git > Write Commit Message with Claude** (also in the Git Changes window's **…** menu) describes the staged changes (or every uncommitted change, if nothing is staged) in the style of the repository's recent commits, and puts the message in Git Changes' message box for you to check. If the box can't be found, the message goes on the clipboard. It uses the engine and model chosen in Settings.
- **Slash commands** (type `/` for completion):
  - `/help`, `/clear` (or `/new`), `/compact`, `/model`, `/effort`, `/mode`, `/retry`, `/undo`, `/checkpoint`, `/restore`, `/diff`, `/output`, `/openfiles`, `/export`, `/history`.
  - `/review [focus]` asks Claude to review your uncommitted changes without editing anything; `/init` asks it to create or improve the repository's `CLAUDE.md`.
  - `/copy` copies Claude's last reply. `/cost` (or `/usage`) shows this chat's tokens and cost alongside the usage strip's figures. `/context` shows how full the context window is and which `CLAUDE.md` files apply. These replies come from the extension, so they aren't sent to Claude or included in exports.
  - **Your own commands**: Markdown files in the repository's `.claude/commands` or `~/.claude/commands` (subfolders allowed) become `/file-name`, as in Claude Code. Frontmatter `description` and `argument-hint` show in completion, and `$ARGUMENTS`, `$1`–`$9` are filled in from what you type after the name. A repository command wins over a personal one with the same name. On the Claude API engine only that text substitution happens; on Claude Code the command is passed to Claude Code, which supports everything its commands can do.
  - Anything else starting with `/` is sent as typed, so Claude Code's own commands still work.
- **Find in the chat** (**Ctrl+F**, or the magnifier in the header) searches messages and replies. **Enter** / **Shift+Enter** (or **F3** / **Shift+F3**) move between matches and **Esc** closes it.
- **Choosing which changes to keep.** When an edit (on either engine) has several separate blocks of changes, the approval card shows each with a checkbox. Untick any you don't want and press **Allow**: only the ticked changes are written, and Claude is told exactly what was left out.
- **Task list.** When Claude plans work with several steps (Claude Code's TodoWrite, or the API engine's `todo_write`), the list appears above the prompt box and ticks off as Claude goes. Click its header to collapse it, or × to hide it until the next update.
- **Files changed.** When a message changes files (on either engine), a row under Claude's reply shows how many files changed and the lines added and removed, e.g. **3 files changed +42 −10**. Expand it to list each file; click one to open what that message did to it in Visual Studio's diff viewer, or press **Revert** beside it to put just that file back the way it was before the message (after a confirmation).
- **Going back.** Hover over one of your messages:
  - **Edit** takes it and Claude's replies after it out of the chat and puts it back in the prompt box. Files are left as they are.
  - **Retry** sends it again.
  - **Branch** opens a copy of the chat as it was just before that message in a new window, with the message waiting in its prompt box, so you can try a different way forward and keep this chat as it is. It works on both engines (Claude Code forks its own session at that point).
  - **Star** marks a message (yours or Claude's) so you can find it again: the star button in the header lists the starred messages and jumps to the one you pick. Stars are saved with the chat.
  - **Undo** puts back the files Claude changed since that message. Before each message the extension records the working tree (tracked and untracked files) as a git tree object, without touching your index, branches or stash. Untracked files over 5 MB are left out, so changes to them aren't listed or undone. The confirmation warns about files that also changed some other way, either while Claude worked (your own edits, another program, or a command it ran) or since.
- **Restore points.** `/checkpoint [name]` records how every file is right now as a row in the transcript (the same git tree snapshot Undo uses); its **Restore files** button, or `/restore [name]` (the latest if you give no name), puts the files back to that point after showing what would change. Only files are restored; the conversation stays as it is (use **Edit** or **Branch** for that). Restore points are saved with the chat.
- **History** (clock icon): previous chats are saved in `%LOCALAPPDATA%\ClaudeForVS\sessions` and can be reopened, searched, pinned, renamed, exported as Markdown or deleted. Pinned chats stay at the top and are kept by **Delete all**. `/export` saves the current chat.
- **Modes**, from the model chip's menu, `/mode`, or Settings:
  - **Coding** (default): asks before every edit and before commands that change anything.
  - **Auto**: edits and runs commands without asking.
  - **Plan**: read-only; Claude reviews and suggests changes but can't make them.
- Claude works inside one git repository.
  - It's **set automatically from Visual Studio**: whenever a solution or folder inside a git repository is opened, or is already open when the chat window first loads, that repository becomes the chat's repository. If it's a different repository, your next message starts a new chat there. Solutions outside git leave the current repository alone.
  - In Settings you can type a path, **Browse…** with Visual Studio's folder picker, or click **Use solution**. A line under the box shows whether the path is inside a git repository and whether it matches the open solution.
  - A repository you pick in Settings is used, and remembered across restarts, until you next open a solution or folder in a git repository. Hover over the header chip to see which applies.
- **Settings** (gear icon, or click either header chip) picks the engine, model, effort, mode, repository and API key, turns hooks on and sets a spending limit (Claude API engine), and lists the commands, tools and trusted hooks you've chosen to always allow in the repository. Engine, model, effort and mode are also under **Tools > Options > Claude > General**. Settings are saved in `%LOCALAPPDATA%\ClaudeForVS\settings.json`.
- **Engines.** Switching engine starts a new chat.
  - **Claude API** (default): calls the API directly with your Anthropic API key and uses this extension's own tools. Billed per token to your Console account.
  - **Claude Code**: runs the [Claude Code CLI](https://code.claude.com/docs/en/setup) (`claude -p`) in the repository, signed in with your own Pro or Max plan. Usage counts against the plan's limits.
    - Settings has **Install Claude Code**, which runs Anthropic's official installer (`irm https://claude.ai/install.ps1 | iex`), and **Sign in**, which runs `claude auth login` and Anthropic's browser login. The extension never sees or stores Claude credentials.
    - One Claude Code process answers every message in a chat, so follow-ups start quickly. It's restarted (resuming the same session) when you change mode or effort, edit or retry an earlier message, or stop a reply.
    - The extension's local MCP server also gives Claude Code `get_diagnostics` and `build_solution` (see below), as `mcp__visualstudio__…`.
    - Claude Code uses its own tools (read, edit, shell, search, web). Its permission prompts appear in the extension's approval card through a local MCP endpoint (`--permission-prompt-tool`) bound to `localhost` under a random path. **Allow all edits** covers Edit/Write for the rest of the chat. Edits to a file with unsaved changes in Visual Studio are refused, so Claude asks you to save first.
    - `ANTHROPIC_API_KEY` is removed from Claude Code's environment, so it uses your plan rather than a stray key.
    - Uses Anthropic's documented options for personal use: the unmodified Claude Code binary, signed in through Anthropic's own flow. Don't distribute a build of this extension that offers Claude plan sign-in to other people; Anthropic doesn't allow that.
  - Models: Opus 5.5 (`claude-opus-5-5`, default), Sonnet 5.5 (`claude-sonnet-5-5`), Haiku 4.5 (`claude-haiku-4-5`).
  - Effort: Low, Medium, High (default), Extra high or Max. A new effort applies from the next message; switching model starts a new chat.
  - Haiku 4.5 has no effort setting, so effort maps to its thinking budget: Low means no thinking, then 4K, 10K, 20K or 32K tokens.
- **The Claude API engine's tools.**
  - `git`: read-only commands (`status`, `diff`, `log`, `show`, `blame`, `grep`, `ls-files`, branch/tag/remote listing, …) run immediately. Anything that changes the repo or contacts a remote (`commit`, `checkout`, `reset`, `push`, `merge`, `stash`, …) shows an **Allow / Deny** prompt first, as do options that write files, run other programs or read files outside the repository (`--output`, `grep -O`, `--ext-diff`, `--no-index`, `blame --contents`, `config --file`, …).
  - Read-only tools that Claude asks for together (`read_file`, `glob`, `grep`, read-only `git`) run at the same time.
  - `glob` finds files by pattern (`*.cs`, `src/**/*.xaml`) and `grep` searches their contents with a regular expression, with optional context lines, a folder or file to search, and a glob filter. Both cover tracked and untracked files but skip git-ignored, binary and very large ones.
  - `web_search` and `web_fetch` are Anthropic's server-side tools, so Claude can look up documentation or an error message. Results show in the transcript. Searches are billed at $10 per 1,000 on top of tokens and are included in the cost estimate; a fetched page costs only the tokens it adds.
  - `read_file`, `edit_file` and `write_file` work only on files inside the repository. Absolute paths, `..` escapes, anything under `.git`, and paths through symlinks or junctions are refused. Every edit shows a preview of what changes, as in `git diff` with a few lines of context, which can be opened in Visual Studio's diff viewer, and needs approval; **Allow all edits** skips the prompt for the rest of that chat. Edits keep the file's encoding (UTF-8 with or without a BOM, UTF-16, UTF-32, or the system's ANSI code page for older files) and line endings. An edit isn't made if the file changed while you were deciding, or has unsaved changes in Visual Studio.
  - `delete_file` and `move_file` delete, move or rename single files inside the repository, with the same rules and approval as edits.
  - `run_command` runs a Windows PowerShell command in the repository (for example `dotnet build` or `dotnet test`) so Claude can check its changes. Each command needs approval, and **Always allow** remembers plain commands (no pipes, redirects or chaining) for that repository in `%LOCALAPPDATA%\ClaudeForVS\allowed-commands.json`. It isn't offered where a rule would allow any code: shells and `Invoke-Expression`/`Start-Process`, or a bare `git`, `python`, `node`, `dotnet` and the like without a subcommand. Commands stop after 5 minutes unless Claude asks for longer (up to 30), and are blocked in Plan mode. While a command runs, its row shows the latest output, and its **Stop** button ends just that command; Claude is told and carries on.
  - `get_diagnostics` reads Visual Studio's Error List: errors from the last build and the live (IntelliSense) errors in open files, optionally for one file or folder. It never needs approval. `build_solution` builds the open solution in Visual Studio and returns the result with the Error List, which also works for projects `dotnet build` can't build (C++, older .NET Framework). Builds need approval in Coding mode (**Always allow builds here** remembers it), are blocked in Plan mode, and only run if the open solution is in the chat's repository.
  - `todo_write` keeps the task list described above.
  - `task` hands a research job to a **subagent**: a separate Claude with its own conversation that explores with `glob`, `grep`, `read_file`, read-only `git` and the web tools and comes back with one report, so the files it reads stay out of the main context. Several `task` calls in one response run at the same time. A subagent can't change anything or ask you for approval (a read that a permission rule says to ask about is left to the main conversation), its tool calls show in the transcript marked `↳`, its tokens count towards the chat's cost, and a `Task` deny rule in `settings.json` removes the tool.
  - `run_tests` runs the tests and reports the counts and each failure (message and top of the stack trace, with repository-relative paths) from a TRX results file instead of pages of console output. With no target it runs `dotnet test` in the repository; give a solution, project or folder, or a test assembly (`.dll`), which runs with Visual Studio's own `vstest.console` (so MSTest and native C++ tests work too). It takes an optional `filter`, `configuration`, `no_build` and `timeout_seconds`. Each run needs approval in Coding mode (**Always allow running tests here** remembers it) and it's blocked in Plan mode. It doesn't drive Test Explorer's window, which has no public way to read results.
  - `get_debug_state` reads Visual Studio's debugger: whether a program is being debugged and, when paused at a breakpoint or exception, the exception, the call stack (file and line, relative to the repository) and the current frame's arguments and locals. It only reads, never needs approval, and also works in Plan mode. Claude Code gets it too, as `mcp__visualstudio__get_debug_state`.
  - **Hooks.** With **Run hooks** on in Settings (off by default, since hooks are commands), the `hooks` in `~/.claude/settings.json`, the repository's `.claude/settings.json` and `.claude/settings.local.json` run as in Claude Code: `PreToolUse` (which can block a tool), `PostToolUse` (its feedback is added to the tool's result), `UserPromptSubmit` (can block your message or add context) and `Stop` (can ask Claude to carry on, up to three times in a row). Each hook is a PowerShell command (or, with `"shell": "bash"` on the hook, a command for the bash that comes with Git for Windows, as in Claude Code) that gets the event as JSON on its standard input, with `CLAUDE_PROJECT_DIR` set; exit code 2 blocks (its stderr goes to Claude), and JSON on stdout can block with `decision` or `hookSpecificOutput.permissionDecision`. Matchers are regular expressions against Claude Code's tool names (`Bash`, `Edit`, `Write`, `Read`…) or this engine's own. Hooks from the repository ask for your approval first (**Always trust here** remembers it until a hook changes), since a cloned repository could otherwise run anything. A hook that fails or times out is reported and ignored. Claude Code runs its own hooks whatever this setting says; `/context` lists the hooks found.
  - **MCP servers** configured for Claude Code work here too: the user's servers in `~/.claude.json` (and that file's per-project ones), and the repository's `.mcp.json`, with `${VAR}` and `${VAR:-default}` expanded. Servers from `.mcp.json` start only after you approve them (or if Claude Code's own settings already do, via `enableAllProjectMcpServers` or `enabledMcpjsonServers`); **Always allow here** remembers your approval in `%LOCALAPPDATA%\ClaudeForVS\mcp-approvals.json` until the server's configuration changes. stdio and Streamable HTTP servers are supported (not the older SSE transport). Their tools appear as `mcp__server__tool`. In Coding mode each call needs approval, with **Always allow** per tool; in Plan mode only tools the server marks read-only run. Servers start with the chat's first message, and `/context` lists them.
  - **Permission rules** from Claude Code's settings (`~/.claude/settings.json`, then the repository's `.claude/settings.json` and `.claude/settings.local.json`) are followed: `permissions.allow`, `ask` and `deny`, with deny beating ask beating allow. `Bash(...)`/`PowerShell(...)` rules cover `run_command` and git commands (`Bash(git push:*)`); allow rules never cover command lines that chain or redirect, while deny and ask rules catch a match anywhere in one. `Read(...)` covers `read_file` and hides denied files from `glob` and `grep`; `Edit(...)`/`Write(...)` cover edits, writes, deletes and moves, with gitignore-style paths (`./src/**`, `/build`, `~/x`, `//abs/path`, `.env`). `WebSearch` and `WebFetch` deny rules remove those tools (`WebFetch(domain:x)` blocks a domain), and `mcp__server`/`mcp__server__tool` rules cover MCP tools. Ask rules prompt even in Auto mode, except for the web tools, which run on Anthropic's side. **Hooks** (`hooks` in the same settings files: `PreToolUse`, `PostToolUse`, `UserPromptSubmit` and `Stop`, with matchers) run when **Run hooks** is on in Settings; see below. `/context` lists the settings files in use.
  - `CLAUDE.md` instructions are followed, as in Claude Code: `~/.claude/CLAUDE.md`, then the repository's `CLAUDE.md`, `.claude/CLAUDE.md` and `CLAUDE.local.md`. They're read when a chat starts, so edits to them apply from the next new chat.
  - **Spending limit.** Set one in Settings and Claude stops and asks before an API chat goes past that estimated cost (list prices, subagents included); each yes allows another limit's worth, and no ends the reply. A subagent can't ask, so it stops itself at the limit and says its report may be incomplete; the main chat then asks.
  - When the context window is nearly full, the conversation is summarized automatically and continues from the summary (`/compact` does it on demand).
- **Questions from Claude.** When Claude needs you to choose (for example, between approaches), a question card appears above the prompt box.
  - It shows 1–4 questions, each with option cards, for a single choice or several, and an "Other" box for your own answer.
  - **Submit** sends your answers and Claude carries on. **Skip** lets Claude continue with its own judgement.
  - On Claude Code this is Claude Code's built-in `AskUserQuestion` tool; the answers go back through the approval endpoint as the tool's `answers`. On the Claude API it's the extension's own `ask_user` tool, with the same question format.
- **Usage strip**, always shown under the header:
  - **Claude Code**: your plan's limits (5-hour, weekly, and any per-model weekly windows) with a bar, a percentage and a reset countdown. They come from the `rate_limit_event` messages Claude Code streams, update while a prompt runs, and are remembered in `%LOCALAPPDATA%\ClaudeForVS\usage.json` between restarts.
  - **Claude API**: this Visual Studio session's tokens with an estimated cost at list prices, plus per-minute rate-limit headroom from the API's `anthropic-ratelimit-*` response headers. Monthly spend isn't shown; it needs an admin key.
  - **Context**: how full the current chat's context window is.
  - Bars turn amber at 75% and red at 90%, or when Claude Code reports a warning or a reached limit. Each reply also shows the tokens it used.
- Streams responses with thinking summaries (shown in grey) and uses automatic prompt caching.

## Layout

| Path | Purpose |
|---|---|
| `src/ClaudeForVS/ClaudePackage.cs`, `ClaudePackage.vsct` | VS package, menu commands, key binding, tool window registration |
| `src/ClaudeForVS/VsIntegration.cs` | Editor selection, Error List, diff viewer, taskbar flashing |
| `src/ClaudeForVS/OptionsPage.cs` | Tools > Options page |
| `src/ClaudeForVS/ToolWindows/` | WPF tool window, view model, composer, slash and custom commands, find, history, settings dialog |
| `src/ClaudeForVS/Markdown/` | Markdig to FlowDocument renderer, syntax highlighter, file links and the read-only `MarkdownView` |
| `src/ClaudeForVS/Agent/ClaudeAgent.cs` and `ClaudeAgent.*.cs` | Claude API engine, as one partial class: the tool-use loop (`ClaudeAgent.cs`), compaction and requests (`.Conversation`), streaming (`.Streaming`), running tools (`.Dispatch`), tool definitions (`.ToolDefinitions`), files, git and search (`.Files`), commands and tests (`.Shell`), Visual Studio tools (`.VisualStudio`), MCP (`.Mcp`), subagents (`.Subagent`), hooks (`.Hooks`) and the spending limit (`.Spend`) |
| `src/ClaudeForVS/Agent/Hooks.cs`, `TestResults.cs`, `DebugState.cs` | Reading and running hooks; reading TRX test results; formatting the debugger's state |
| `src/ClaudeForVS/Agent/ClaudeCodeEngine.cs`, `ApprovalServer.cs` | Claude Code engine: runs `claude -p` and answers its permission prompts |
| `src/ClaudeForVS/Agent/RepoFiles.cs` | Repository sandbox for file reads and writes |
| `src/ClaudeForVS/Agent/RepoSearch.cs` | The `glob` and `grep` tools |
| `src/ClaudeForVS/Agent/ShellRunner.cs`, `CommandRules.cs` | PowerShell commands for `run_command`, and the always-allowed command rules |
| `src/ClaudeForVS/Agent/ProjectInstructions.cs` | Loads `CLAUDE.md` files for the Claude API engine |
| `src/ClaudeForVS/Agent/PermissionRules.cs` | Claude Code's settings.json permission rules, for the Claude API engine |
| `src/ClaudeForVS/Agent/Mcp/` | MCP server configuration and client (stdio and Streamable HTTP) for the Claude API engine |
| `src/ClaudeForVS/Agent/ChangeBlocks.cs`, `ToolWindows/ChangeReview.cs` | Splitting an edit into blocks so the user can keep some of them |
| `src/ClaudeForVS/Agent/VisualStudioHost.cs` | The Error List, builds and debugger, as the engines see them |
| `src/ClaudeForVS/Agent/CommitMessageWriter.cs` | Writes commit messages for Git Changes |
| `src/ClaudeForVS/Agent/ApiKeyStore.cs` | DPAPI-encrypted API key in `%LOCALAPPDATA%\ClaudeForVS` |
| `src/ClaudeForVS/Git/` | Runs git (PATH, or the copy bundled with Visual Studio) with a read-only allowlist; working-tree snapshots for undo and the files-changed row |
| `tests/ClaudeForVS.Tests/` | xUnit tests |

This is an in-process VSSDK package (.NET Framework 4.8, WPF). It uses the Anthropic C# SDK's netstandard2.0 build. Most of the SDK's dependencies resolve to the copies Visual Studio already ships, through VS's binding redirects. The rest (Anthropic, Markdig, Microsoft.Extensions.AI.Abstractions, System.Net.ServerSentEvents) ship in the VSIX and load from the extension folder via `[ProvideBindingPath]`.

## Build and test

```
dotnet build src\ClaudeForVS -c Release --no-incremental
dotnet test tests\ClaudeForVS.Tests -c Release
```

The package is written to `src/ClaudeForVS/bin/Release/net48/ClaudeForVS.vsix`. The build should finish with 0 warnings and 0 errors.

The tests cover the logic that doesn't need Visual Studio: parsing (including Claude Code's stream output), slash and custom commands, highlighting, prompt composition, export, command rules, git's read-only check, the file sandbox and encodings, `CLAUDE.md` loading, `run_command`, `glob`/`grep`, web results, settings.json permission rules, MCP configuration and results, keeping some of an edit's changes, Error List formatting, task lists, commit message prompts, Test Explorer's copied text, hooks, TRX test results, the debugger's report and the new context attachments. The Claude API engine's tool loop is tested end to end against a fake Messages API on localhost (tool calls, approvals, hooks, subagents, the spending limit and `run_tests`). The undo snapshot and diff-stat tests need git on PATH, and the `run_tests` and hook tests start `powershell.exe` and `dotnet`.

GitHub Actions (`.github/workflows/build.yml`) runs the same build and tests on every push and pull request, and keeps the `.vsix` as an artifact.

## Run / debug

- **Install:** close Visual Studio, double-click the `.vsix`, then reopen and use **Tools > Claude Chat**. It installs over earlier versions, which use the same extension ID.
- **Debug (F5):** open `VSClaudeExtension.sln` in Visual Studio. This requires the **Visual Studio extension development** workload (Visual Studio Installer > Modify). F5 builds, deploys to the experimental instance, and launches `devenv /rootsuffix Exp`.

On first use, either paste an API key from https://console.anthropic.com (API usage is billed to that Console account, separately from any Claude subscription), or switch the engine to Claude Code in Settings and sign in.

## Uninstall

**Extensions > Manage Extensions > Installed > Claude for Visual Studio > Uninstall**, then restart Visual Studio.
To remove the saved key, settings and chat history too, delete `%LOCALAPPDATA%\ClaudeForVS`.
