# Claude for VS

Visual Studio extension (net48, WPF) that hosts a Claude chat tool window. Source is in `src/ClaudeForVS`.

## Build

```powershell
dotnet build src\ClaudeForVS -c Release --no-incremental
```

The package is written to `src/ClaudeForVS/bin/Release/net48/ClaudeForVS.vsix`. The build should finish with 0 warnings and 0 errors.

## After finishing a set of changes

Ask the user whether they would like the changes committed. Do not commit, stage or bump anything until they say yes.

If they say yes:

1. Bump the .vsix version (patch number, e.g. 0.3.8 → 0.3.9) in all three places so they stay in step:
   - `Identity Version` in `src/ClaudeForVS/source.extension.vsixmanifest`
   - `<AssemblyVersion>` in `src/ClaudeForVS/ClaudeForVS.csproj` (same version with a trailing `.0`)
   - `<FileVersion>` in `src/ClaudeForVS/ClaudeForVS.csproj` (same value as `<AssemblyVersion>`)
2. Do a clean rebuild with the command above and confirm it succeeds. If it fails, fix the problem before going further.
3. Stage the changes, including the version bump, and commit with a short message in the style of the existing history (e.g. "Added token usage to prompts.").

If they say no, leave the working tree as it is.
