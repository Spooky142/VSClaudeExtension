using System;
using System.Linq;
using System.Threading.Tasks;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>Offering to carry on at lower priority when the 5-hour limit has run out but the weekly one hasn't.</summary>
internal sealed partial class ClaudeChatViewModel
{
    // Shared by every chat window, so the offer is made once for each 5-hour window, not once per window or reply.
    private static DateTimeOffset? offeredForReset;
    private static DateTime offeredAtUtc = DateTime.MinValue;

    // Whether this window's banners include the lower-priority one, so the timer can drop it when the mode ends.
    private bool showingLowPriority;

    /// <summary>Raised after a Claude Code reply fails because the 5-hour limit has run out while the weekly one hasn't.</summary>
    public event EventHandler<LowPriorityOffer>? LowPriorityOffered;

    /// <summary>After a Claude Code reply failed: fetches fresh plan usage and, if the 5-hour limit is what stopped it, makes the offer.</summary>
    private async Task OfferLowPriorityAsync()
    {
        await this.RefreshPlanUsageAsync();
        if (LowPriorityMode.Offer(PlanLimits, DateTimeOffset.Now, LowPriorityMode.IsActive) is not LowPriorityOffer offer)
        {
            return;
        }

        // Once per 5-hour window: by its reset time, or for five hours when the reset time isn't known.
        bool offered = offer.ResetsAt is DateTimeOffset reset
            ? offeredForReset is DateTimeOffset last && Math.Abs((last - reset).TotalMinutes) < 5
            : DateTime.UtcNow - offeredAtUtc < TimeSpan.FromHours(5);
        if (offered)
        {
            return;
        }

        offeredForReset = offer.ResetsAt;
        offeredAtUtc = DateTime.UtcNow;
        this.LowPriorityOffered?.Invoke(this, offer);
    }

    /// <summary>Turns lower priority on for every chat until the 5-hour limit resets, and sends the last message again.</summary>
    public bool TryContinueAtLowPriority(LowPriorityOffer offer, out Task run)
    {
        run = Task.CompletedTask;
        LowPriorityMode.TurnOn(offer.ResetsAt);
        RefreshAllBanners();
        if (this.IsBusy || this.Messages.LastOrDefault(m => m.Kind == ChatEntryKind.User) is not ChatEntry last)
        {
            this.Status = "Lower priority is on. Send your message again when you're ready.";
            return false;
        }

        run = this.RetryAsync(last);
        return true;
    }

    /// <summary>Called by the view's timer: drops the banner once lower priority has ended.</summary>
    public void RefreshLowPriority()
    {
        if (this.showingLowPriority != LowPriorityMode.IsActive)
        {
            this.RefreshBanners();
        }
    }

    private NoticeBanner? LowPriorityBanner()
    {
        this.showingLowPriority = LowPriorityMode.IsActive;
        if (!this.showingLowPriority || LowPriorityMode.Until is not DateTimeOffset until)
        {
            return null;
        }

        return new NoticeBanner(
            "lowpriority:" + until.UtcTicks,
            $"Working at lower priority until {until.ToLocalTime():t}, using your weekly limit. Replies may pause or fail while Claude waits for spare capacity.",
            "Turn off",
            () =>
            {
                LowPriorityMode.TurnOff();
                RefreshAllBanners();
                this.Status = "Lower priority is off. Messages wait for your 5-hour limit to reset.";
            });
    }

    /// <summary>The offer's wording: when the 5-hour limit resets and how much of the weekly limit is left.</summary>
    internal static string DescribeOffer(LowPriorityOffer offer)
    {
        string reset = offer.ResetsAt is DateTimeOffset at
            ? $"Your 5-hour limit resets at {at.ToLocalTime():t}"
            : "You've reached your 5-hour limit";
        string weekly = offer.WeeklyUsed is double used
            ? $", and you still have {Math.Max(0, 100 - used):0}% of your weekly limit left."
            : ", but you still have some of your weekly limit left.";
        return reset + weekly
            + "\n\nClaude Code can carry on now at lower priority until then. Replies use your weekly limit, and may pause or fail when there's no spare capacity."
            + " This uses an option Claude Code only offers in its own terminal, so a Claude Code update could stop it working.";
    }
}
