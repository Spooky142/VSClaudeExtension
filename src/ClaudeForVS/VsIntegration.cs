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
internal sealed record ErrorListItem(string Severity, string Code, string Description, string? File, int Line, string? Project);

/// <summary>
/// Everything the chat window needs from Visual Studio itself: the editor, the Error List, the diff viewer and the
/// main window. Every member must be called on the UI thread.
/// </summary>
internal sealed class VsIntegration
{
    private readonly IServiceProvider services;

    public VsIntegration(IServiceProvider services)
    {
        this.services = services;
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
        if (!selected)
        {
            entries = table.Entries.ToList();
        }

        var items = new List<ErrorListItem>();
        foreach (ITableEntryHandle entry in entries)
        {
            string severity = entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out object sev) && sev is __VSERRORCATEGORY category
                ? category switch { __VSERRORCATEGORY.EC_ERROR => "error", __VSERRORCATEGORY.EC_WARNING => "warning", _ => "message" }
                : "error";
            if (!selected && severity != "error")
            {
                continue;
            }

            items.Add(new ErrorListItem(
                severity,
                entry.TryGetValue(StandardTableKeyNames.ErrorCode, out object code) ? code as string ?? string.Empty : string.Empty,
                entry.TryGetValue(StandardTableKeyNames.Text, out object text) ? text as string ?? string.Empty : string.Empty,
                entry.TryGetValue(StandardTableKeyNames.DocumentName, out object file) ? file as string : null,
                entry.TryGetValue(StandardTableKeyNames.Line, out object line) && line is int l ? l + 1 : 0,
                entry.TryGetValue(StandardTableKeyNames.ProjectName, out object project) ? project as string : null));
        }

        return items;
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
