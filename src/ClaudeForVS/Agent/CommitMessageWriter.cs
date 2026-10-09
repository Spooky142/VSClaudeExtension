using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>
/// Writes a commit message for a repository's changes (staged ones, or everything uncommitted if nothing is staged),
/// in the style of its recent history, with whichever engine is selected.
/// </summary>
internal static class CommitMessageWriter
{
    private const int MaxDiffChars = 80_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static async Task<string> GenerateAsync(AgentSettings settings, string repoRoot, CancellationToken cancellationToken)
    {
        (string diff, bool staged) = await ChangesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (diff.Trim().Length == 0)
        {
            throw new InvalidOperationException("There are no changes to describe.");
        }

        (int _, string log) = await GitRunner.RunAsync(repoRoot, ["log", "-n", "15", "--format=%s"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        string reply = settings.Engine == ChatEngineKind.ClaudeCode
            ? await AskClaudeCodeAsync(settings, repoRoot, BuildPrompt(diff, staged, log, instructions: null), timeout.Token).ConfigureAwait(false)
            : await AskApiAsync(settings, BuildPrompt(diff, staged, log, ProjectInstructions.Load(repoRoot).Text), timeout.Token).ConfigureAwait(false);
        string message = Clean(reply);
        return message.Length > 0 ? message : throw new InvalidOperationException("Claude didn't return a message.");
    }

    /// <summary>The changes to describe: the staged diff, or if nothing is staged, every uncommitted change plus new files.</summary>
    private static async Task<(string Diff, bool Staged)> ChangesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        (int _, string staged) = await GitRunner.RunAsync(repoRoot, ["diff", "--staged", "--stat", "--patch", "--no-ext-diff"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        if (staged.Trim().Length > 0)
        {
            return (staged, true);
        }

        (int _, string unstaged) = await GitRunner.RunAsync(repoRoot, ["diff", "HEAD", "--stat", "--patch", "--no-ext-diff"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        (int _, string untracked) = await GitRunner.RunAsync(repoRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        string files = untracked.Trim();
        return (unstaged + (files.Length > 0 ? "\nNew files (not shown above):\n" + files : string.Empty), false);
    }

    /// <summary>The request: the diff, recent subjects for style, and (for the API) the project's instructions. Internal for tests.</summary>
    internal static string BuildPrompt(string diff, bool staged, string recentSubjects, string? instructions)
    {
        if (diff.Length > MaxDiffChars)
        {
            diff = diff.Substring(0, MaxDiffChars) + "\n[diff truncated]";
        }

        var sb = new StringBuilder();
        sb.Append("Write a git commit message for the ").Append(staged ? "staged changes" : "uncommitted changes").Append(" below.\n\n");
        sb.Append("Match the style of the repository's recent commit subjects (tense, length, punctuation, prefixes) and follow any ");
        sb.Append("commit message conventions in the project instructions, but don't bump versions or run anything. ");
        sb.Append("Summarize what changed and why in a short subject line; add a body after a blank line only if it helps. ");
        sb.Append("Reply with the commit message only: no preamble, no quotes and no code fences.\n");
        if (recentSubjects.Trim().Length > 0)
        {
            sb.Append("\n<recent commit subjects>\n").Append(recentSubjects.Trim()).Append("\n</recent commit subjects>\n");
        }

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append("\n<project instructions>\n").Append(instructions!.Trim()).Append("\n</project instructions>\n");
        }

        sb.Append("\n<diff>\n").Append(diff.TrimEnd()).Append("\n</diff>\n");
        return sb.ToString();
    }

    /// <summary>Strips what models sometimes add anyway: code fences, surrounding quotes, a "Commit message:" label. Internal for tests.</summary>
    internal static string Clean(string reply)
    {
        string text = reply.Replace("\r\n", "\n").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            int closing = text.LastIndexOf("```", StringComparison.Ordinal);
            text = firstNewline >= 0 && closing > firstNewline ? text.Substring(firstNewline + 1, closing - firstNewline - 1).Trim() : text.Trim('`').Trim();
        }

        foreach (string label in new[] { "Commit message:", "commit message:" })
        {
            if (text.StartsWith(label, StringComparison.Ordinal))
            {
                text = text.Substring(label.Length).Trim();
            }
        }

        if (text.Length > 1 && ((text[0] == '"' && text[text.Length - 1] == '"') || (text[0] == '\'' && text[text.Length - 1] == '\'')))
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }

        return text.Replace("\n", Environment.NewLine);
    }

    private static async Task<string> AskApiAsync(AgentSettings settings, string prompt, CancellationToken cancellationToken)
    {
        string apiKey = ApiKeyStore.Load() ?? throw new InvalidOperationException("Add your Anthropic API key in Claude's Settings first.");
        using var client = new AnthropicClient { ApiKey = apiKey };
        Message message = await client.Messages.Create(new MessageCreateParams
        {
            Model = AgentSettings.ModelId(settings.Model),
            MaxTokens = 2000,
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        foreach (ContentBlock block in message.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                text.Append(t.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>Asks Claude Code (one turn, no tools) in the repository, so it also reads CLAUDE.md itself.</summary>
    private static async Task<string> AskClaudeCodeAsync(AgentSettings settings, string repoRoot, string prompt, CancellationToken cancellationToken)
    {
        string exe = ClaudeCodeCli.FindExecutable() ?? throw new InvalidOperationException("Claude Code isn't installed. Install it from Claude's Settings.");
        var args = new List<string> { "-p", "--output-format", "text", "--max-turns", "1", "--model", AgentSettings.ModelId(settings.Model) };
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(GitRunner.QuoteArgument)))
        {
            WorkingDirectory = repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ClaudeCodeCli.RemoveApiKeyVariables(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();

        // The prompt goes in on stdin as UTF-8 bytes (net48 can't set stdin's encoding).
        byte[] bytes = new UTF8Encoding(false).GetBytes(prompt);
        await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() =>
        {
            Processes.Kill(process);
            exited.TrySetCanceled();
        }))
        {
            await exited.Task.ConfigureAwait(false);
        }

        string text = await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string err = (await errors.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(err.Length > 0 ? err : text.Trim().Length > 0 ? text.Trim() : $"Claude Code exited with code {process.ExitCode}.");
        }

        return text;
    }
}
