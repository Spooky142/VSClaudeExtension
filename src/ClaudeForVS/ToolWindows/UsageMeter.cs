namespace ClaudeForVS.ToolWindows;

internal enum UsageSeverity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>One tile in the usage strip.</summary>
/// <param name="Label">What is measured, e.g. "5-hour limit".</param>
/// <param name="Value">Headline figure, e.g. "62%" or "$0.11".</param>
/// <param name="Detail">Secondary line, e.g. "resets in 2h 10m".</param>
/// <param name="Percent">Bar fill 0-100, or null for a text-only tile.</param>
internal sealed record UsageMeter(string Label, string Value, string Detail, double? Percent, UsageSeverity Severity)
{
    public bool HasBar => this.Percent.HasValue;

    public double BarValue => this.Percent ?? 0;

    /// <summary>Picks a severity from how full a window is and Claude Code's own status, if any.</summary>
    public static UsageSeverity SeverityFor(double? percent, string? status = null) =>
        status == "rejected" || percent >= 90 ? UsageSeverity.Critical
        : status == "allowed_warning" || percent >= 75 ? UsageSeverity.Warning
        : UsageSeverity.Normal;
}
