using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>What this chat has cost, and the limit the user can set on it.</summary>
internal sealed partial class ClaudeAgent
{
    // Estimated cost of everything this engine instance has sent, at list prices.
    private double spent;

    // Each time the user lets Claude carry on past the limit, the limit moves up by another one.
    private int limitRaises;

    /// <summary>A cost in US dollars to ask the user at; 0 means no limit. Can change between messages.</summary>
    public double SpendLimitUsd { get; set; }

    /// <summary>The estimated cost of this chat so far.</summary>
    public double Spent => this.spent;

    private readonly object spendGate = new();

    /// <summary>True once the chat has used up its limit (and whatever the user has allowed beyond it).</summary>
    private bool OverSpendLimit => this.SpendLimitUsd > 0 && this.spent >= this.SpendLimitUsd * (1 + this.limitRaises);

    /// <param name="model">The model that used the tokens, if not the chat's own (a subagent's).</param>
    private void ReportUsage(IChatSink sink, TokenUsage usage, ClaudeModel? model = null)
    {
        ClaudeModel by = model ?? this.Model;
        lock (this.spendGate)
        {
            this.spent += ModelPricing.EstimateCost(by, usage);
        }

        sink.ReportTokenUsage(by, usage);
    }

    /// <summary>
    /// True if the chat is under its limit, or the user said to carry on. Past the limit it asks, and each yes allows
    /// another limit's worth; a no ends the reply.
    /// </summary>
    private async Task<bool> WithinSpendLimitAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        double limit = this.SpendLimitUsd;
        if (limit <= 0 || this.spent < limit * (1 + this.limitRaises))
        {
            return true;
        }

        sink.SetStatus("Waiting for your approval…");
        ApprovalResult approval = await sink.RequestApprovalAsync(
            ActivityKind.Other,
            $"Spending limit reached: ≈ ${this.spent:0.00} of ${limit:0.00}",
            $"This chat has used about ${this.spent:0.00} (estimated at list prices), which is past the limit you set in Settings. "
                + $"Continue to let Claude carry on until ≈ ${limit * (2 + this.limitRaises):0.00}.",
            null,
            cancellationToken);
        if (approval.Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, "Stopped at your spending limit", $"≈ ${this.spent:0.00} of ${limit:0.00}. Send another message to go on.");
            sink.AppendText("\n\n_Stopped at your spending limit._");
            return false;
        }

        this.limitRaises++;
        return true;
    }
}
