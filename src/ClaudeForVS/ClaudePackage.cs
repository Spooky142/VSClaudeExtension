using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeForVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeForVS;

/// <summary>
/// In-process VS package: registers Tools &gt; Claude Chat and the chat tool window.
/// ProvideBindingPath lets the CLR find the Anthropic SDK and Markdig next to this assembly.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")] // with Solution Explorer
[ProvideBindingPath]
public sealed class ClaudePackage : AsyncPackage
{
    public const string PackageGuidString = "4b0f1f5e-7a3c-4d52-9f1e-2c6a8b9d0e41";
    private static readonly Guid CommandSet = new("9e3d2b1a-5c47-4f8e-a6b2-1d0c3e5f7a92");
    private const int ShowClaudeWindowCommandId = 0x0100;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await this.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(
                (_, _) => this.JoinableTaskFactory.RunAsync(() => this.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, this.DisposalToken)).Task.FileAndForget("ClaudeForVS/ShowWindow"),
                new CommandID(CommandSet, ShowClaudeWindowCommandId)));
        }
    }

    /// <summary>Returns the directory of the open solution or folder, or null.</summary>
    internal string? GetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.GetService(typeof(SVsSolution)) is IVsSolution solution
            && solution.GetSolutionInfo(out string dir, out _, out _) == 0
            && !string.IsNullOrEmpty(dir))
        {
            return dir;
        }

        return null;
    }
}
