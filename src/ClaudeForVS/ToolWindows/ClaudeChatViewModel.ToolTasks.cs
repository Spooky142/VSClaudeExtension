using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>Where the Claude Tasks window should go, and whether it should take focus.</summary>
internal sealed record TasksWindowRequest(ToolTask? Task, bool Activate);

// Subagents and commands, shown in this chat's Claude Tasks window while they run and after.
internal sealed partial class ClaudeChatViewModel
{
    // The tasks window keeps this many; the oldest finished ones go first.
    private const int MaxToolTasks = 100;

    // The tasks window has room for far more output than a row in the chat.
    private const int MaxTaskOutputChars = 50_000;

    // A command still running after this long opens the tasks window; quicker ones only appear in its list.
    private static readonly TimeSpan TasksWindowDelay = TimeSpan.FromSeconds(3);

    /// <summary>Newest first.</summary>
    public ObservableCollection<ToolTask> ToolTasks { get; } = [];

    public bool HasToolTasks => this.ToolTasks.Count > 0;

    public int RunningToolTasks => this.ToolTasks.Count(t => t.IsRunning);

    /// <summary>The header button's tooltip, e.g. "Claude Tasks: 2 running".</summary>
    public string ToolTasksTooltip => this.RunningToolTasks is int running and > 0 ? $"Claude Tasks: {running} running" : "Claude Tasks: subagents and commands Claude has run";

    /// <summary>Raised when the Claude Tasks window should be shown, e.g. as a subagent starts. Handled by the view.</summary>
    public event EventHandler<TasksWindowRequest>? TasksWindowRequested;

    /// <summary>Shows the Claude Tasks window, with <paramref name="task"/> selected if given.</summary>
    public void ShowToolTasks(ToolTask? task, bool activate) => this.TasksWindowRequested?.Invoke(this, new TasksWindowRequest(task, activate));

    public void ClearFinishedToolTasks()
    {
        foreach (ToolTask done in this.ToolTasks.Where(t => !t.IsRunning).ToList())
        {
            this.ToolTasks.Remove(done);
        }

        this.OnToolTasksChanged();
    }

    ILiveActivity IChatSink.StartLiveActivity(ActivityKind kind, string title, Action? stop, bool expanded)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var task = new ToolTask(kind, title, stop);
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolTask.IsRunning))
            {
                this.OnToolTasksChanged();
            }
        };

        this.ToolTasks.Insert(0, task);
        while (this.ToolTasks.Count > MaxToolTasks && this.ToolTasks.LastOrDefault(t => !t.IsRunning) is ToolTask oldest)
        {
            this.ToolTasks.Remove(oldest);
        }

        this.OnToolTasksChanged();
        var entry = new ChatEntry(ChatEntryKind.Tool, title, activity: kind) { LiveTask = task };
        this.Messages.Add(entry);

        // Never take focus from whatever the user is typing in.
        if (expanded)
        {
            this.ShowToolTasks(task, activate: false);
        }
        else
        {
            var timer = new DispatcherTimer { Interval = TasksWindowDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (task.IsRunning)
                {
                    this.ShowToolTasks(task, activate: false);
                }
            };
            timer.Start();
        }

        return new LiveActivity(this.Messages, entry, task);
    }

    /// <summary>
    /// Gives the finished call's report to its task in the tasks window. Calls report with the title they started
    /// with; the newest finished task of that title that hasn't had its report yet is the one.
    /// </summary>
    private void ReportToolTaskResult(ActivityKind kind, string title, string detail)
    {
        if (this.ToolTasks.FirstOrDefault(t => !t.IsRunning && !t.HasResult && t.Title == title) is ToolTask task)
        {
            task.SetResult(kind, detail.Length > MaxTaskOutputChars ? detail.Substring(0, MaxTaskOutputChars) + "\n…" : detail);
        }
    }

    // Keeps running tasks' times counting while any are running.
    private DispatcherTimer? toolTaskClock;

    private void OnToolTasksChanged()
    {
        this.OnPropertyChanged(nameof(this.HasToolTasks));
        this.OnPropertyChanged(nameof(this.RunningToolTasks));
        this.OnPropertyChanged(nameof(this.ToolTasksTooltip));

        bool running = this.ToolTasks.Any(t => t.IsRunning);
        if (running && this.toolTaskClock is not { IsEnabled: true })
        {
            this.toolTaskClock ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                foreach (ToolTask task in this.ToolTasks)
                {
                    task.Tick();
                }
            }, Dispatcher.CurrentDispatcher);
            this.toolTaskClock.Start();
        }
        else if (!running)
        {
            this.toolTaskClock?.Stop();
        }
    }

    /// <summary>A tool call while it runs: a row in the chat, removed when it finishes as the finished call gets its own row, and its task.</summary>
    private sealed class LiveActivity(ObservableCollection<ChatEntry> messages, ChatEntry entry, ToolTask task) : ILiveActivity
    {
        // Running output keeps its latest part, which is what's worth watching.
        public void SetOutput(string output) => task.SetOutput(output.Length > MaxTaskOutputChars ? "…\n" + output.Substring(output.Length - MaxTaskOutputChars) : output);

        public void Dispose()
        {
            messages.Remove(entry);
            task.Finish();
        }
    }
}
