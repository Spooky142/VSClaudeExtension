using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>The usage strip (plan windows, API spend, context) and the token count shown under each prompt.</summary>
internal sealed partial class ClaudeChatViewModel
{
    // Usage strip state. Plan windows are shared by every chat window and persist across restarts; API totals cover this
    // Visual Studio session.
    private static readonly Dictionary<string, PlanLimit> PlanLimits = PlanUsageStore.Load();
    private TokenUsage sessionTokens;
    private double sessionCost;
    private ApiRateLimits? apiLimits;

    // Tokens in the current chat's context, as of the latest response; 0 until the chat's engine has answered once.
    private long contextTokens;

    // Tokens (and, for the API, estimated cost) used by the prompt currently running; shown as its footer.
    private TokenUsage promptTokens;
    private double promptCost;
    private bool promptHasCost;
    private string usageEmptyText = string.Empty;

    // Tokens and cost of the current chat since it was started or opened, for /cost.
    private TokenUsage chatTokens;
    private double chatCost;
    private bool chatHasCost;

    /// <summary>Tiles in the always-visible usage strip, for whichever engine is selected.</summary>
    public ObservableCollection<UsageMeter> UsageMeters { get; } = [];

    public bool HasUsageMeters => this.UsageMeters.Count > 0;

    /// <summary>Tiles per row: all in one row, except four, which go in a 2x2 grid.</summary>
    public int UsageColumns => this.UsageMeters.Count == 4 ? 2 : Math.Max(1, this.UsageMeters.Count);

    /// <summary>Shown in place of the meters before there's anything to report.</summary>
    public string UsageEmptyText { get => this.usageEmptyText; private set => this.Set(ref this.usageEmptyText, value); }

    /// <summary>Rebuilds the usage tiles. Also called on a timer so "resets in" stays current.</summary>
    public void RefreshUsage()
    {
        List<UsageMeter> meters = this.settings.Engine == ChatEngineKind.ClaudeCode ? this.PlanMeters() : this.ApiMeters();
        meters.Add(this.ContextMeter());

        // Only tiles that changed are replaced, so the timer's refresh doesn't rebuild the strip when nothing moved.
        for (int i = 0; i < meters.Count; i++)
        {
            if (i >= this.UsageMeters.Count)
            {
                this.UsageMeters.Add(meters[i]);
            }
            else if (!this.UsageMeters[i].Equals(meters[i]))
            {
                this.UsageMeters[i] = meters[i];
            }
        }

        while (this.UsageMeters.Count > meters.Count)
        {
            this.UsageMeters.RemoveAt(this.UsageMeters.Count - 1);
        }

        this.UsageEmptyText = meters.Count > 0 ? string.Empty
            : this.settings.Engine == ChatEngineKind.ClaudeCode ? "Plan usage limits appear here after your first message."
            : "Token usage and rate limits appear here after your first message.";
        this.OnPropertyChanged(nameof(this.HasUsageMeters));
        this.OnPropertyChanged(nameof(this.UsageColumns));
    }

    private List<UsageMeter> PlanMeters()
    {
        // The 5-hour and weekly windows always show (at 0% until known); the first other window seen takes the third slot.
        string[] order = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_overage_included", "overage"];
        IEnumerable<string> windows = new[] { "five_hour", "seven_day" }
            .Concat(PlanLimits.Keys
                .Where(w => w is not "five_hour" and not "seven_day")
                .OrderBy(w => Array.IndexOf(order, w) is int i && i >= 0 ? i : order.Length)
                .Take(1));
        var meters = new List<UsageMeter>();
        foreach (string window in windows)
        {
            string label = window switch
            {
                "five_hour" => "5-hour limit",
                "seven_day" => "Weekly limit",
                "seven_day_opus" => "Weekly · Opus",
                "seven_day_sonnet" => "Weekly · Sonnet",
                "seven_day_overage_included" => "Weekly · with extra",
                "overage" => "Extra usage",
                _ => window,
            };

            if (!PlanLimits.TryGetValue(window, out PlanLimit? limit))
            {
                meters.Add(new UsageMeter(label, "0%", "Updates after your next message", 0, UsageSeverity.Normal));
                continue;
            }

            if (limit.ResetsAt is DateTimeOffset reset && reset <= DateTimeOffset.Now)
            {
                meters.Add(new UsageMeter(label, "0%", "Reset · updates after your next message", 0, UsageSeverity.Normal));
                continue;
            }

            // Without a percentage the bar still shows: full if the limit's been hit, empty otherwise.
            double percent = limit.Utilization ?? (limit.Status == "rejected" ? 100 : 0);
            string value = limit.Utilization is double pct ? $"{pct:0}%"
                : limit.Status switch { "rejected" => "Limit reached", "allowed_warning" => "Near limit", _ => "OK" };
            meters.Add(new UsageMeter(label, value, FormatReset(limit.ResetsAt), percent, UsageMeter.SeverityFor(limit.Utilization, limit.Status)));
        }

        return meters;
    }

