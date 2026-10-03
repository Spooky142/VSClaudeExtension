using System;
using System.Runtime.InteropServices;
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
        this.Content = new ClaudeChatControl((ClaudePackage)this.Package);
    }
}
