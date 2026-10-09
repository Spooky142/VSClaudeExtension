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

/// <summary>Claude's task list, shown above the prompt box.</summary>
internal sealed partial class ClaudeChatViewModel
{
    private bool tasksExpanded = true;

    /// <summary>Claude's latest task list (TodoWrite / todo_write), shown above the prompt box.</summary>
    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public bool HasTasks => this.Tasks.Count > 0;

    /// <summary>"Tasks · 2 of 5 done".</summary>
    public string TasksSummary => $"Tasks · {this.Tasks.Count(t => t.Status == "completed")} of {this.Tasks.Count} done";

    /// <summary>The task in progress, shown in the header while the list is collapsed.</summary>
    public string CurrentTask => this.Tasks.FirstOrDefault(t => t.Status == "in_progress")?.Display ?? string.Empty;

    public bool TasksExpanded
    {
        get => this.tasksExpanded;
        set => this.Set(ref this.tasksExpanded, value);
    }

    void IChatSink.ReportTasks(IReadOnlyList<TaskItem> tasks) => this.SetTasks(tasks);

    /// <summary>Replaces the task list; an empty list hides it.</summary>
    public void SetTasks(IReadOnlyList<TaskItem> tasks)
    {
        this.Tasks.Clear();
        foreach (TaskItem task in tasks)
        {
            this.Tasks.Add(task);
        }

        this.OnPropertyChanged(nameof(this.HasTasks));
        this.OnPropertyChanged(nameof(this.TasksSummary));
        this.OnPropertyChanged(nameof(this.CurrentTask));
    }
}
