using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeForVS.ToolWindows;
using Microsoft.VisualStudio;
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
[ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Float, Width = 560, Height = 760)] // its own floating window; can still be docked by dragging
[ProvideBindingPath]
public sealed class ClaudePackage : AsyncPackage
{
    public const string PackageGuidString = "4b0f1f5e-7a3c-4d52-9f1e-2c6a8b9d0e41";
    private static readonly Guid CommandSet = new("9e3d2b1a-5c47-4f8e-a6b2-1d0c3e5f7a92");
    private const int ShowClaudeWindowCommandId = 0x0100;

    private IVsSolution? solution;
    private uint solutionEventsCookie;

    /// <summary>
    /// Raised on the UI thread after a solution or folder is opened, with its directory.
    /// The chat window uses it to follow the open solution's repository.
    /// </summary>
    internal event EventHandler<string>? SolutionOpened;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await this.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(
                (_, _) => this.JoinableTaskFactory.RunAsync(() => this.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, this.DisposalToken)).Task.FileAndForget("ClaudeForVS/ShowWindow"),
                new CommandID(CommandSet, ShowClaudeWindowCommandId)));
        }

        this.solution = await this.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        this.solution?.AdviseSolutionEvents(new SolutionEvents(this), out this.solutionEventsCookie);
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS disposes packages on the main thread
        if (disposing && this.solution != null && this.solutionEventsCookie != 0)
        {
            this.solution.UnadviseSolutionEvents(this.solutionEventsCookie);
            this.solutionEventsCookie = 0;
        }

        base.Dispose(disposing);
    }

    /// <summary>Shows Visual Studio's folder picker. Returns the chosen folder, or null if cancelled.</summary>
    internal string? BrowseForFolder(IntPtr owner, string title, string? initialDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
        {
            return null;
        }

        const int MaxChars = 1024;
        IntPtr buffer = Marshal.AllocCoTaskMem(MaxChars * sizeof(char));
        try
        {
            var info = new[]
            {
                new VSBROWSEINFOW
                {
                    lStructSize = (uint)Marshal.SizeOf<VSBROWSEINFOW>(),
                    hwndOwner = owner,
                    pwzDlgTitle = title,
                    pwzInitialDir = initialDirectory,
                    pwzDirName = buffer,
                    nMaxDirName = MaxChars,
                },
            };
            return shell.GetDirectoryViaBrowseDlg(info) == VSConstants.S_OK ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private void RaiseSolutionOpened(string? directory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((directory ?? this.GetSolutionDirectory()) is string dir)
        {
            this.SolutionOpened?.Invoke(this, dir);
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

    /// <summary>Forwards "solution opened" and "folder opened" notifications to the package.</summary>
    private sealed class SolutionEvents(ClaudePackage package) : IVsSolutionEvents, IVsSolutionEvents7
    {
        // Solution events are raised on the main thread.
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package.RaiseSolutionOpened(null);
            return VSConstants.S_OK;
        }

        public void OnAfterOpenFolder(string folderPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package.RaiseSolutionOpened(folderPath);
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        public void OnBeforeCloseFolder(string folderPath)
        {
        }

        public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
        {
        }

        public void OnAfterCloseFolder(string folderPath)
        {
        }

        public void OnAfterLoadAllDeferredProjects()
        {
        }
    }
}
