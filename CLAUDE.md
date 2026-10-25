# Claude for VS

Visual Studio extension (net48, WPF) that hosts a Claude chat tool window. Source is in `src/ClaudeForVS`.

## Build

```powershell
dotnet build src\ClaudeForVS -c Release --no-incremental
```

The package is written to `src/ClaudeForVS/bin/Release/net48/ClaudeForVS.vsix`. The build should finish with 0 warnings and 0 errors.

## Tests

```powershell
dotnet test tests\ClaudeForVS.Tests -c Release
```

xUnit tests for the logic that doesn't need Visual Studio (parsing, Claude Code's stream output, slash and custom commands, highlighting, prompt composition, export, command rules, git's read-only check, the file sandbox and encodings, CLAUDE.md loading, `run_command`, `glob`/`grep`, web results, settings.json permission rules, MCP configuration, partial edits, Error List formatting, task lists, commit messages, hooks, TRX results, the debugger report, the Roslyn code tools against an in-memory workspace, NuGet package tools and project-file reading, formatting the lines Claude wrote, model IDs and Claude Code version numbers), the Claude API engine's tool loop against a fake Messages API on localhost, plus git-backed tests for the undo snapshots and diff stats, which need git on PATH. All tests should pass.

## Versioning

The version is `major.minor.patch`. Pick the part to bump from everything going into the commit:

- **Patch** (0.3.8 → 0.3.9): bug fixes, small tweaks to existing behaviour, wording or layout adjustments.
- **Minor** (0.3.9 → 0.4.0): new features or noticeable changes to how something works. Reset the patch number to 0.
- **Major** (0.4.2 → 1.0.0): breaking changes (e.g. saved settings, chat history or usage files that older versions can't read, a raised minimum Visual Studio version, a removed engine or feature) or a milestone release the user calls for. Reset minor and patch to 0. Only do a major bump when the user asks for one.

When asking what to do with the changes, say which bump you'd suggest and the version it gives, e.g. "minor, 0.3.18 → 0.4.0". The user has the final say. Mention the new version at the end of the commit message (e.g. "Bumped to 0.4.0.").

## After finishing a set of changes

Ask the user once what they'd like done with the changes, offering two choices, e.g. "Commit as a minor bump, 0.3.18 → 0.4.0? **Commit only**, or **commit, push and release**?" Do not commit, stage, bump, push or tag anything until they answer.

If they choose either:

1. Bump the .vsix version (see Versioning above) in all three places so they stay in step:
   - `Identity Version` in `src/ClaudeForVS/source.extension.vsixmanifest`
   - `<AssemblyVersion>` in `src/ClaudeForVS/ClaudeForVS.csproj` (same version with a trailing `.0`)
   - `<FileVersion>` in `src/ClaudeForVS/ClaudeForVS.csproj` (same value as `<AssemblyVersion>`)
2. Do a clean rebuild with the command above and confirm it succeeds. If it fails, fix the problem before going further.
3. Stage the changes, including the version bump, and commit with a short message in the style of the existing history (e.g. "Added token usage to prompts.").
4. Only if they chose **commit, push and release**: tag the commit with the new version and push both:

   ```powershell
   git tag v0.4.0
   git push origin main
   git push origin v0.4.0
   ```

   The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests every push and keeps the .vsix as an artifact of the run. For a `v*` tag it also checks the tag matches the manifest's version and publishes a GitHub release with `ClaudeForVS-<version>.vsix` attached, then deletes the older releases and their tags, so only the newest is listed. After pushing a tag, also delete older `v*` tags from the local clone (`git tag -d`), since the workflow only removes them on GitHub. The extension's self-update (`Agent/ExtensionUpdates.cs`) reads that latest release.

If they chose **commit only**, a later "push and release" just runs step 4 for the commits made since.

If they say no, leave the working tree as it is.
