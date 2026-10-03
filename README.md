# Claude for Visual Studio

A self-contained Visual Studio 2026 extension that lets you chat with Claude about a cloned git repository.
It calls the Anthropic API directly with your own API key and doesn't depend on any Claude Code install.

## What it does

- **Tools > Claude Chat** opens a tool window with a prompt box and a streaming transcript.
- Claude can run `git` commands in the repository and read files from the working tree.
  - Read-only commands (`status`, `diff`, `log`, `show`, `blame`, `grep`, `ls-files`, branch/tag/remote listing, …) run immediately.
  - Anything that changes the repo or contacts a remote (`commit`, `checkout`, `reset`, `push`, `merge`, `stash`, …) shows an **Allow / Deny** prompt first.
- The repository is detected from the open solution (**From solution**), or you can type any path inside a clone.
- Uses `claude-opus-5-5` with adaptive thinking (summaries shown in grey), streaming, and automatic prompt caching.

## Layout

| Path | Purpose |
|---|---|
| `src/ClaudeForVS/ExtensionEntrypoint.cs` | Extension metadata |
| `src/ClaudeForVS/ShowClaudeWindowCommand.cs` | Tools menu command |
| `src/ClaudeForVS/ToolWindows/` | Tool window, view model, Remote UI XAML |
| `src/ClaudeForVS/Agent/ClaudeAgent.cs` | Streaming tool-use loop against the Messages API |
| `src/ClaudeForVS/Agent/ApiKeyStore.cs` | DPAPI-encrypted API key in `%LOCALAPPDATA%\ClaudeForVS` |
| `src/ClaudeForVS/Git/GitRunner.cs` | Runs git (PATH, or the copy bundled with Visual Studio); read-only allowlist |

This is an out-of-process extension (VisualStudio.Extensibility, .NET 8), so it runs in its own process alongside Visual Studio.

## Build

```
dotnet build src/ClaudeForVS/ClaudeForVS.csproj
```

Output: `src/ClaudeForVS/bin/Debug/net8.0-windows8.0/ClaudeForVS.vsix`.

## Run / debug

- **Install:** close Visual Studio, double-click the `.vsix`, then reopen and use **Tools > Claude Chat**.
- **Debug (F5):** open `VSClaudeExtension.sln` in Visual Studio. This requires the **Visual Studio extension development** workload (Visual Studio Installer > Modify). F5 launches an experimental instance with the extension loaded.

On first use, paste an API key from https://console.anthropic.com. API usage is billed to that Console account, separately from any Claude subscription.

## Uninstall

**Extensions > Manage Extensions > Installed > Claude for Visual Studio > Uninstall**, then restart Visual Studio.
To remove the saved key too, delete `%LOCALAPPDATA%\ClaudeForVS`.
