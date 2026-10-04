# Claude for Visual Studio

A Visual Studio extension (Visual Studio 2022 17.14 or later, including Visual Studio 2026) that lets you chat with Claude about a cloned git repository and have it edit files, run builds and tests there.
It works with either your own Anthropic API key, calling the API directly, or the Claude Code CLI signed in with your Pro or Max plan.

## What it does

- **Tools > Claude Chat** (or **Ctrl+Shift+Alt+C**) opens a tool window with a prompt box and a streaming transcript.
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
- **Slash commands** (type `/` for completion):
  - `/help`, `/clear` (or `/new`), `/compact`, `/model`, `/effort`, `/mode`, `/retry`, `/undo`, `/export`, `/history`.
  - `/review [focus]` asks Claude to review your uncommitted changes without editing anything; `/init` asks it to create or improve the repository's `CLAUDE.md`.
  - `/copy` copies Claude's last reply. `/cost` (or `/usage`) shows this chat's tokens and cost alongside the usage strip's figures. `/context` shows how full the context window is and which `CLAUDE.md` files apply. These replies come from the extension, so they aren't sent to Claude or included in exports.
  - **Your own commands**: Markdown files in the repository's `.claude/commands` or `~/.claude/commands` (subfolders allowed) become `/file-name`, as in Claude Code. Frontmatter `description` and `argument-hint` show in completion, and `$ARGUMENTS`, `$1`–`$9` are filled in from what you type after the name. A repository command wins over a personal one with the same name. On the Claude API engine only that text substitution happens; on Claude Code the command is passed to Claude Code, which supports everything its commands can do.
  - Anything else starting with `/` is sent as typed, so Claude Code's own commands still work.
- **Find in the chat** (**Ctrl+F**, or the magnifier in the header) searches messages and replies. **Enter** / **Shift+Enter** (or **F3** / **Shift+F3**) move between matches and **Esc** closes it.
- **Going back.** Hover over one of your messages:
  - **Edit** takes it and Claude's replies after it out of the chat and puts it back in the prompt box. Files are left as they are.
  - **Retry** sends it again.
  - **Undo** puts back the files Claude changed since that message. Before each message the extension records the working tree (tracked and untracked files) as a git tree object, without touching your index, branches or stash.
- **History** (clock icon): previous chats are saved in `%LOCALAPPDATA%\ClaudeForVS\sessions` and can be reopened, searched, pinned, renamed, exported as Markdown or deleted. Pinned chats stay at the top and are kept by **Delete all**. `/export` saves the current chat.
- **Modes**, from the model chip's menu, `/mode`, or Settings:
  - **Coding** (default): asks before every edit and before commands that change anything.
  - **Auto**: edits and runs commands without asking.
  - **Plan**: read-only; Claude reviews and suggests changes but can't make them.
- Claude works inside one git repository.
  - It's **set automatically from Visual Studio**: whenever a solution or folder inside a git repository is opened, or is already open when the chat window first loads, that repository becomes the chat's repository. If it's a different repository, your next message starts a new chat there. Solutions outside git leave the current repository alone.
  - In Settings you can type a path, **Browse…** with Visual Studio's folder picker, or click **Use solution**. A line under the box shows whether the path is inside a git repository and whether it matches the open solution.
  - A repository you pick in Settings is used, and remembered across restarts, until you next open a solution or folder in a git repository. Hover over the header chip to see which applies.
