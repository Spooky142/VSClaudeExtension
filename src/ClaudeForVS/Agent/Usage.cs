using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>Tokens used by one API response.</summary>
internal readonly record struct TokenUsage(long Input, long CacheWrite, long CacheRead, long Output)
{
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>One Claude plan usage window (5-hour, weekly, ...) as last reported by Claude Code.</summary>
/// <param name="Window">Claude Code's window name, e.g. "five_hour", "seven_day", "seven_day_opus".</param>
/// <param name="Utilization">Percent of the window used, 0-100, if reported.</param>
/// <param name="Status">"allowed", "allowed_warning" or "rejected".</param>
internal sealed record PlanLimit(string Window, double? Utilization, DateTimeOffset? ResetsAt, string Status, DateTimeOffset ObservedAt);

/// <summary>The API's per-minute rate-limit headroom, from the most recent response's headers.</summary>
internal sealed record ApiRateLimits(
    long? RequestsLimit,
    long? RequestsRemaining,
    long? TokensLimit,
    long? TokensRemaining,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt)
{
    /// <summary>Percent of the most constrained limit (requests or tokens) used, 0-100.</summary>
    public double? PercentUsed
    {
        get
        {
            double? Used(long? limit, long? remaining) =>
                limit is > 0 && remaining is long r ? 100.0 * (1 - ((double)r / limit.Value)) : null;
            double?[] values = [Used(this.RequestsLimit, this.RequestsRemaining), Used(this.TokensLimit, this.TokensRemaining)];
            return values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max() is double max && !double.IsNaN(max)
                ? Math.Max(0, Math.Min(100, max))
                : null;
        }
    }
}

/// <summary>Published per-token prices, used for the session cost estimate (USD per million tokens).</summary>
internal static class ModelPricing
{
    public static double EstimateCost(ClaudeModel model, TokenUsage usage)
    {
        (double input, double output, double cacheRead, double cacheWrite) = model switch
        {
            ClaudeModel.Sonnet => (2.00, 10.00, 0.20, 2.50),
            ClaudeModel.Haiku => (1.00, 5.00, 0.10, 1.25),
            _ => (4.00, 20.00, 0.20, 5.00),
        };
        return ((usage.Input * input) + (usage.Output * output) + (usage.CacheRead * cacheRead) + (usage.CacheWrite * cacheWrite)) / 1_000_000;
    }
}

/// <summary>Remembers the last plan usage windows in %LOCALAPPDATA%\ClaudeForVS\usage.json so they show after a restart.</summary>
internal static class PlanUsageStore
{
    private static readonly string UsageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "usage.json");

    public static Dictionary<string, PlanLimit> Load()
    {
        try
        {
            if (File.Exists(UsageFile)
                && JsonSerializer.Deserialize<List<PlanLimit>>(File.ReadAllText(UsageFile)) is { } saved)
            {
                return saved.ToDictionary(l => l.Window, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new Dictionary<string, PlanLimit>(StringComparer.Ordinal);
    }

    public static void Save(IEnumerable<PlanLimit> limits)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsageFile)!);
            File.WriteAllText(UsageFile, JsonSerializer.Serialize(limits.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Reads the anthropic-ratelimit-* headers off every API response.</summary>
internal sealed class RateLimitHeaderHandler : DelegatingHandler
{
    private ApiRateLimits? latest;

    /// <summary>The most recent rate-limit snapshot, or null if no response has carried the headers yet.</summary>
    public ApiRateLimits? Latest => Volatile.Read(ref this.latest);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        long? Number(string name) =>
            response.Headers.TryGetValues(name, out IEnumerable<string>? values) && long.TryParse(values.FirstOrDefault(), out long n) ? n : null;
        DateTimeOffset? Time(string name) =>
            response.Headers.TryGetValues(name, out IEnumerable<string>? values) && DateTimeOffset.TryParse(values.FirstOrDefault(), out DateTimeOffset t) ? t : null;

        // "tokens" is the most restrictive of the input/output token limits; fall back to input tokens.
        long? tokensLimit = Number("anthropic-ratelimit-tokens-limit") ?? Number("anthropic-ratelimit-input-tokens-limit");
        long? tokensRemaining = Number("anthropic-ratelimit-tokens-remaining") ?? Number("anthropic-ratelimit-input-tokens-remaining");
        long? requestsLimit = Number("anthropic-ratelimit-requests-limit");
        if (tokensLimit != null || requestsLimit != null)
        {
            DateTimeOffset? reset = new[]
            {
                Time("anthropic-ratelimit-tokens-reset") ?? Time("anthropic-ratelimit-input-tokens-reset"),
                Time("anthropic-ratelimit-requests-reset"),
            }.Where(t => t.HasValue).OrderBy(t => t).FirstOrDefault();

            Volatile.Write(
                ref this.latest,
                new ApiRateLimits(requestsLimit, Number("anthropic-ratelimit-requests-remaining"), tokensLimit, tokensRemaining, reset, DateTimeOffset.Now));
        }

        return response;
    }
}
