using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

// Changing this GUID discards any layout VS saved for the window (e.g. when it used to dock with Solution Explorer).
[Guid("5e8f3a21-9c4d-4b7a-b16e-3d2f0a9c8e74")]
public sealed class ClaudeToolWindow : ToolWindowPane
{
    public ClaudeToolWindow()
        : base(null)
    {
        this.Caption = "Claude";
    }

    protected override void Initialize()
    {
        base.Initialize();
        var chat = new ClaudeChatControl((ClaudePackage)this.Package);
        this.Content = chat;

        // Edit.Find (Ctrl+F) goes to the active window's own commands first, so it searches the chat rather than
        // opening Visual Studio's Find dialog while the chat has focus.
        if (this.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(
                (_, _) => chat.OpenFind(),
                new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Find)));
        }
    }
}