    private List<UsageMeter> ApiMeters()
    {
        var meters = new List<UsageMeter>();
        TokenUsage t = this.sessionTokens;
        string sessionDetail = $"{FormatTokens(t.Input + t.CacheWrite)} in · {FormatTokens(t.Output)} out"
            + (t.CacheRead > 0 ? $" · {FormatTokens(t.CacheRead)} cached" : string.Empty);
        meters.Add(new UsageMeter("This session", $"≈ ${this.sessionCost:0.00}", sessionDetail, null, UsageSeverity.Normal));

        if (this.apiLimits is ApiRateLimits limits && limits.PercentUsed is double used)
        {
            bool reset = limits.ResetsAt is DateTimeOffset at && at <= DateTimeOffset.Now;
            double shown = reset ? 0 : used;
            string detail = reset ? "Per-minute window has reset" : FormatReset(limits.ResetsAt);
            meters.Add(new UsageMeter("Rate limit (per minute)", $"{shown:0}% used", detail, shown, UsageMeter.SeverityFor(shown)));
        }
        else
        {
            meters.Add(new UsageMeter("Rate limit (per minute)", "0% used", "Updates after your next message", 0, UsageSeverity.Normal));
        }

        return meters;
    }

    private UsageMeter ContextMeter()
    {
        long window = AgentSettings.ContextWindow;
        double used = Math.Min(100, 100.0 * this.contextTokens / window);
        string detail = this.contextTokens > 0 ? $"{FormatTokens(this.contextTokens)} of {FormatTokens(window)} tokens"
            : $"{FormatTokens(window)} token window · updates as Claude replies";
        return new UsageMeter("Context", $"{used:0}%", detail, used, UsageMeter.SeverityFor(used));
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1_000 ? $"{n / 1_000.0:0.#}k" : n.ToString("N0");

    private static string FormatReset(DateTimeOffset? at)
    {
        if (at is not DateTimeOffset reset)
        {
            return string.Empty;
        }

        TimeSpan left = reset - DateTimeOffset.Now;
        return left.TotalMinutes < 1 ? (left.TotalSeconds > 0 ? $"Resets in {left.Seconds}s" : "Resetting now")
            : left.TotalHours < 1 ? $"Resets in {(int)left.TotalMinutes}m"
            : left.TotalHours < 24 ? $"Resets in {(int)left.TotalHours}h {left.Minutes}m"
            : $"Resets {reset.ToLocalTime():ddd h:mm tt}";
    }

    private long liveTokens;

    // The usage entry of the prompt in progress; its text is the realtime "1,234 tokens" count.
    private ChatEntry? liveUsageEntry;

    private void SetLiveTokens(long tokens)
    {
        this.liveTokens = tokens;
        if (tokens > 0)
        {
            this.ShowPromptUsage($"{tokens:N0} tokens");
        }
    }

    /// <summary>Shows the prompt's token count as the last transcript entry, below anything added since it last changed.</summary>
    private void ShowPromptUsage(string text)
    {
        if (this.liveUsageEntry is null)
        {
            this.liveUsageEntry = new ChatEntry(ChatEntryKind.Usage, string.Empty, text);
            this.Messages.Add(this.liveUsageEntry);
            return;
        }

        this.liveUsageEntry.SetText(text);
        int at = this.Messages.IndexOf(this.liveUsageEntry);
        if (at >= 0 && at < this.Messages.Count - 1)
        {
            this.Messages.Move(at, this.Messages.Count - 1);
        }
    }

    void IChatSink.ReportLiveTokens(long totalTokens) => this.SetLiveTokens(totalTokens);

    // When a Claude Code prompt last finished in any window. The usage endpoint can lag behind the last few requests, so
    // plan usage is fetched more often for a while afterwards.
    private static DateTime lastRunEndedUtc = DateTime.MinValue;

    /// <summary>
    /// Whether the view's timer should fetch plan usage: while idle on Claude Code, every minute (so use elsewhere shows),
    /// or every 15 seconds in the minute after a prompt finishes. Running prompts fetch it themselves.
    /// </summary>
    public bool PlanUsageDue
    {
        get
        {
            if (this.settings.Engine != ChatEngineKind.ClaudeCode || this.IsBusy)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            TimeSpan interval = now - lastRunEndedUtc < TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1);
            return now - PlanUsageClient.LastRequestedUtc >= interval;
        }
    }

