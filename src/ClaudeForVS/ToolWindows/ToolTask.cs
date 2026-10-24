using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

internal enum ToolTaskState
{
    Running,
    Finished,

    /// <summary>The user stopped it.</summary>
    Stopped,
}

/// <summary>
/// A subagent or command shown in the Claude Tasks window: its steps or output as it works, then how it ended. The chat
/// shows a one-line row for it while it runs, and its usual finished row afterwards.
/// </summary>
internal sealed class ToolTask : INotifyPropertyChanged
{
    private readonly Func<DateTime> now;
    private ToolTaskState state;
    private bool stopRequested;
    private DateTime? ended;

    public ToolTask(ActivityKind activity, string title, Action? stop, Func<DateTime>? clock = null)
    {
        this.now = clock ?? (() => DateTime.UtcNow);
        this.Activity = activity;
        this.Title = title;
        this.StopAction = stop;
        this.Started = this.now();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActivityKind Activity { get; private set; }

    public string Title { get; }

    public DateTime Started { get; }

    public ToolTaskState State
    {
        get => this.state;
        private set
        {
            this.state = value;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.IsRunning));
            this.OnPropertyChanged(nameof(this.CanStop));
            this.OnPropertyChanged(nameof(this.StatusText));
        }
    }

    public bool IsRunning => this.state == ToolTaskState.Running;

    /// <summary>True once the finished call's own report (e.g. a subagent's answer) has replaced the live output.</summary>
    public bool HasResult { get; private set; }

    /// <summary>The steps or output so far, then the finished call's report.</summary>
    public IReadOnlyList<DiffLine> DetailLines { get; private set; } = [];

    public bool HasDetail => this.DetailLines.Count > 0;

    /// <summary>"Running · 12s", "Finished in 1m 05s" or "Stopped after 4s".</summary>
    public string StatusText
    {
        get
        {
            TimeSpan took = (this.ended ?? this.now()) - this.Started;
            return this.state switch
            {
                ToolTaskState.Running => this.stopRequested ? "Stopping…" : $"Running · {FormatDuration(took)}",
                ToolTaskState.Stopped => $"Stopped after {FormatDuration(took)}",
                _ => this.Activity == ActivityKind.Failed ? $"Failed after {FormatDuration(took)}" : $"Finished in {FormatDuration(took)}",
            };
        }
    }

    // ===== Stopping =====

    /// <summary>Stops just this task; null if it can't be stopped on its own (e.g. Claude Code's subagents).</summary>
    public Action? StopAction { get; }

    public bool CanStop => this.StopAction != null && this.IsRunning && !this.stopRequested;

    public void RequestStop()
    {
        if (this.CanStop)
        {
            this.stopRequested = true;
            this.OnPropertyChanged(nameof(this.CanStop));
            this.OnPropertyChanged(nameof(this.StatusText));
            this.StopAction!();
        }
    }

    // ===== Progress =====

    /// <summary>Replaces the output shown so far.</summary>
    public void SetOutput(string output)
    {
        this.DetailLines = DiffLine.Parse(output, this.Activity);
        this.OnPropertyChanged(nameof(this.DetailLines));
        this.OnPropertyChanged(nameof(this.HasDetail));
    }

    /// <summary>The task has stopped running; its live output stays until <see cref="SetResult"/> replaces it.</summary>
    public void Finish()
    {
        if (this.IsRunning)
        {
            this.ended = this.now();
            this.State = this.stopRequested ? ToolTaskState.Stopped : ToolTaskState.Finished;
        }
    }

    /// <summary>Shows the finished call's report, as it appears in the chat.</summary>
    public void SetResult(ActivityKind kind, string detail)
    {
        this.HasResult = true;
        this.Activity = kind;
        this.OnPropertyChanged(nameof(this.Activity));
        if (detail.Length > 0)
        {
            this.SetOutput(detail);
        }

        this.OnPropertyChanged(nameof(this.StatusText));
    }

    /// <summary>Re-reads <see cref="StatusText"/>, so a running task's time keeps counting.</summary>
    public void Tick()
    {
        if (this.IsRunning)
        {
            this.OnPropertyChanged(nameof(this.StatusText));
        }
    }

    /// <summary>"4s", "1m 05s" or "1h 02m".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{(int)span.TotalSeconds}s";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
