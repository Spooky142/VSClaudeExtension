using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// A chat window's subagents and commands, each with its steps or output as it works. There's one per chat window,
/// with the same instance number.
/// </summary>
[Guid("b7d4c2e9-3f1a-4e8b-9c62-71a5d0f3e8b4")]
public sealed class ClaudeTasksWindow : ToolWindowPane
{
    private readonly ClaudeTasksControl tasks = new();

    public ClaudeTasksWindow()
        : base(null)
    {
        this.Caption = "Claude Tasks";
        this.Content = this.tasks;
    }

    internal ClaudeTasksControl Tasks => this.tasks;

    /// <summary>Shows the tasks of <paramref name="chat"/>.</summary>
    internal void Bind(ClaudeChatControl chat) => this.tasks.Bind(chat.ViewModel);

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.Frame is IVsWindowFrame frame
            && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_MultiInstanceToolNum, out object number))
            && number is int n)
        {
            this.Caption = n == 0 ? "Claude Tasks" : $"Claude ({n + 1}) Tasks";

            // Visual Studio can bring this window back with its layout before the chat it belongs to has opened; the chat
            // binds it when it does.
            if (this.Package is ClaudePackage package && package.FindToolWindow(typeof(ClaudeToolWindow), n, create: false) is ClaudeToolWindow { Chat: ClaudeChatControl chat })
            {
                this.Bind(chat);
            }
        }
    }
}
