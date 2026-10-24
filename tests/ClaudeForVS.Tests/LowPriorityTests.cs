using System;
using System.Collections.Generic;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class LowPriorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, PlanLimit> Limits(PlanLimit? session, PlanLimit? week)
    {
        var limits = new Dictionary<string, PlanLimit>(StringComparer.Ordinal);
        if (session != null)
        {
            limits[session.Window] = session;
        }

        if (week != null)
        {
            limits[week.Window] = week;
        }

        return limits;
    }

    private static PlanLimit Session(double? used, string status = "allowed", int resetInMinutes = 90) =>
        new("five_hour", used, Now.AddMinutes(resetInMinutes), status, Now);

    private static PlanLimit Week(double? used, string status = "allowed") => new("seven_day", used, Now.AddDays(3), status, Now);

    [Fact]
    public void OfferedWhenTheSessionLimitIsSpentButTheWeeklyOneIsnt()
    {
        LowPriorityOffer? offer = LowPriorityMode.Offer(Limits(Session(100), Week(62)), Now, active: false);
        Assert.Equal(new LowPriorityOffer(Now.AddMinutes(90), 62), offer);

        // A rejected request counts even without a percentage.
        Assert.NotNull(LowPriorityMode.Offer(Limits(Session(null, "rejected"), Week(null)), Now, active: false));
    }

    [Fact]
    public void NotOfferedOtherwise()
    {
        Assert.Null(LowPriorityMode.Offer(Limits(Session(80), Week(40)), Now, active: false)); // session limit not reached
        Assert.Null(LowPriorityMode.Offer(Limits(Session(100), Week(100)), Now, active: false)); // weekly spent too
        Assert.Null(LowPriorityMode.Offer(Limits(Session(100), Week(50, "rejected")), Now, active: false));
        Assert.Null(LowPriorityMode.Offer(Limits(Session(100), null), Now, active: false)); // weekly unknown
        Assert.Null(LowPriorityMode.Offer(Limits(Session(100, resetInMinutes: -1), Week(10)), Now, active: false)); // already reset
        Assert.Null(LowPriorityMode.Offer(Limits(Session(100), Week(10)), Now, active: true)); // already on
    }

    [Fact]
    public void HeaderIsAddedToAnyCustomHeaders()
    {
        Assert.Equal("anthropic-usage-limit: slow", LowPriorityMode.WithHeader(null));
        Assert.Equal("X-Team: tools\nanthropic-usage-limit: slow", LowPriorityMode.WithHeader("X-Team: tools\r\n"));
    }

    [Fact]
    public void OfferSaysWhatsLeft()
    {
        string text = ClaudeChatViewModel.DescribeOffer(new LowPriorityOffer(null, 62.4));
        Assert.StartsWith("You've reached your 5-hour limit, and you still have 38% of your weekly limit left.", text);
        Assert.Contains("lower priority", text);
    }
}
