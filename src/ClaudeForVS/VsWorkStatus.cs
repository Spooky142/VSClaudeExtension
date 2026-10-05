using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TaskStatusCenter;
using Microsoft.VisualStudio.Threading;

namespace ClaudeForVS;

/// <summary>
/// Claude's work as a background task in Visual Studio's status bar: the spinner at the bottom left, with what Claude is
/// doing and a Cancel button, so it's visible with the chat window closed or behind other windows.
/// </summary>
internal sealed class WorkStatus : IDisposable
{
    private readonly ITaskHandler handler;
    private readonly TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellation;

    private WorkStatus(ITaskHandler handler, Action cancel)
    {
        this.handler = handler;

        // Cancel is raised on whatever thread Visual Studio likes; stopping the chat belongs on the UI thread.
        this.cancellation = handler.UserCancellation.Register(() => StopAsync(cancel).FileAndForget("ClaudeForVS/WorkCancel"));
        handler.RegisterTask(this.finished.Task);
    }

    /// <summary>Shows the task. Returns null if Visual Studio has no task status center. Call on the UI thread.</summary>
    /// <param name="cancel">Called when the user clicks Cancel on the task.</param>
    public static WorkStatus? Begin(IServiceProvider services, string title, string text, Action cancel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (services.GetService(typeof(SVsTaskStatusCenterService)) is not IVsTaskStatusCenterService center)
        {
            return null;
        }

        var options = new TaskHandlerOptions { Title = title, ActionsAfterCompletion = CompletionActions.None };
        ITaskHandler handler = center.PreRegister(options, Progress(text));
        return new WorkStatus(handler, cancel);
    }

    /// <summary>Changes the line of text under the task's title.</summary>
    public void Update(string text) => this.handler.Progress.Report(Progress(text));

    public void Dispose()
    {
        this.cancellation.Dispose();
        this.finished.TrySetResult(true);
    }

    private static async Task StopAsync(Action cancel)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        cancel();
    }

    private static TaskProgressData Progress(string text) => new() { CanBeCanceled = true, ProgressText = text, PercentComplete = null };
}

