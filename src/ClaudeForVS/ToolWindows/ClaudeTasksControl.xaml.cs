using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeForVS.ToolWindows;

/// <summary>The Claude Tasks window's content: a chat's subagents and commands, and the selected one's steps or output.</summary>
public partial class ClaudeTasksControl : UserControl
{
    /// <summary>True while the output is scrolled to the bottom; new output then keeps it there.</summary>
    private bool followOutput = true;

    private ClaudeChatViewModel? viewModel;

    public ClaudeTasksControl()
    {
        this.InitializeComponent();
    }

    internal void Bind(ClaudeChatViewModel chat)
    {
        if (ReferenceEquals(this.viewModel, chat))
        {
            return;
        }

        if (this.viewModel != null)
        {
            this.viewModel.ToolTasks.CollectionChanged -= this.OnTasksChanged;
        }

        this.viewModel = chat;
        this.DataContext = chat;
        chat.ToolTasks.CollectionChanged += this.OnTasksChanged;
        this.Select(null);
    }

    /// <summary>Selects <paramref name="task"/>, or with none, keeps the selection (selecting the newest task if there's none).</summary>
    internal void Select(ToolTask? task)
    {
        if (task != null)
        {
            this.List.SelectedItem = task;
            this.List.ScrollIntoView(task);
        }
        else if (this.List.SelectedItem is null && this.List.Items.Count > 0)
        {
            this.List.SelectedIndex = 0;
        }
    }

    // A cleared or trimmed list may have taken the selected task with it.
    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => this.Select(null);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A running task is watched from its latest output; a finished one is read from the top.
        this.followOutput = this.List.SelectedItem is ToolTask { IsRunning: true };
        if (this.followOutput)
        {
            this.Output.ScrollToEnd();
        }
        else
        {
            this.Output.ScrollToHome();
        }
    }

    private void OnOutputScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (this.followOutput)
            {
                this.Output.ScrollToEnd();
            }
        }
        else
        {
            this.followOutput = this.Output.VerticalOffset >= this.Output.ScrollableHeight - 2;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => (this.List.SelectedItem as ToolTask)?.RequestStop();

    private void OnClearFinished(object sender, RoutedEventArgs e) => this.viewModel?.ClearFinishedToolTasks();
}
