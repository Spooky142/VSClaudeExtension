# Claude for Visual Studio

A self-contained Visual Studio 2026 extension that lets you chat with Claude about a cloned git repository and have it edit files there.
It calls the Anthropic API directly with your own API key and doesn't depend on any Claude Code install.

## What it does

- **Tools > Claude Chat** opens a tool window with a prompt box and a streaming transcript.
  - **Enter** sends the prompt. **Shift+Enter** adds a new line.
  - Answers render as Markdown (headings, lists, code blocks, tables, links) while they stream, and you can select and copy them.
  - The transcript follows new output. Scroll up to read earlier messages; scrolling back to the bottom resumes following.
- Claude works inside one repository, which **From solution** detects from the open solution. You can also type any path inside a clone.
  - `git`: read-only commands (`status`, `diff`, `log`, `show`, `blame`, `grep`, `ls-files`, branch/tag/remote listing, …) run immediately. Anything that changes the repo or contacts a remote (`commit`, `checkout`, `reset`, `push`, `merge`, `stash`, …) shows an **Allow / Deny** prompt first.
  - `read_file`, `edit_file` and `write_file` work only on files inside the repository. Absolute paths, `..` escapes, anything under `.git`, and paths through symlinks or junctions are refused. Every edit shows a preview and needs approval; **Allow all edits** skips the prompt for the rest of that chat. Edits keep the file's encoding and line endings.
- **Settings** (gear icon, or click either header chip) picks the engine, model, effort, repository and API key. Settings are saved in `%LOCALAPPDATA%\ClaudeForVS\settings.json`.
- **Engines.** Switching engine starts a new chat.
  - **Claude API** (default): calls the API directly with your Anthropic API key and uses this extension's own git and file tools. Billed per token to your Console account.
  - **Claude Code**: runs the [Claude Code CLI](https://code.claude.com/docs/en/setup) (`claude -p`) in the repository, signed in with your own Pro or Max plan. Usage counts against the plan's limits.
    - Settings has **Install Claude Code**, which runs Anthropic's official installer (`irm https://claude.ai/install.ps1 | iex`), and **Sign in**, which runs `claude auth login` and Anthropic's browser login. The extension never sees or stores Claude credentials.
    - Claude Code uses its own tools (read, edit, shell, search, web). Its permission prompts appear in the extension's approval card through a local MCP endpoint (`--permission-prompt-tool`) bound to `localhost` under a random path. **Allow all edits** covers Edit/Write for the rest of the chat.
    - `ANTHROPIC_API_KEY` is removed from Claude Code's environment, so it uses your plan rather than a stray key.
    - Uses Anthropic's documented options for personal use: the unmodified Claude Code binary, signed in through Anthropic's own flow. Don't distribute a build of this extension that offers Claude plan sign-in to other people; Anthropic doesn't allow that.
  - Models: Opus 5.5 (`claude-opus-5-5`, default), Sonnet 5.5 (`claude-sonnet-5-5`), Haiku 4.5 (`claude-haiku-4-5`).
  - Effort: Low, Medium, High (default), Extra high or Max. A new effort applies from the next message; switching model starts a new chat.
  - Haiku 4.5 has no effort setting, so effort maps to its thinking budget: Low means no thinking, then 4K, 10K, 20K or 32K tokens.
- Streams responses with thinking summaries (shown in grey) and uses automatic prompt caching.

## Layout

| Path | Purpose |
|---|---|
| `src/ClaudeForVS/ClaudePackage.cs`, `ClaudePackage.vsct` | VS package, Tools menu command, tool window registration |
| `src/ClaudeForVS/ToolWindows/` | WPF tool window, view model, transcript entries |
| `src/ClaudeForVS/Markdown/` | Markdig to FlowDocument renderer and the read-only `MarkdownView` |
| `src/ClaudeForVS/Agent/ClaudeAgent.cs` | Streaming tool-use loop against the Messages API, plus tool implementations |
| `src/ClaudeForVS/Agent/RepoFiles.cs` | Repository sandbox for file reads and writes |
| `src/ClaudeForVS/Agent/ApiKeyStore.cs` | DPAPI-encrypted API key in `%LOCALAPPDATA%\ClaudeForVS` |
| `src/ClaudeForVS/Git/GitRunner.cs` | Runs git (PATH, or the copy bundled with Visual Studio); read-only allowlist |

This is an in-process VSSDK package (.NET Framework 4.8, WPF). It uses the Anthropic C# SDK's netstandard2.0 build. Most of the SDK's dependencies resolve to the copies Visual Studio already ships, through VS's binding redirects. The rest (Anthropic, Markdig, Microsoft.Extensions.AI.Abstractions, System.Net.ServerSentEvents) ship in the VSIX and load from the extension folder via `[ProvideBindingPath]`.

## Build

The VSSDK command-table compiler needs Visual Studio's MSBuild, not `dotnet build`:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" src\ClaudeForVS\ClaudeForVS.csproj -restore
```

Output: `src/ClaudeForVS/bin/Debug/net48/ClaudeForVS.vsix`.

## Run / debug

- **Install:** close Visual Studio, double-click the `.vsix`, then reopen and use **Tools > Claude Chat**. It installs over 0.1.x, which used the same extension ID.
- **Debug (F5):** open `VSClaudeExtension.sln` in Visual Studio. This requires the **Visual Studio extension development** workload (Visual Studio Installer > Modify). F5 builds, deploys to the experimental instance, and launches `devenv /rootsuffix Exp`.

On first use, paste an API key from https://console.anthropic.com. API usage is billed to that Console account, separately from any Claude subscription.

## Uninstall

**Extensions > Manage Extensions > Installed > Claude for Visual Studio > Uninstall**, then restart Visual Studio.
To remove the saved key too, delete `%LOCALAPPDATA%\ClaudeForVS`.
