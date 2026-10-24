using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeForVS.ToolWindows;

// Changing this GUID discards any layout VS saved for the window (e.g. when it used to dock with Solution Explorer).
[Guid("5e8f3a21-9c4d-4b7a-b16e-3d2f0a9c8e74")]
public sealed class ClaudeToolWindow : ToolWindowPane
{
    private ClaudeChatControl? chat;

    public ClaudeToolWindow()
        : base(null)
    {
        this.Caption = "Claude";
    }

    /// <summary>Which of the chat windows this is: 0 for the first, then 1, 2… for ones opened alongside it.</summary>
    internal int InstanceNumber { get; private set; }

    internal ClaudeChatControl? Chat => this.chat;

    protected override void Initialize()
    {
        base.Initialize();
        this.chat = new ClaudeChatControl((ClaudePackage)this.Package, this);
        this.Content = this.chat;

        // Edit.Find (Ctrl+F) goes to the active window's own commands first, so it searches the chat rather than
        // opening Visual Studio's Find dialog while the chat has focus.
        if (this.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(
                (_, _) => this.chat.OpenFind(),
                new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Find)));
        }
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.Frame is IVsWindowFrame frame
            && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_MultiInstanceToolNum, out object number))
            && number is int n)
        {
            this.InstanceNumber = n;
            this.Caption = n == 0 ? "Claude" : $"Claude ({n + 1})";

            // Its tasks window may have come back with Visual Studio's layout first.
            if (this.chat != null && this.Package is ClaudePackage package && package.FindToolWindow(typeof(ClaudeTasksWindow), n, create: false) is ClaudeTasksWindow tasks)
            {
                tasks.Bind(this.chat);
            }
        }
    }
}
