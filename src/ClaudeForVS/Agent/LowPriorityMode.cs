using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClaudeForVS.Agent;

/// <summary>When the 5-hour limit has run out but the weekly one hasn't: what the offer to carry on at lower priority shows.</summary>
/// <param name="ResetsAt">When the 5-hour limit resets, if known.</param>
/// <param name="WeeklyUsed">Percent of the weekly limit used, if known.</param>
internal sealed record LowPriorityOffer(DateTimeOffset? ResetsAt, double? WeeklyUsed);

/// <summary>
/// Claude Code's lower-priority mode: past the 5-hour limit, requests carry on against the weekly limit when there's
/// spare capacity. Claude Code only offers it in its interactive /low-priority command, which opts in by sending
/// "anthropic-usage-limit: slow" with each request, so Claude Code processes started while it's on get that header.
/// It's for the whole plan, so it's shared by every chat window, and lasts until the 5-hour limit resets.
/// </summary>
internal static class LowPriorityMode
{
    internal const string Header = "anthropic-usage-limit: slow";

    /// <summary>How long it stays on when the reset time isn't known: the length of the window.</summary>
    private static readonly TimeSpan DefaultLength = TimeSpan.FromHours(5);

    private static long untilTicks; // UTC ticks; 0 when off

    public static DateTimeOffset? Until => Get() is long ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public static bool IsActive => Get() is long ticks && DateTime.UtcNow.Ticks < ticks;

    public static void TurnOn(DateTimeOffset? resetsAt) =>
        System.Threading.Interlocked.Exchange(ref untilTicks, (resetsAt is DateTimeOffset r && r > DateTimeOffset.UtcNow ? r : DateTimeOffset.UtcNow + DefaultLength).UtcTicks);

    public static void TurnOff() => System.Threading.Interlocked.Exchange(ref untilTicks, 0);

    /// <summary>Adds the header to a Claude Code process's environment while the mode is on, keeping any custom headers already set.</summary>
    public static void Apply(ProcessStartInfo psi)
    {
        if (IsActive)
        {
            psi.EnvironmentVariables["ANTHROPIC_CUSTOM_HEADERS"] = WithHeader(psi.EnvironmentVariables["ANTHROPIC_CUSTOM_HEADERS"]);
        }
    }

    /// <summary>ANTHROPIC_CUSTOM_HEADERS (one "Name: Value" per line) with the header added.</summary>
    internal static string WithHeader(string? existing) =>
        string.IsNullOrWhiteSpace(existing) ? Header : existing!.TrimEnd('\r', '\n') + "\n" + Header;

    /// <summary>
    /// The offer to make, if the 5-hour limit has run out (and hasn't reset) while the weekly one still has room;
    /// otherwise null. Nothing is offered while the mode is already on, or if the weekly limit isn't known.
    /// </summary>
    public static LowPriorityOffer? Offer(IReadOnlyDictionary<string, PlanLimit> limits, DateTimeOffset now, bool active)
    {
        if (active
            || !limits.TryGetValue("five_hour", out PlanLimit? session) || !IsSpent(session)
            || session.ResetsAt is DateTimeOffset reset && reset <= now
            || !limits.TryGetValue("seven_day", out PlanLimit? week) || IsSpent(week))
        {
            return null;
        }

        return new LowPriorityOffer(session.ResetsAt, week.Utilization);
    }

    private static bool IsSpent(PlanLimit limit) => limit.Status == "rejected" || limit.Utilization >= 100;

    private static long? Get()
    {
        long ticks = System.Threading.Interlocked.Read(ref untilTicks);
        return ticks == 0 ? null : ticks;
    }
}