    /// <summary>Fetches the plan usage windows now, rather than waiting for the next prompt to report them.</summary>
    public async Task RefreshPlanUsageAsync()
    {
        ReportPlanLimits(await PlanUsageClient.FetchAsync(CancellationToken.None));
    }

    private void ResetChatUsage()
    {
        this.chatTokens = default;
        this.chatCost = 0;
        this.chatHasCost = false;
    }

    void IChatSink.ReportTokenUsage(ClaudeModel model, TokenUsage usage)
    {
        double cost = ModelPricing.EstimateCost(model, usage);
        this.sessionTokens += usage;
        this.sessionCost += cost;
        this.promptTokens += usage;
        this.promptCost += cost;
        this.promptHasCost = true;
        this.RefreshUsage();
    }

    // Claude Code usage comes out of the plan, so there's no per-token cost to show.
    void IChatSink.ReportPromptUsage(TokenUsage usage) => this.promptTokens += usage;

    private string FormatPromptUsage()
    {
        // Only what Claude wrote: input is mostly the system prompt, tools and history sent again each round, which made
        // even a short prompt look like tens of thousands of tokens. The cost estimate still covers everything.
        long output = this.promptTokens.Output > 0 ? this.promptTokens.Output : this.liveTokens;
        string text = $"{output:N0} tokens";
        return this.promptHasCost ? $"{text} · ≈ ${this.promptCost:0.00}" : text;
    }

    void IChatSink.ReportContext(long tokens)
    {
        this.contextTokens = tokens;
        this.RefreshUsage();
    }

    void IChatSink.ReportCompacted(long tokensBefore)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        string was = tokensBefore > 0 ? $" · was {FormatTokens(tokensBefore)} tokens" : string.Empty;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Context compacted{was}"));
        this.contextTokens = 0; // the next reply reports the new size
        this.RefreshUsage();
    }

    void IChatSink.ReportApiRateLimits(ApiRateLimits limits)
    {
        this.apiLimits = limits;
        this.RefreshUsage();
    }

    void IChatSink.ReportPlanLimit(PlanLimit limit) => ReportPlanLimits([limit]);

    /// <summary>Takes in plan windows, then saves them and refreshes every window's strip once for the lot.</summary>
    private static void ReportPlanLimits(IReadOnlyList<PlanLimit> limits)
    {
        if (limits.Count == 0)
        {
            return;
        }

        foreach (PlanLimit reported in limits)
        {
            // Stream events often omit the percentage; keep the last exact figure for the same window period.
            PlanLimit limit = reported;
            if (limit.Utilization is null
                && PlanLimits.TryGetValue(limit.Window, out PlanLimit? known)
                && known.Utilization is double pct
                && known.ResetsAt is DateTimeOffset knownReset && limit.ResetsAt is DateTimeOffset newReset
                && Math.Abs((knownReset - newReset).TotalMinutes) < 5)
            {
                limit = limit with { Utilization = pct };
            }

            PlanLimits[limit.Window] = limit;
        }

        PlanUsageStore.Save(PlanLimits.Values);
        foreach (ClaudeChatViewModel chat in Instances)
        {
            chat.RefreshUsage();
        }
    }
}
