using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClaudeForVS.Agent;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace ClaudeForVS;

/// <summary>The document open in the code editor, and what's selected in it.</summary>
/// <param name="FullPath">The document's path.</param>
/// <param name="StartLine">First selected line (1-based), or the caret line when nothing is selected.</param>
/// <param name="EndLine">Last selected line (1-based).</param>
/// <param name="SelectedText">The selection; empty when nothing is selected.</param>
internal sealed record EditorContext(string FullPath, int StartLine, int EndLine, string SelectedText)
{
    public bool HasSelection => this.SelectedText.Trim().Length > 0;
}

/// <summary>One row of the Error List.</summary>
/// <param name="Source">What reported it, e.g. "Build" or "IntelliSense"; null if unknown.</param>
internal sealed record ErrorListItem(string Severity, string Code, string Description, string? File, int Line, string? Project, string? Source = null);

/// <summary>A file, folder or project selected in Solution Explorer.</summary>
/// <param name="IsFolder">True for folders, and for projects (given as their folder).</param>
internal sealed record SolutionItem(string FullPath, bool IsFolder, string? ProjectFile);

/// <summary>
/// Everything the chat window needs from Visual Studio itself: the editor, the Error List, the diff viewer and the
/// main window. Every member must be called on the UI thread.
/// </summary>
internal sealed class VsIntegration : IVisualStudioHost
{
    private readonly IServiceProvider services;

    public VsIntegration(IServiceProvider services)
    {
        this.services = services;
    }

    public async System.Threading.Tasks.Task<string?> GetSolutionDirectoryAsync(System.Threading.CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return this.services.GetService(typeof(SVsSolution)) is IVsSolution solution
            && solution.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK
            && !string.IsNullOrEmpty(dir)
            ? dir
            : null;
    }

    public async System.Threading.Tasks.Task<IReadOnlyList<ErrorListItem>> GetDiagnosticsAsync(System.Threading.CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return this.GetAllDiagnostics();
    }

