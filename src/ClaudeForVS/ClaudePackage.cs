using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeForVS.ToolWindows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
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
[ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Float, Width = 560, Height = 760, MultiInstances = true)] // floating windows; can still be docked by dragging
[ProvideToolWindow(typeof(ClaudeTasksWindow), Style = VsDockStyle.Float, Width = 720, Height = 480, MultiInstances = true)]
[ProvideBindingPath]
[ProvideOptionPage(typeof(OptionsPage), "Claude", "General", 0, 0, true)]
public sealed class ClaudePackage : AsyncPackage
{
    public const string PackageGuidString = "4b0f1f5e-7a3c-4d52-9f1e-2c6a8b9d0e41";
    private static readonly Guid CommandSet = new("9e3d2b1a-5c47-4f8e-a6b2-1d0c3e5f7a92");
    private const int ShowClaudeWindowCommandId = 0x0100;
    private const int AskAboutSelectionCommandId = 0x0101;
    private const int ExplainCommandId = 0x0102;
    private const int FixCommandId = 0x0103;
    private const int WriteTestsCommandId = 0x0104;
    private const int FixErrorsCommandId = 0x0105;
    private const int NewChatWindowCommandId = 0x0106;
    private const int FixTestsCommandId = 0x0107;
    private const int AddToClaudeCommandId = 0x0108;
    private const int WriteCommitMessageCommandId = 0x0109;
    private const int AskAboutDebugStateCommandId = 0x010A;
    private const int EditInlineCommandId = 0x010B;

    // Chat windows beyond this many are refused; each one can run its own Claude.
    private const int MaxChatWindows = 20;

    private IVsSolution? solution;
    private uint solutionEventsCookie;

    /// <summary>The package, once it has loaded. The light bulb lives in an editor extension that can run before it has.</summary>
    internal static ClaudePackage? Instance { get; private set; }

    /// <summary>Loads the package if it isn't yet, and returns it (null if it couldn't be loaded).</summary>
    internal static async System.Threading.Tasks.Task<ClaudePackage?> LoadAsync()
    {
        if (Instance is null)
        {
            object? shell = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (shell is IVsShell7 loader)
            {
                Guid package = new(PackageGuidString);
                await loader.LoadPackageAsync(ref package);
            }
        }

        return Instance;
    }

    /// <summary>
    /// The light bulb's Claude actions. Fix attaches the errors and warnings on the line (from the Error List) and
    /// asks Claude to fix them; the others attach the code and ask about it, as the editor's Claude menu does.
    /// </summary>
    internal void RunLightBulbAction(LightBulb.LightBulbAction action, LightBulb.LightBulbContext context)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var editor = new EditorContext(context.FilePath, context.StartLine, context.EndLine, context.Text);
        IReadOnlyList<ErrorListItem> problems = action == LightBulb.LightBulbAction.Fix
            ? this.Vs.GetAllDiagnostics()
                .Where(i => i.Severity is "error" or "warning" && string.Equals(i.File, context.FilePath, StringComparison.OrdinalIgnoreCase)
                    && i.Line >= context.CaretStartLine && i.Line <= context.CaretEndLine)
                .ToList()
            : [];

