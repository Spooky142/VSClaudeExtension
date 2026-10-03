using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

[Guid("c7a1e2d3-4b5f-4a6e-8d9c-0f1e2a3b4c5d")]
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
