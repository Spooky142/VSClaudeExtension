# Claude for Visual Studio

A Visual Studio extension that lets you chat with Claude about a git repository and have it edit files, run builds and tests, and use Visual Studio's own tools. It needs Visual Studio 2022 17.14 or later (including Visual Studio 2026).

## Getting started

1. Download `ClaudeForVS-<version>.vsix` from the [latest release](https://github.com/Spooky142/VSClaudeExtension/releases/latest), close Visual Studio and double-click it. The extension updates itself after that.
2. Open **Tools > Claude Chat** (**Ctrl+Shift+Alt+C**).
3. Choose an engine in Settings (the gear icon):
   - **Claude API** (default): paste an API key from https://console.anthropic.com. You're billed per token on that Console account, separately from any Claude subscription.
   - **Claude Code**: runs the [Claude Code CLI](https://code.claude.com/docs/en/setup) signed in with your Pro or Max plan. Settings can install it and sign you in. The extension never sees your Claude credentials. This is for personal use only: don't distribute a build that offers Claude plan sign-in to other people.

## Features

- **Chat** with streaming Markdown, clickable file links, several chat windows at once, search (**Ctrl+F**), and saved history you can pin, rename and export.
- **Context:** the open file and selection are attached automatically. You can also `@`-mention files, paste images, or use **Add to Claude** in Solution Explorer.
- **Visual Studio commands:** Ask, Explain, Fix, Write Tests and **Edit with Claude Here** (**Ctrl+Alt+/**) in the editor and the light bulb; **Fix with Claude** in the Error List and Test Explorer; **Ask Claude About This Break** while debugging; **Write Commit Message with Claude** in Git Changes.
- **Safe edits:** Coding, Auto and Plan modes; approval cards that let you keep only some of an edit's changes; a files-changed row with diffs and per-file revert; **Undo**, **Branch** and `/checkpoint`/`/restore` to go back. Edits to open files go through the editor, so a single **Ctrl+Z** undoes them.
- **Tools:** files, git, search, PowerShell, `run_tests`, the Error List, builds, the debugger, Roslyn navigation and rename, NuGet packages, the web, and subagents. Claude Code gets the Visual Studio tools through a local MCP server; you can turn them off in Settings to use less of your plan.
- **Claude Code compatibility:** `CLAUDE.md`, `.claude/commands`, permission rules and hooks from `settings.json`, and MCP servers from `.mcp.json` all work on both engines. The **MCP servers** and **Skills** windows list what Claude Code uses (claude.ai connectors and plugins included) and turn each on or off per repository.
- **Slash commands:** type `/` to see them, e.g. `/review`, `/commit-message`, `/diff`, `/compact`, `/cost`, `/context`, `/undo`.
- **Usage strip:** your plan's limits (Claude Code) or tokens and estimated cost (API), plus how full the context window is. You can set a spending limit for the API engine.
- **Models:** Opus 5.5 (default), Sonnet 5.5 and Haiku 5.5, with effort levels from Low to Max.

Settings, history and the encrypted API key are kept in `%LOCALAPPDATA%\ClaudeForVS`.

## Build and debug

```
dotnet build src\ClaudeForVS -c Release --no-incremental
dotnet test tests\ClaudeForVS.Tests -c Release
```

The package is written to `src/ClaudeForVS/bin/Release/net48/ClaudeForVS.vsix`. To debug, open `VSClaudeExtension.sln` with the **Visual Studio extension development** workload installed and press F5, which launches the experimental instance. GitHub Actions builds and tests every push, and publishes a release for each `v*` tag.

## Uninstall

Go to **Extensions > Manage Extensions > Installed**, choose **Claude for Visual Studio > Uninstall** and restart Visual Studio. To remove your settings and history too, delete `%LOCALAPPDATA%\ClaudeForVS`.