        this.JoinableTaskFactory.RunAsync(async () =>
        {
            if (await this.ShowChatAsync() is not ClaudeChatControl chat)
            {
                return;
            }

            switch (action)
            {
                case LightBulb.LightBulbAction.Fix when problems.Count > 0:
                    chat.AskAbout(editor, null);
                    chat.FixErrors(problems);
                    break;
                case LightBulb.LightBulbAction.Fix:
                    // The Error List had nothing for these lines (it can lag behind the squiggles).
                    chat.AskAbout(editor, context.CaretStartLine == context.CaretEndLine
                        ? $"Find and fix the problem on line {context.CaretStartLine}."
                        : $"Find and fix the problems on lines {context.CaretStartLine}-{context.CaretEndLine}.");
                    break;
                case LightBulb.LightBulbAction.Explain:
                    chat.AskAbout(editor, "Explain this code.");
                    break;
                case LightBulb.LightBulbAction.WriteTests:
                    chat.AskAbout(editor, "Write unit tests for this code, following the conventions of the existing tests in this repository.");
                    break;
                case LightBulb.LightBulbAction.Document:
                    chat.AskAbout(editor, "Add XML documentation comments to this code: a summary, and parameters, returns and exceptions where they apply. " +
                        "Follow the style of the documentation elsewhere in this repository, and don't change the code itself.");
                    break;
            }
        }).Task.FileAndForget("ClaudeForVS/LightBulbAction");
    }

    /// <summary>The chat window used most recently. Commands from the editor, Error List and so on go to it.</summary>
    internal ClaudeChatControl? LastActiveChat { get; set; }

    /// <summary>
    /// Raised on the UI thread after a solution or folder is opened, with its directory.
    /// The chat window uses it to follow the open solution's repository.
    /// </summary>
    internal event EventHandler<string>? SolutionOpened;

    /// <summary>The editor, Error List, diff viewer and main window. Available once the package has initialized.</summary>
    internal VsIntegration Vs { get; private set; } = null!;

    /// <summary>Windows notifications for every chat window: one notification area icon between them.</summary>
    internal DesktopNotifier Notifier { get; } = new();

    // .NET Framework allows 2 connections per host by default, which would queue a chat's parallel subagents (each
    // streaming its own response from the API) behind one another.
    private const int MinConnectionsPerHost = 16;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Only ever raised: Visual Studio or another extension may have set it higher already.
        if (System.Net.ServicePointManager.DefaultConnectionLimit < MinConnectionsPerHost)
        {
            System.Net.ServicePointManager.DefaultConnectionLimit = MinConnectionsPerHost;
        }

        // The services are fetched while still on the background thread Visual Studio loads the package on, so its UI
        // thread only does what has to happen there.
        object? componentModel = await this.GetServiceAsync(typeof(SComponentModel));
        object? commandService = await this.GetServiceAsync(typeof(IMenuCommandService));
        object? solutionService = await this.GetServiceAsync(typeof(SVsSolution));

        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Instance = this;
        this.Vs = new VsIntegration(this);
        Agent.OpenDocuments.HasUnsavedChanges = this.Vs.HasUnsavedChanges;
        Agent.OpenDocuments.UnsavedText = this.Vs.GetUnsavedText;
        Agent.OpenDocuments.WriteToEditor = this.Vs.WriteToEditor;
        Agent.VisualStudioHost.Current = this.Vs;
        Agent.PackageHostRegistry.Current = new VsPackageHost(this);
        if (componentModel is IComponentModel components)
        {
            Agent.CodeIntelligenceHost.Current = new VsCodeIntelligence(components);
        }

        if (commandService is OleMenuCommandService commands)
        {
            this.AddCommand(commands, ShowClaudeWindowCommandId, null, chat => chat.FocusPrompt());
            this.AddCommand(commands, NewChatWindowCommandId, () => this.JoinableTaskFactory.RunAsync(this.OpenNewChatWindowAsync).Task.FileAndForget("ClaudeForVS/NewChatWindow"));
            this.AddCommand(
                commands,
                WriteCommitMessageCommandId,
                () => this.JoinableTaskFactory.RunAsync(this.WriteCommitMessageAsync).Task.FileAndForget("ClaudeForVS/CommitMessage"),
                () => (Git.GitRunner.FindRepoRoot(this.GetSolutionDirectory()) ?? Git.GitRunner.FindRepoRoot(this.LastActiveChat?.RepoPath)) is null ? Availability.Disabled : Availability.Enabled);

            // Test Explorer's selection is copied while it's still the active window. (It isn't looked at beforehand, as
            // that means going through the clipboard.)
            this.AddCommand(commands, FixTestsCommandId, () => this.Vs.CopyTestExplorerSelection(), (chat, tests) => chat.FixTests(tests));
            this.AddCommand(
                commands, AddToClaudeCommandId, () => this.Vs.GetSolutionExplorerSelection(), (chat, items) => chat.AddSolutionItems(items),
                () => this.Vs.GetSolutionExplorerSelection().Count > 0 ? Availability.Enabled : Availability.Disabled);

            // Editor commands read the selection before the chat window takes focus. They're only shown in the editor of a
            // file (not, say, a diff's or the Output window's), so the Claude submenu goes from the others.
            Func<Availability> inFileEditor = () => this.Vs.GetActiveTextView() is null ? Availability.Hidden : Availability.Enabled;
            this.AddCommand(commands, AskAboutSelectionCommandId, () => this.Vs.GetEditorContext(), (chat, editor) => chat.AskAbout(editor, null), inFileEditor);
            this.AddCommand(commands, ExplainCommandId, () => this.Vs.GetEditorContext(), (chat, editor) => chat.AskAbout(editor, "Explain this code."), inFileEditor);
            this.AddCommand(commands, FixCommandId, () => this.Vs.GetEditorContext(), (chat, editor) => chat.AskAbout(editor, "Find and fix any bugs in this code."), inFileEditor);
            this.AddCommand(commands, WriteTestsCommandId, () => this.Vs.GetEditorContext(), (chat, editor) =>
                chat.AskAbout(editor, "Write unit tests for this code, following the conventions of the existing tests in this repository."), inFileEditor);
            this.AddCommand(
                commands, FixErrorsCommandId, () => this.Vs.GetErrors(), (chat, errors) => chat.FixErrors(errors),
                () => this.Vs.GetErrors().Count > 0 ? Availability.Enabled : Availability.Disabled);

            // The debugger's state is read before the chat takes focus, as the user may be about to step. Hidden when
            // nothing is being debugged, and not available until it's paused.
            this.AddCommand(
                commands, AskAboutDebugStateCommandId, () => this.Vs.GetDebugState(), (chat, state) => chat.AskAboutDebugState(state),
                () => this.Vs.GetDebugMode() switch
                {
                    Agent.DebugMode.Paused => Availability.Enabled,
                    Agent.DebugMode.Running => Availability.Disabled,
                    _ => Availability.Hidden,
                });

            // Edits in the editor itself; the chat window isn't involved.
            this.AddCommand(commands, EditInlineCommandId, this.EditInline, inFileEditor);
        }

        this.solution = solutionService as IVsSolution;
        this.solution?.AdviseSolutionEvents(new SolutionEvents(this), out this.solutionEventsCookie);
    }

    /// <summary>Whether a command shows on its menus and can be chosen, as worked out when a menu with it opens.</summary>
    private enum Availability
    {
        Hidden,
        Disabled,
        Enabled,
    }

    private void AddCommand(OleMenuCommandService commands, int id, Func<object?>? capture, Action<ClaudeChatControl> run) =>
        this.AddCommand<object?>(commands, id, capture ?? (() => null), (chat, _) => run(chat));

    /// <summary>Adds a command that captures something (on the UI thread, before anything changes focus), then shows the chat and acts on it.</summary>
    /// <param name="available">Whether it applies just now; null if it always does.</param>
    private void AddCommand<T>(OleMenuCommandService commands, int id, Func<T> capture, Action<ClaudeChatControl, T> run, Func<Availability>? available = null) =>
        this.AddCommand(
            commands,
            id,
            () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                T captured = capture();
                this.JoinableTaskFactory.RunAsync(async () =>
                {
                    if (await this.ShowChatAsync() is ClaudeChatControl chat)
                    {
                        run(chat, captured);
                    }
                }).Task.FileAndForget("ClaudeForVS/Command");
            },
            available);

    /// <summary>
    /// Adds a command. Until the package has loaded, Visual Studio shows every command as the .vsct has it (visible and
    /// enabled, so choosing one loads the package); after that, <paramref name="available"/> decides.
    /// </summary>
    private void AddCommand(OleMenuCommandService commands, int id, Action execute, Func<Availability>? available = null)
    {
        var command = new OleMenuCommand((_, _) => execute(), new CommandID(CommandSet, id));
        if (available != null)
        {
            command.BeforeQueryStatus += (_, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Availability now;
                try
                {
                    now = available();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    now = Availability.Enabled; // when in doubt, let the command itself say what's wrong
                }

                command.Visible = now != Availability.Hidden;
                command.Enabled = now == Availability.Enabled;
            };
        }

        commands.AddCommand(command);
    }
    /// <summary>Edit with Claude (Ctrl+Alt+/): asks what to change at the caret and edits the code in place.</summary>
    private void EditInline()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.Vs.GetActiveTextView() is not (Microsoft.VisualStudio.Text.Editor.IWpfTextView view, string path))
        {
            this.Vs.RequestAttention("Put the caret in a code file to edit it with Claude.");
            return;
        }

        // The repository and settings of the chat used last, as the chat would edit with them; else the file's own repository.
        string repoRoot = this.LastActiveChat?.ViewModel.RepoRoot is string chatRoot && path.StartsWith(chatRoot, StringComparison.OrdinalIgnoreCase)
            ? chatRoot
            : Git.GitRunner.FindRepoRoot(System.IO.Path.GetDirectoryName(path)) ?? System.IO.Path.GetDirectoryName(path)!;
        Agent.AgentSettings settings = this.LastActiveChat?.ViewModel.Settings ?? Agent.AgentSettings.Load();
        this.JoinableTaskFactory.RunAsync(() => Editor.InlineEditSession.StartAsync(view, path, repoRoot, settings)).Task.FileAndForget("ClaudeForVS/EditInline");
    }

    /// <summary>Shows the chat window used most recently (the first one if none has been used yet) and returns its chat.</summary>
    private async System.Threading.Tasks.Task<ClaudeChatControl?> ShowChatAsync()
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        if (this.LastActiveChat?.Pane.Frame is IVsWindowFrame frame && ErrorHandler.Succeeded(frame.Show()))
        {
            return this.LastActiveChat;
        }

        ToolWindowPane pane = await this.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, this.DisposalToken);
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        return pane.Content as ClaudeChatControl;
    }

    /// <summary>
    /// Opens another chat window alongside the others, reusing one that was closed while idle. Each window has its own
    /// chat, repository and Claude, so they can work at the same time.
    /// </summary>
    internal System.Threading.Tasks.Task OpenNewChatWindowAsync() => this.OpenChatWindowAsync();

    /// <summary>Opens a chat window for a branch of a chat (see <see cref="ClaudeChatViewModel.BranchFrom"/>), with its message in the prompt box.</summary>
    internal async System.Threading.Tasks.Task OpenBranchAsync(ClaudeForVS.ToolWindows.SessionInfo info, string prompt)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        ClaudeChatControl? chat = await this.OpenChatWindowAsync();
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        if (chat != null)
        {
            await chat.OpenBranchAsync(info, prompt);
        }
    }

    /// <summary>
    /// Shows the Claude Tasks window of <paramref name="chat"/> (it has the chat window's instance number), with the
    /// requested task selected. Unless asked to, it doesn't take focus, so it can open while the user is typing.
    /// </summary>
    internal void ShowTasksWindow(ClaudeChatControl chat, TasksWindowRequest request)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.FindToolWindow(typeof(ClaudeTasksWindow), chat.Pane.InstanceNumber, create: true) is not ClaudeTasksWindow { Frame: IVsWindowFrame frame } window)
        {
            return;
        }

        window.Bind(chat);
        window.Tasks.Select(request.Task);
        _ = request.Activate ? frame.Show() : frame.ShowNoActivate();
    }

    /// <summary>Opens a chat window (see <see cref="OpenNewChatWindowAsync"/>) and returns its chat, or null if there are too many.</summary>
    private async System.Threading.Tasks.Task<ClaudeChatControl?> OpenChatWindowAsync()
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        for (int id = 1; id < MaxChatWindows; id++)
        {
            if (this.FindToolWindow(typeof(ClaudeToolWindow), id, create: false) is ClaudeToolWindow existing)
            {
                bool hidden = existing.Frame is IVsWindowFrame f && f.IsVisible() != VSConstants.S_OK;
                if (!hidden || existing.Chat is not { IsIdle: true } closedChat)
                {
                    continue;
                }

                closedChat.StartFresh();
                ((IVsWindowFrame)existing.Frame).Show();
                closedChat.FocusPrompt();
                return closedChat;
            }

            ToolWindowPane pane = await this.ShowToolWindowAsync(typeof(ClaudeToolWindow), id, create: true, this.DisposalToken);
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
            ClaudeChatControl? opened = pane.Content as ClaudeChatControl;
            opened?.FocusPrompt();
            return opened;
        }

        this.Vs.RequestAttention($"There are already {MaxChatWindows} chat windows open. Close one first.");
        return null;
    }

    /// <summary>Git > Write Commit Message with Claude: describes the changes and puts the message in the Git Changes window.</summary>
    private async System.Threading.Tasks.Task WriteCommitMessageAsync()
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
        string? repo = Git.GitRunner.FindRepoRoot(this.GetSolutionDirectory()) ?? Git.GitRunner.FindRepoRoot(this.LastActiveChat?.RepoPath);
        if (repo is null)
        {
            this.Vs.RequestAttention("Open a solution or folder in a git repository to write a commit message.");
            return;
        }

        IVsStatusbar? statusBar = await this.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        object icon = (short)Constants.SBAI_General;
        statusBar?.Animation(1, ref icon);
        statusBar?.SetText("Claude is writing a commit message…");
        try
        {
            string message = await System.Threading.Tasks.Task.Run(() => Agent.CommitMessageWriter.GenerateAsync(Agent.AgentSettings.Load(), repo, this.DisposalToken));
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(this.DisposalToken);
            if (await this.Vs.SetCommitMessageAsync(message))
            {
                statusBar?.SetText("Claude wrote a commit message in Git Changes. Check it before you commit.");
            }
            else
            {
                System.Windows.Clipboard.SetText(message);
                statusBar?.SetText("Claude's commit message is on the clipboard. Paste it into Git Changes.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception
            or Anthropic.Exceptions.AnthropicException or System.Net.Http.HttpRequestException or COMException or OperationCanceledException)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync();
            statusBar?.SetText("Claude couldn't write a commit message: " + ex.Message);
            this.Vs.ShowWarning("Claude couldn't write a commit message.\n\n" + ex.Message);
        }
        finally
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync();
            statusBar?.Animation(0, ref icon);
        }
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS disposes packages on the main thread
        if (disposing && this.solution != null && this.solutionEventsCookie != 0)
        {
            this.solution.UnadviseSolutionEvents(this.solutionEventsCookie);
            this.solutionEventsCookie = 0;
        }

        if (disposing)
        {
            this.Notifier.Dispose();
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