    private DTE2? Dte
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return this.services.GetService(typeof(DTE)) as DTE2;
        }
    }

    /// <summary>The active code document and its selection, or null when no text document is active.</summary>
    public EditorContext? GetEditorContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Document? doc = this.Dte?.ActiveDocument;
            if (doc is null || string.IsNullOrEmpty(doc.FullName) || doc.Selection is not TextSelection selection)
            {
                return null;
            }

            int top = selection.TopPoint.Line;
            int bottom = selection.BottomPoint.Line;

            // A selection ending at the start of a line doesn't include that line.
            if (bottom > top && selection.BottomPoint.LineCharOffset == 1)
            {
                bottom--;
            }

            return new EditorContext(doc.FullName, top, bottom, selection.Text ?? string.Empty);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The code editor used last and the file in it; null when it isn't a file's editor.</summary>
    public (Microsoft.VisualStudio.Text.Editor.IWpfTextView View, string FullPath)? GetActiveTextView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.services.GetService(typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager)) is not Microsoft.VisualStudio.TextManager.Interop.IVsTextManager manager
            || ErrorHandler.Failed(manager.GetActiveView(0, null, out Microsoft.VisualStudio.TextManager.Interop.IVsTextView vsView)) || vsView is null
            || this.services.GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel)) is not Microsoft.VisualStudio.ComponentModelHost.IComponentModel components)
        {
            return null;
        }

        Microsoft.VisualStudio.Text.Editor.IWpfTextView? view = components.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
        return view != null
            && components.GetService<Microsoft.VisualStudio.Text.ITextDocumentFactoryService>().TryGetTextDocument(view.TextDataModel.DocumentBuffer, out Microsoft.VisualStudio.Text.ITextDocument document)
            && !string.IsNullOrEmpty(document.FilePath)
            ? (view, document.FilePath)
            : null;
    }

    /// <summary>Opens a file in the editor with the caret on the given line (1-based; 0 leaves it where it was).</summary>
    public bool OpenFile(string fullPath, int line, int endLine = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!File.Exists(fullPath))
        {
            return false;
        }

        try
        {
            VsShellUtilities.OpenDocument(this.services, fullPath);
            if (line > 0 && this.Dte?.ActiveDocument?.Selection is TextSelection selection)
            {
                selection.GotoLine(line, Select: false);
                if (endLine > line)
                {
                    selection.LineDown(Extend: true, Count: endLine - line + 1);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True if the file is open in an editor with changes that haven't been saved.</summary>
    public bool HasUnsavedChanges(string fullPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.services.GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable table
            || ErrorHandler.Failed(table.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, fullPath, out _, out _, out IntPtr docData, out _))
            || docData == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(docData) is IVsPersistDocData persist
                && ErrorHandler.Succeeded(persist.IsDocDataDirty(out int dirty))
                && dirty != 0;
        }
        finally
        {
            Marshal.Release(docData);
        }
    }

    /// <summary>
    /// Opens Visual Studio's side-by-side diff for a change. When the file on disk already matches the "after"
    /// side, the real file is shown on the right so it can be edited there.
    /// </summary>
    /// <param name="afterLabel">What the right-hand side is called when it isn't the file on disk.</param>
    public void ShowDiff(FileChange change, string caption, string afterLabel = "proposed")
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.services.GetService(typeof(SVsDifferenceService)) is not IVsDifferenceService diff)
        {
            return;
        }

        string name = Path.GetFileName(change.FullPath);
        string folder = Path.Combine(Path.GetTempPath(), "ClaudeForVS", "diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        string left = Path.Combine(folder, "before-" + name);
        File.WriteAllText(left, change.Before);
        var options = __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;

        string right = change.FullPath;
        string rightLabel = $"{name} (now)";
        if (!File.Exists(change.FullPath) || !string.Equals(FileChange.ReadOrEmpty(change.FullPath), change.After, StringComparison.Ordinal))
        {
            right = Path.Combine(folder, "after-" + name);
            File.WriteAllText(right, change.After);
            options |= __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
            rightLabel = $"{name} ({afterLabel})";
        }

        diff.OpenComparisonWindow2(left, right, caption, change.FullPath, $"{name} (before)", rightLabel, caption, null, (uint)options);
    }

    /// <summary>The rows selected in the Error List, or every error if none are selected.</summary>
    public IReadOnlyList<ErrorListItem> GetErrors()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.services.GetService(typeof(SVsErrorList)) is not IErrorList errorList)
        {
            return [];
        }

        IWpfTableControl table = errorList.TableControl;
        List<ITableEntryHandle> entries = table.SelectedEntries.ToList();
        bool selected = entries.Count > 0;
        return ToItems(selected ? entries : table.Entries).Where(i => selected || i.Severity == "error").ToList();
    }

    /// <summary>Every row in the Error List, whatever its filter shows: build errors and the live errors in open files.</summary>
    public IReadOnlyList<ErrorListItem> GetAllDiagnostics()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return this.services.GetService(typeof(SVsErrorList)) is IErrorList errorList ? ToItems(errorList.TableControl.Entries).ToList() : [];
    }

    private static IEnumerable<ErrorListItem> ToItems(IEnumerable<ITableEntryHandle> entries)
    {
        foreach (ITableEntryHandle entry in entries)
        {
            string severity = entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out object sev) && sev is __VSERRORCATEGORY category
                ? category switch { __VSERRORCATEGORY.EC_ERROR => "error", __VSERRORCATEGORY.EC_WARNING => "warning", _ => "message" }
                : "error";

            yield return new ErrorListItem(
                severity,
                entry.TryGetValue(StandardTableKeyNames.ErrorCode, out object code) ? code as string ?? string.Empty : string.Empty,
                entry.TryGetValue(StandardTableKeyNames.Text, out object text) ? text as string ?? string.Empty : string.Empty,
                entry.TryGetValue(StandardTableKeyNames.DocumentName, out object file) ? file as string : null,
                entry.TryGetValue(StandardTableKeyNames.Line, out object line) && line is int l ? l + 1 : 0,
                entry.TryGetValue(StandardTableKeyNames.ProjectName, out object project) ? project as string : null,
                entry.TryGetValue(StandardTableKeyNames.BuildTool, out object tool) ? tool as string : null);
        }
    }

    /// <summary>
    /// Builds the open solution (Build > Build Solution) and waits for it to finish. Cancelling cancels the build.
    /// Throws <see cref="InvalidOperationException"/> if no solution is open or a build is already running.
    /// </summary>
    public async System.Threading.Tasks.Task<BuildOutcome> BuildSolutionAsync(System.Threading.CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        DTE2 dte = this.Dte ?? throw new InvalidOperationException("Visual Studio's automation model isn't available.");
        if (dte.Solution is not Solution solution || !solution.IsOpen)
        {
            throw new InvalidOperationException("No solution is open in Visual Studio.");
        }

        SolutionBuild build = solution.SolutionBuild;
        if (build.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            throw new InvalidOperationException("A build is already running in Visual Studio. Wait for it to finish, then try again.");
        }

        var done = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        BuildEvents events = dte.Events.BuildEvents; // held so the event source isn't collected while waiting
        _dispBuildEvents_OnBuildDoneEventHandler onDone = (_, _) => done.TrySetResult(true);
        events.OnBuildDone += onDone;
        try
        {
            build.Build(WaitForBuildToFinish: false);
            using (cancellationToken.Register(() => done.TrySetResult(false)))
            {
                await done.Task;
            }
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            events.OnBuildDone -= onDone;
            if (cancellationToken.IsCancellationRequested && build.BuildState == vsBuildState.vsBuildStateInProgress)
            {
                try
                {
                    dte.ExecuteCommand("Build.Cancel");
                }
                catch (COMException)
                {
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        int failed = build.LastBuildInfo;

        // The Error List fills in just after the build reports it's done.
        await System.Threading.Tasks.Task.Delay(750, cancellationToken);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return failed == 0
            ? new BuildOutcome(true, "Build succeeded.")
            : new BuildOutcome(false, $"Build failed: {failed} project{(failed == 1 ? "" : "s")} didn't build.");
    }

    /// <summary>The name and text of the Output window's active pane, or null if there's none or it can't be read.</summary>
    public (string Name, string Text)? GetOutputWindowText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (this.Dte?.Windows.Item(EnvDTE.Constants.vsWindowKindOutput)?.Object is OutputWindow output
                && output.ActivePane is OutputWindowPane pane)
            {
                TextDocument document = pane.TextDocument;
                string text = document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
                return (pane.Name ?? string.Empty, text);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NullReferenceException or InvalidCastException)
        {
        }

        return null;
    }

    /// <summary>The files open in the editor, and whether each has unsaved changes.</summary>
    public IReadOnlyList<(string Path, bool Unsaved)> GetOpenDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<(string, bool)>();
        try
        {
            if (this.Dte?.Documents is Documents documents)
            {
                foreach (Document document in documents)
                {
                    if (!string.IsNullOrEmpty(document.FullName) && File.Exists(document.FullName))
                    {
                        result.Add((document.FullName, !document.Saved));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
        }

        return result;
    }

    public async System.Threading.Tasks.Task<ActiveDocument?> GetActiveDocumentAsync(System.Threading.CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (this.GetEditorContext() is not EditorContext editor)
        {
            return null;
        }

        int caretLine = editor.StartLine;
        int caretColumn = 1;
        try
        {
            if (this.Dte?.ActiveDocument?.Selection is TextSelection selection)
            {
                caretLine = selection.ActivePoint.Line;
                caretColumn = selection.ActivePoint.DisplayColumn;
            }
        }
        catch (COMException)
        {
        }

        // The lines scrolled into view, from the editor view that was last active (the one the document is shown in).
        int first = 0, last = 0;
        if (this.services.GetService(typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager)) is Microsoft.VisualStudio.TextManager.Interop.IVsTextManager manager
            && ErrorHandler.Succeeded(manager.GetActiveView(0, null, out Microsoft.VisualStudio.TextManager.Interop.IVsTextView view)) && view != null
            && ErrorHandler.Succeeded(view.GetScrollInfo(1, out _, out _, out int visible, out int top)))
        {
            first = top + 1;
            last = top + Math.Max(1, visible);
        }

        IReadOnlyList<(string Path, bool Unsaved)> open = this.GetOpenDocuments();
        return new ActiveDocument(
            editor.FullPath,
            open.Any(d => string.Equals(d.Path, editor.FullPath, StringComparison.OrdinalIgnoreCase) && d.Unsaved),
            caretLine,
            caretColumn,
            first,
            last,
            editor.StartLine,
            editor.EndLine,
            editor.SelectedText,
            open.Select(d => (d.Path, d.Unsaved)).ToList());
    }

    public async System.Threading.Tasks.Task<DebugState> GetDebugStateAsync(System.Threading.CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return this.GetDebugState();
    }

    /// <summary>True while the debugger is stopped at a breakpoint, an exception or a step.</summary>
    public bool IsDebuggerPaused
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return this.Dte?.Debugger?.CurrentMode == dbgDebugMode.dbgBreakMode;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>The debugger's state; when paused, the exception, call stack and the current frame's arguments and locals.</summary>
    public DebugState GetDebugState()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Debugger? debugger = this.Dte?.Debugger;
        if (debugger is null)
        {
            return DebugState.NotDebugging;
        }

        try
        {
            switch (debugger.CurrentMode)
            {
                case dbgDebugMode.dbgDesignMode:
                    return DebugState.NotDebugging;
                case dbgDebugMode.dbgRunMode:
                    return new DebugState(DebugMode.Running, null, null, null, [], [], [], null, ProgramName(debugger));
            }

            string? reason = debugger.LastBreakReason switch
            {
                dbgEventReason.dbgEventReasonBreakpoint => "breakpoint hit",
                dbgEventReason.dbgEventReasonStep => "stepped",
                dbgEventReason.dbgEventReasonUserBreak => "paused by the user",
                dbgEventReason.dbgEventReasonExceptionThrown => "exception thrown",
                dbgEventReason.dbgEventReasonExceptionNotHandled => "unhandled exception",
                _ => null,
            };

            string? exceptionType = null;
            string? exceptionMessage = null;
            if (debugger.LastBreakReason is dbgEventReason.dbgEventReasonExceptionThrown or dbgEventReason.dbgEventReasonExceptionNotHandled)
            {
                // Function evaluation can be refused (e.g. in native code or while another thread is mid-call), so each part
                // falls back to something cheaper: the expression's own type, then its displayed value.
                exceptionType = Evaluate(debugger, "$exception.GetType().FullName") is string typeName ? typeName.Trim('"') : Evaluate(debugger, "$exception", wantType: true);
                exceptionMessage = Evaluate(debugger, "$exception.Message") is string message ? message.Trim('"')
                    : Evaluate(debugger, "$exception") is string shown ? shown.Trim('{', '}', '"') : null;
                if (exceptionType is null && exceptionMessage is null)
                {
                    exceptionType = "(the exception's details couldn't be read; see the Exception Helper or the Locals window)";
                }
            }

            var stack = new List<DebugFrame>();
            try
            {
                foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
                {
                    stack.Add(ToFrame(frame));
                    if (stack.Count >= DebugReport.MaxFrames)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or NullReferenceException or InvalidCastException)
            {
                // Some frames (or the whole stack) can't be read, e.g. in native code.
            }

            int? threads = null;
            try
            {
                threads = debugger.CurrentProgram?.Threads?.Count;
            }
            catch (Exception ex) when (ex is COMException or NullReferenceException)
            {
            }

            StackFrame? current = debugger.CurrentStackFrame;
            return new DebugState(
                DebugMode.Paused,
                reason,
                exceptionType,
                exceptionMessage,
                stack,
                current is null ? [] : ToValues(current.Arguments),
                current is null ? [] : ToValues(current.Locals),
                threads,
                ProgramName(debugger));
        }
        catch (COMException)
        {
            return DebugState.NotDebugging;
        }
    }

    private static string? ProgramName(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return debugger.CurrentProgram?.Name is { Length: > 0 } name ? Path.GetFileName(name) : null;
        }
        catch (Exception ex) when (ex is COMException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Evaluates an expression in the paused program: its value, or its type if <paramref name="wantType"/>; null if it can't be evaluated.</summary>
    private static string? Evaluate(Debugger debugger, string expression, bool wantType = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            EnvDTE.Expression result = debugger.GetExpression(expression, false, 2000);
            return result.IsValidValue ? (wantType ? result.Type : result.Value) : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    private static DebugFrame ToFrame(StackFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? file = null;
        int line = 0;
        if (frame is StackFrame2 detailed)
        {
            try
            {
                file = string.IsNullOrEmpty(detailed.FileName) ? null : detailed.FileName;
                line = (int)detailed.LineNumber;
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException)
            {
            }
        }

        return new DebugFrame(frame.FunctionName ?? "(unknown)", file, line, frame.Module);
    }

    private static List<DebugValue> ToValues(Expressions expressions)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var values = new List<DebugValue>();
        try
        {
            foreach (EnvDTE.Expression expression in expressions)
            {
                values.Add(new DebugValue(expression.Name ?? string.Empty, expression.Type ?? string.Empty, expression.Value ?? string.Empty));
                if (values.Count >= 60)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or NullReferenceException)
        {
        }

        return values;
    }

    /// <summary>The files, folders and projects selected in Solution Explorer (in solution or folder view).</summary>
    public IReadOnlyList<SolutionItem> GetSolutionExplorerSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<SolutionItem>();
        if (this.services.GetService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection monitor
            || ErrorHandler.Failed(monitor.GetCurrentSelection(out IntPtr hierarchyPtr, out uint itemId, out IVsMultiItemSelect multi, out IntPtr containerPtr)))
        {
            return result;
        }

        try
        {
            IVsHierarchy? single = hierarchyPtr == IntPtr.Zero ? null : Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
            var selected = new List<(IVsHierarchy? Hierarchy, uint ItemId)>();
            if (itemId == VSConstants.VSITEMID_SELECTION && multi != null
                && ErrorHandler.Succeeded(multi.GetSelectionInfo(out uint count, out _)) && count > 0)
            {
                var items = new VSITEMSELECTION[count];
                if (ErrorHandler.Succeeded(multi.GetSelectedItems(0, count, items)))
                {
                    selected.AddRange(items.Select(i => (i.pHier ?? single, i.itemid)));
                }
            }
            else
            {
                selected.Add((single, itemId));
            }

            foreach ((IVsHierarchy? hierarchy, uint id) in selected)
            {
                string? path = null;
                if (hierarchy is null || id == VSConstants.VSITEMID_NIL)
                {
                    continue;
                }

                if (hierarchy is IVsSolution solution)
                {
                    path = solution.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK ? dir : null;
                }
                else if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(id, out string name)))
                {
                    path = name;
                }

                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                path = path!.TrimEnd('\\', '/');
                if (Directory.Exists(path))
                {
                    result.Add(new SolutionItem(path, true, null));
                }
                else if (File.Exists(path))
                {
                    string extension = Path.GetExtension(path);
                    bool isProject = extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
                    result.Add(isProject ? new SolutionItem(Path.GetDirectoryName(path)!, true, path) : new SolutionItem(path, false, null));
                }
            }
        }
        finally
        {
            if (hierarchyPtr != IntPtr.Zero)
            {
                Marshal.Release(hierarchyPtr);
            }

            if (containerPtr != IntPtr.Zero)
            {
                Marshal.Release(containerPtr);
            }
        }

        return result.Distinct().ToList();
    }

    /// <summary>
    /// What Test Explorer's Copy puts on the clipboard for the selected tests (names, outcomes, messages and stack
    /// traces), or null if nothing was copied. Call while Test Explorer is the active window; the clipboard is put
    /// back as it was.
    /// </summary>
    public string? CopyTestExplorerSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.Dte is not DTE2 dte)
        {
            return null;
        }

        DataObject? saved = SnapshotClipboard();
        try
        {
            Clipboard.Clear();
            dte.ExecuteCommand("Edit.Copy");
            return Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (saved != null)
                {
                    Clipboard.SetDataObject(saved, copy: true);
                }
                else
                {
                    Clipboard.Clear();
                }
            }
            catch (ExternalException)
            {
            }
        }
    }

    /// <summary>A copy of whatever is on the clipboard, format by format, or null if it's empty or busy.</summary>
    private static DataObject? SnapshotClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is not IDataObject current || current.GetFormats(autoConvert: false) is not { Length: > 0 } formats)
            {
                return null;
            }

            var copy = new DataObject();
            foreach (string format in formats)
            {
                try
                {
                    if (current.GetData(format, autoConvert: false) is object data)
                    {
                        copy.SetData(format, data);
                    }
                }
                catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException or InvalidOperationException)
                {
                    // Some formats (e.g. delayed-render ones) can't be read back; leave them out.
                }
            }

            return copy;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the Git Changes window and puts <paramref name="message"/> in its commit message box. Returns false if the
    /// box couldn't be found (it isn't a public API, so this may stop working in a later Visual Studio).
    /// </summary>
    public async System.Threading.Tasks.Task<bool> SetCommitMessageAsync(string message)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (this.services.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
        {
            return false;
        }

        Guid gitChanges = GitChangesWindow;
        if (ErrorHandler.Failed(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref gitChanges, out IVsWindowFrame frame)) || frame is null)
        {
            try
            {
                this.Dte?.ExecuteCommand("View.GitChanges");
            }
            catch (COMException)
            {
                return false;
            }

            if (ErrorHandler.Failed(shell.FindToolWindow(0, ref gitChanges, out frame)) || frame is null)
            {
                return false;
            }
        }

        frame.Show();

        // Give the window a moment to lay out its content after being shown.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await System.Threading.Tasks.Task.Delay(150);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            object? content = ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object view))
                ? view is ToolWindowPane pane ? pane.Content : view
                : null;
            if (content is DependencyObject root && FindCommitMessageBox(root) is object box)
            {
                switch (box)
                {
                    case System.Windows.Controls.TextBox textBox:
                        textBox.Text = message;
                        textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
                        textBox.Focus();
                        textBox.CaretIndex = message.Length;
                        return true;
                    case Microsoft.VisualStudio.Text.Editor.ITextView textView:
                        using (Microsoft.VisualStudio.Text.ITextEdit edit = textView.TextBuffer.CreateEdit())
                        {
                            edit.Replace(0, textView.TextBuffer.CurrentSnapshot.Length, message);
                            edit.Apply();
                        }

                        return true;
                }
            }
        }

        return false;
    }

    private static readonly Guid GitChangesWindow = new("1c64b9c2-e352-428e-a56d-0ace190b99a6");

    /// <summary>The Git Changes window's commit message box: an editor, or failing that a multi-line text box, preferring one named for the message.</summary>
    private static object? FindCommitMessageBox(DependencyObject root)
    {
        System.Windows.Controls.TextBox? fallback = null;
        var queue = new Queue<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (!seen.Add(node))
            {
                continue;
            }

            if (node is Microsoft.VisualStudio.Text.Editor.ITextView view && !view.Options.GetOptionValue(Microsoft.VisualStudio.Text.Editor.DefaultTextViewOptions.ViewProhibitUserInputId))
            {
                return view;
            }

            if (node is System.Windows.Controls.TextBox { IsReadOnly: false, AcceptsReturn: true } box)
            {
                string name = box.Name + " " + System.Windows.Automation.AutomationProperties.GetName(box);
                if (name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("commit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return box;
                }

                fallback ??= box;
            }

            int children = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(node) : 0;
            for (int i = 0; i < children; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }

            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d)
                {
                    queue.Enqueue(d);
                }
            }
        }

        return fallback;
    }

    /// <summary>True while one of Visual Studio's windows is in the foreground.</summary>
    public static bool IsActive()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    /// <summary>
    /// Flashes Visual Studio's taskbar button until it's brought to the front, and shows the message in its
    /// status bar. Does nothing to the taskbar while Visual Studio is already in front.
    /// </summary>
    public void RequestAttention(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.services.GetService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
        {
            statusBar.SetText("Claude: " + message);
        }

        if (IsActive() || Application.Current?.MainWindow is not System.Windows.Window main)
        {
            return;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(main).Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
