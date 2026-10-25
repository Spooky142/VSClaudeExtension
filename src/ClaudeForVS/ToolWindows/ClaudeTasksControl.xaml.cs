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
        HighContrast.Follow(this);
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

    /// <summary>The output list's scroll viewer, from its template.</summary>
    private ScrollViewer? Output => this.OutputLines.Template?.FindName("Output", this.OutputLines) as ScrollViewer;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A running task is watched from its latest output; a finished one is read from the top.
        this.followOutput = this.List.SelectedItem is ToolTask { IsRunning: true };
        if (this.followOutput)
        {
            this.Output?.ScrollToEnd();
        }
        else
        {
            this.Output?.ScrollToHome();
        }
    }

    private void OnOutputScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer output)
        {
            return;
        }

        if (e.ExtentHeightChange != 0)
        {
            if (this.followOutput)
            {
                output.ScrollToEnd();
            }
        }
        else
        {
            this.followOutput = output.VerticalOffset >= output.ScrollableHeight - 2;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => (this.List.SelectedItem as ToolTask)?.RequestStop();

    private void OnClearFinished(object sender, RoutedEventArgs e) => this.viewModel?.ClearFinishedToolTasks();
}
