using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace ClaudeForVS.Agent;

/// <summary>Running the hooks from Claude Code's settings files.</summary>
internal sealed partial class ClaudeAgent
{
    // At most this many times in a row a Stop hook can ask Claude to carry on.
    private const int MaxStopContinuations = 3;

    private readonly HookRules allHooks;

    // What actually runs: decided once per chat, when hooks are first needed, since the repository's own hooks need the user's trust.
    private HookRunner? hooks;

    /// <summary>Whether to run hooks at all (a setting; off by default, since they run commands). Takes effect from the next message.</summary>
    public bool HooksEnabled { get; set; }

    /// <summary>The hooks the settings files define (whether or not they're being run).</summary>
    public HookRules Hooks => this.allHooks;

    /// <summary>
    /// Works out which hooks this chat runs. Hooks from the user's own settings run as they are; ones from the
    /// repository's settings run only once the user has trusted them, since a cloned repository could otherwise run
    /// anything. Trust is remembered until a hook changes.
    /// </summary>
    private async Task EnsureHooksAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        if (!this.HooksEnabled || this.hooks != null || this.allHooks.Count == 0)
        {
            return;
        }

        HookRules active = this.allHooks;
        if (active.NeedsTrust && !CommandRules.IsToolAllowed(this.RepoRoot, active.TrustKey()))
        {
            sink.SetStatus("Waiting for your approval…");
            string detail = string.Join("\n", active.Commands.Where(c => c.FromRepository).Select(c =>
                $"{c.Event}{(string.IsNullOrEmpty(c.Matcher) ? string.Empty : " (" + c.Matcher + ")")}: {c.Command}"))
                + "\n\nThey come from this repository's .claude settings and run on this machine with your permissions.";
            ApprovalResult approval = await sink.RequestApprovalAsync(ActivityKind.Shell, "Run this repository's hooks", detail, "Always trust here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, "Repository hooks not run", "You'll be asked again in your next chat.");
                active = active.WithoutRepository();
            }
            else if (approval.Decision == Approval.AllowAll)
            {
                CommandRules.Allow(this.RepoRoot, active.TrustKey());
            }
        }

        this.hooks = new HookRunner(active, this.RepoRoot, this.instanceId);
    }

    private bool HooksActive => this.HooksEnabled && this.hooks != null;

    private static Action<string, string> HookReporter(IChatSink sink) => (title, detail) => sink.ToolActivity(ActivityKind.Failed, title, detail);

    /// <summary>Runs PreToolUse hooks. Returns what to tell Claude if a hook blocked the tool, otherwise null.</summary>
    private async Task<string?> RunPreToolHooksAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        if (!this.HooksActive)
        {
            return null;
        }

        string ccName = HookRules.ClaudeCodeName(toolUse.Name);
        var payload = new Dictionary<string, object?>
        {
            ["tool_name"] = ccName,
            ["tool_input"] = HookRules.HookInput(toolUse.Name, toolUse.Input),
        };
        HookOutcome outcome = await this.hooks!.RunAsync(HookEvent.PreToolUse, payload, HookReporter(sink), cancellationToken, ccName, toolUse.Name);
        if (!outcome.Blocked)
        {
            return null;
        }

        string reason = outcome.Message.Length > 0 ? outcome.Message : "No reason was given.";
        sink.ToolActivity(ActivityKind.Declined, $"A hook blocked {toolUse.Name}", reason);
        return $"A hook in the user's settings blocked this ({reason}). Don't try to get around it; adjust what you're doing or ask the user.";
    }

    /// <summary>Runs PostToolUse hooks; returns feedback to add to the tool's result (empty if there is none).</summary>
    private async Task<string> RunPostToolHooksAsync(ToolUseBlockParam toolUse, string output, IChatSink sink, CancellationToken cancellationToken)
    {
        if (!this.HooksActive)
        {
            return string.Empty;
        }

        string ccName = HookRules.ClaudeCodeName(toolUse.Name);
        var payload = new Dictionary<string, object?>
        {
            ["tool_name"] = ccName,
            ["tool_input"] = HookRules.HookInput(toolUse.Name, toolUse.Input),
            ["tool_response"] = output,
        };
        HookOutcome outcome = await this.hooks!.RunAsync(HookEvent.PostToolUse, payload, HookReporter(sink), cancellationToken, ccName, toolUse.Name);
        string feedback = string.Join("\n", new[] { outcome.Message, outcome.Context }.Where(s => s.Length > 0));
        if (feedback.Length == 0)
        {
            return string.Empty;
        }

        sink.ToolActivity(ActivityKind.Other, $"Hook feedback on {toolUse.Name}", feedback);
        return $"\n\n[Hook feedback: {feedback}]";
    }

    /// <summary>Runs UserPromptSubmit hooks. Returns a reason if one blocked the prompt; extra context from the hooks goes in <paramref name="context"/>.</summary>
    private async Task<string?> RunPromptHooksAsync(string prompt, IChatSink sink, CancellationToken cancellationToken, Action<string> context)
    {
        if (!this.HooksActive)
        {
            return null;
        }

        HookOutcome outcome = await this.hooks!.RunAsync(
            HookEvent.UserPromptSubmit, new Dictionary<string, object?> { ["prompt"] = prompt }, HookReporter(sink), cancellationToken);
        if (outcome.Blocked)
        {
            return outcome.Message.Length > 0 ? outcome.Message : "A hook blocked this message.";
        }

        if (outcome.Context.Length > 0)
        {
            context(outcome.Context);
        }

        return null;
    }

    /// <summary>Runs Stop hooks. Returns what to tell Claude if a hook wants it to carry on, otherwise null.</summary>
    private async Task<string?> RunStopHooksAsync(bool continuing, IChatSink sink, CancellationToken cancellationToken)
    {
        if (!this.HooksActive)
        {
            return null;
        }

        HookOutcome outcome = await this.hooks!.RunAsync(
            HookEvent.Stop, new Dictionary<string, object?> { ["stop_hook_active"] = continuing }, HookReporter(sink), cancellationToken);
        if (!outcome.Blocked)
        {
            return null;
        }

        string reason = outcome.Message.Length > 0 ? outcome.Message : "A hook asked you to carry on.";
        sink.ToolActivity(ActivityKind.Other, "A hook asked Claude to carry on", reason);
        return $"[A Stop hook in the user's settings says you aren't finished: {reason}]";
    }
}