- **Settings** (gear icon, or click either header chip) picks the engine, model, effort, mode, repository and API key, and lists the commands you've chosen to always allow in the repository. Engine, model, effort and mode are also under **Tools > Options > Claude > General**. Settings are saved in `%LOCALAPPDATA%\ClaudeForVS\settings.json`.
- **Engines.** Switching engine starts a new chat.
  - **Claude API** (default): calls the API directly with your Anthropic API key and uses this extension's own tools. Billed per token to your Console account.
  - **Claude Code**: runs the [Claude Code CLI](https://code.claude.com/docs/en/setup) (`claude -p`) in the repository, signed in with your own Pro or Max plan. Usage counts against the plan's limits.
    - Settings has **Install Claude Code**, which runs Anthropic's official installer (`irm https://claude.ai/install.ps1 | iex`), and **Sign in**, which runs `claude auth login` and Anthropic's browser login. The extension never sees or stores Claude credentials.
    - Claude Code uses its own tools (read, edit, shell, search, web). Its permission prompts appear in the extension's approval card through a local MCP endpoint (`--permission-prompt-tool`) bound to `localhost` under a random path. **Allow all edits** covers Edit/Write for the rest of the chat.
    - `ANTHROPIC_API_KEY` is removed from Claude Code's environment, so it uses your plan rather than a stray key.
    - Uses Anthropic's documented options for personal use: the unmodified Claude Code binary, signed in through Anthropic's own flow. Don't distribute a build of this extension that offers Claude plan sign-in to other people; Anthropic doesn't allow that.
  - Models: Opus 5.5 (`claude-opus-5-5`, default), Sonnet 5.5 (`claude-sonnet-5-5`), Haiku 4.5 (`claude-haiku-4-5`).
  - Effort: Low, Medium, High (default), Extra high or Max. A new effort applies from the next message; switching model starts a new chat.
  - Haiku 4.5 has no effort setting, so effort maps to its thinking budget: Low means no thinking, then 4K, 10K, 20K or 32K tokens.
- **The Claude API engine's tools.**
  - `git`: read-only commands (`status`, `diff`, `log`, `show`, `blame`, `grep`, `ls-files`, branch/tag/remote listing, …) run immediately. Anything that changes the repo or contacts a remote (`commit`, `checkout`, `reset`, `push`, `merge`, `stash`, …) shows an **Allow / Deny** prompt first.
  - `read_file`, `edit_file` and `write_file` work only on files inside the repository. Absolute paths, `..` escapes, anything under `.git`, and paths through symlinks or junctions are refused. Every edit shows a preview, which can be opened in Visual Studio's diff viewer, and needs approval; **Allow all edits** skips the prompt for the rest of that chat. Edits keep the file's encoding and line endings.
  - `delete_file` and `move_file` delete, move or rename single files inside the repository, with the same rules and approval as edits.
  - `run_command` runs a Windows PowerShell command in the repository (for example `dotnet build` or `dotnet test`) so Claude can check its changes. Each command needs approval, and **Always allow** remembers plain commands (no pipes, redirects or chaining) for that repository in `%LOCALAPPDATA%\ClaudeForVS\allowed-commands.json`. Commands stop after 5 minutes unless Claude asks for longer (up to 30), and are blocked in Plan mode.
  - `CLAUDE.md` instructions are followed, as in Claude Code: `~/.claude/CLAUDE.md`, then the repository's `CLAUDE.md`, `.claude/CLAUDE.md` and `CLAUDE.local.md`. They're read when a chat starts, so edits to them apply from the next new chat.
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
| `src/ClaudeForVS/Agent/ClaudeAgent.cs` | Claude API engine: streaming tool-use loop against the Messages API, plus tool implementations |
| `src/ClaudeForVS/Agent/ClaudeCodeEngine.cs`, `ApprovalServer.cs` | Claude Code engine: runs `claude -p` and answers its permission prompts |
| `src/ClaudeForVS/Agent/RepoFiles.cs` | Repository sandbox for file reads and writes |
| `src/ClaudeForVS/Agent/ShellRunner.cs`, `CommandRules.cs` | PowerShell commands for `run_command`, and the always-allowed command rules |
| `src/ClaudeForVS/Agent/ProjectInstructions.cs` | Loads `CLAUDE.md` files for the Claude API engine |
| `src/ClaudeForVS/Agent/ApiKeyStore.cs` | DPAPI-encrypted API key in `%LOCALAPPDATA%\ClaudeForVS` |
| `src/ClaudeForVS/Git/` | Runs git (PATH, or the copy bundled with Visual Studio) with a read-only allowlist; working-tree snapshots for undo |
| `tests/ClaudeForVS.Tests/` | xUnit tests |

This is an in-process VSSDK package (.NET Framework 4.8, WPF). It uses the Anthropic C# SDK's netstandard2.0 build. Most of the SDK's dependencies resolve to the copies Visual Studio already ships, through VS's binding redirects. The rest (Anthropic, Markdig, Microsoft.Extensions.AI.Abstractions, System.Net.ServerSentEvents) ship in the VSIX and load from the extension folder via `[ProvideBindingPath]`.

## Build and test

```
dotnet build src\ClaudeForVS -c Release --no-incremental
dotnet test tests\ClaudeForVS.Tests -c Release
```

The package is written to `src/ClaudeForVS/bin/Release/net48/ClaudeForVS.vsix`. The build should finish with 0 warnings and 0 errors.

The tests cover the logic that doesn't need Visual Studio: parsing (including Claude Code's stream output), slash and custom commands, highlighting, prompt composition, export, command rules, `CLAUDE.md` loading and `run_command`. The undo snapshot tests need git on PATH.

GitHub Actions (`.github/workflows/build.yml`) runs the same build and tests on every push and pull request, and keeps the `.vsix` as an artifact.

## Run / debug

- **Install:** close Visual Studio, double-click the `.vsix`, then reopen and use **Tools > Claude Chat**. It installs over earlier versions, which use the same extension ID.
- **Debug (F5):** open `VSClaudeExtension.sln` in Visual Studio. This requires the **Visual Studio extension development** workload (Visual Studio Installer > Modify). F5 builds, deploys to the experimental instance, and launches `devenv /rootsuffix Exp`.

On first use, either paste an API key from https://console.anthropic.com (API usage is billed to that Console account, separately from any Claude subscription), or switch the engine to Claude Code in Settings and sign in.

## Uninstall

**Extensions > Manage Extensions > Installed > Claude for Visual Studio > Uninstall**, then restart Visual Studio.
To remove the saved key, settings and chat history too, delete `%LOCALAPPDATA%\ClaudeForVS`.
