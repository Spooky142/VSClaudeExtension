using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>One question to Claude and its text reply, outside any chat: with Claude Code or the Claude API, whichever is selected.</summary>
internal static class OneShot
{
    /// <param name="instructions">The project's instructions (CLAUDE.md) to add for the API; Claude Code reads them itself.</param>
    public static Task<string> AskAsync(AgentSettings settings, string repoRoot, ClaudeModel model, string prompt, Func<string> instructions, CancellationToken cancellationToken) =>
        settings.Engine == ChatEngineKind.ClaudeCode
            ? AskClaudeCodeAsync(repoRoot, model, prompt, cancellationToken)
            : AskApiAsync(settings, model, AddInstructions(prompt, instructions()), cancellationToken);

    private static string AddInstructions(string prompt, string? instructions) =>
        string.IsNullOrWhiteSpace(instructions) ? prompt : $"{prompt.TrimEnd()}\n\n<project instructions>\n{instructions!.Trim()}\n</project instructions>\n";

    private static async Task<string> AskApiAsync(AgentSettings settings, ClaudeModel model, string prompt, CancellationToken cancellationToken)
    {
        string apiKey = ApiKeyStore.Load() ?? throw new InvalidOperationException("Add your Anthropic API key in Claude's Settings first.");
        using var client = new AnthropicClient { ApiKey = apiKey };
        Message message = await client.Messages.Create(new MessageCreateParams
        {
            Model = settings.ApiModelId(model),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            MaxTokens = 16_000,
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
    private static async Task<string> AskClaudeCodeAsync(string repoRoot, ClaudeModel model, string prompt, CancellationToken cancellationToken)
    {
        string exe = ClaudeCodeCli.FindExecutable() ?? throw new InvalidOperationException("Claude Code isn't installed. Install it from Claude's Settings.");
        var args = new List<string> { "-p", "--output-format", "text", "--max-turns", "1", "--model", AgentSettings.ClaudeCodeAlias(model), "--effort", "low" };
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
        LowPriorityMode.Apply(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();
        Processes.CloseWithVisualStudio(process);

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
