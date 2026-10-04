using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using ClaudeForVS.Agent;
using ClaudeForVS.Markdown;

namespace ClaudeForVS.ToolWindows;

internal enum AttachmentKind
{
    /// <summary>The document open in the editor and where the caret is; attached automatically.</summary>
    ActiveFile,

    /// <summary>Code selected in the editor, sent from the editor's context menu.</summary>
    Selection,

    /// <summary>Rows from the Error List.</summary>
    Errors,

    Image,

    /// <summary>Tests from Test Explorer, with their outcomes, messages and stack traces.</summary>
    Tests,

    /// <summary>A file from Solution Explorer, with its contents.</summary>
    File,

    /// <summary>A folder or project from Solution Explorer, with the files in it.</summary>
    Folder,
}

/// <summary>Reading what Test Explorer's Copy puts on the clipboard.</summary>
internal static class TestExplorerText
{
    /// <summary>How many tests the text describes ("Test Name:" lines); at least 1 for any non-empty text.</summary>
    public static int CountTests(string text)
    {
        int count = text.Replace("\r\n", "\n").Split('\n').Count(l => l.TrimStart().StartsWith("Test Name:", StringComparison.OrdinalIgnoreCase));
        return Math.Max(text.Trim().Length > 0 ? 1 : 0, count);
    }

    /// <summary>The tests' names, from "Test Name:" lines.</summary>
    public static IReadOnlyList<string> Names(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.StartsWith("Test Name:", StringComparison.OrdinalIgnoreCase))
        .Select(l => l.Substring("Test Name:".Length).Trim())
        .Where(n => n.Length > 0)
        .ToList();
}

/// <summary>Something attached to the next prompt, shown as a chip above the prompt box.</summary>
internal sealed class Attachment
{
    private const int MaxSelectionChars = 40_000;

    private Attachment(AttachmentKind kind, string label, string toolTip, string context)
    {
        this.Kind = kind;
        this.Label = label;
        this.ToolTip = toolTip;
        this.Context = context;
    }

    public AttachmentKind Kind { get; }

    /// <summary>Chip text, e.g. "Foo.cs:12-30".</summary>
    public string Label { get; }

    public string ToolTip { get; }

    /// <summary>What's added to the prompt for Claude; empty for images, which are sent as images.</summary>
    public string Context { get; }

    public PromptImage? Image { get; private init; }

    public ImageSource? Thumbnail { get; private init; }

    public bool HasThumbnail => this.Thumbnail != null;

    /// <summary>Icon glyph (Segoe Fluent Icons) for the chip.</summary>
    public string Glyph => this.Kind switch
    {
        AttachmentKind.Selection => "", // code
        AttachmentKind.Errors => "", // error
        AttachmentKind.Image => "", // picture
        AttachmentKind.Tests => "", // bug
        AttachmentKind.Folder => "", // folder
        _ => "", // document
    };

    // A file attached from Solution Explorer is sent whole up to this size; beyond it Claude is told to read it itself.
    private const int MaxAttachedFileChars = 60_000;

    // Most files listed for an attached folder.
    private const int MaxFolderFiles = 300;

    /// <summary>Tests copied from Test Explorer.</summary>
    public static Attachment ForTests(string details)
    {
        int count = TestExplorerText.CountTests(details);
        IReadOnlyList<string> names = TestExplorerText.Names(details);
        string text = details.Length > MaxSelectionChars ? details.Substring(0, MaxSelectionChars) + "\n… (truncated)" : details;
        string context = "Tests selected in Visual Studio's Test Explorer, as it copies them (names, outcomes, messages and stack traces):\n"
            + Fence(text.TrimEnd(), "tests.txt");
        string label = count == 1 && names.Count == 1 ? ShortTestName(names[0]) : $"{count} tests";
        string tip = names.Count > 0
            ? string.Join("\n", names.Take(8)) + (names.Count > 8 ? $"\n… and {names.Count - 8} more" : string.Empty)
            : "Tests from Test Explorer";
        return new Attachment(AttachmentKind.Tests, label, tip, context);
    }

    /// <summary>"Namespace.Class.Method(args)" → "Method(args)".</summary>
    internal static string ShortTestName(string name)
    {
        int paren = name.IndexOf('(');
        string withoutArgs = paren > 0 ? name.Substring(0, paren) : name;
        int dot = withoutArgs.LastIndexOf('.');
        return dot >= 0 && dot < withoutArgs.Length - 1 ? name.Substring(dot + 1) : name;
    }

    /// <summary>A file from Solution Explorer: its contents, or just its path if it's large or binary.</summary>
    public static Attachment ForFile(string fullPath, string repoRoot)
    {
        string path = DisplayPath(fullPath, repoRoot);
        string? text = null;
        try
        {
            if (new FileInfo(fullPath).Length <= MaxAttachedFileChars * 4)
            {
                text = RepoFiles.Decode(File.ReadAllBytes(fullPath), path).Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolInputException)
        {
            // Unreadable or binary: refer to it by path only.
        }

        string context = text is null || text.Length > MaxAttachedFileChars
            ? $"The user attached `{path}` (too large or not text to include here; read it if you need its contents)."
            : $"The user attached `{path}`:\n" + Fence(text, fullPath);
        return new Attachment(AttachmentKind.File, Path.GetFileName(fullPath), path, context);
    }

    /// <summary>A folder (or a project, given its project file) from Solution Explorer, listing the files in it.</summary>
    /// <param name="repoFiles">The repository's files (repository-relative, forward slashes), for folders inside it.</param>
    public static Attachment ForFolder(string fullPath, string? projectFile, string repoRoot, IReadOnlyList<string> repoFiles)
    {
        string root = repoRoot.TrimEnd('\\', '/');
        string dir = fullPath.TrimEnd('\\', '/');
        bool isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
        bool inRepo = isRoot || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string folder = isRoot ? string.Empty : inRepo ? DisplayPath(dir, repoRoot) : dir;

        List<string> files;
        if (inRepo)
        {
            files = (isRoot ? repoFiles : repoFiles.Where(f => f.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        }
        else
        {
            try
            {
                files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Take(MaxFolderFiles + 1).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = [];
            }
        }

        string shown = isRoot ? "the repository root" : inRepo ? $"`{folder}/`" : $"`{dir}`";
        var context = new StringBuilder();
        if (projectFile != null)
        {
            context.Append($"The user attached the project `{DisplayPath(projectFile, repoRoot)}` (folder {shown}).");
            try
            {
                string projectText = File.ReadAllText(projectFile);
                if (projectText.Length <= MaxAttachedFileChars / 2)
                {
                    context.Append('\n').Append(Fence(projectText, projectFile));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        else
        {
            context.Append($"The user attached the folder {shown}.");
        }

        if (files.Count > 0)
        {
            context.Append($"\nFiles in it{(files.Count > MaxFolderFiles ? $" (first {MaxFolderFiles} of {files.Count:N0})" : string.Empty)}:\n");
            context.Append(string.Join("\n", files.Take(MaxFolderFiles)));
        }

        string label = projectFile != null ? Path.GetFileName(projectFile) : Path.GetFileName(dir) + "/";
        string tip = (projectFile != null ? DisplayPath(projectFile, repoRoot) : isRoot ? root : folder)
            + $"\n{(files.Count == 1 ? "1 file" : $"{files.Count:N0} files")} listed for Claude";
        return new Attachment(AttachmentKind.Folder, label, tip, context.ToString());
    }

    /// <summary>The editor's document and caret or selection, attached automatically. The selection is included if there is one.</summary>
    public static Attachment ForActiveFile(EditorContext editor, string repoRoot)
    {
        string path = DisplayPath(editor.FullPath, repoRoot);
        string lines = LineRange(editor);
        var context = new StringBuilder();
        if (editor.HasSelection)
        {
            context.Append($"The user has lines {editor.StartLine}-{editor.EndLine} of `{path}` selected in the editor:\n");
            context.Append(Fence(editor.SelectedText, editor.FullPath));
        }
        else
        {
            context.Append($"The user has `{path}` open in the editor, with the cursor on line {editor.StartLine}.");
        }

        return new Attachment(
            AttachmentKind.ActiveFile,
            $"{Path.GetFileName(editor.FullPath)}{(editor.HasSelection ? ":" + lines : string.Empty)}",
            $"{path}{(editor.HasSelection ? $" (lines {lines} selected)" : $" (line {editor.StartLine})")}\nThe open file is sent with your message. Click × to leave it out.",
            context.ToString());
    }

    /// <summary>A selection sent from the editor's context menu.</summary>
    public static Attachment ForSelection(EditorContext editor, string repoRoot)
    {
        string path = DisplayPath(editor.FullPath, repoRoot);
        string lines = LineRange(editor);
        string context = $"Lines {lines} of `{path}`:\n" + Fence(editor.SelectedText, editor.FullPath);
        return new Attachment(AttachmentKind.Selection, $"{Path.GetFileName(editor.FullPath)}:{lines}", $"{path}, lines {lines}", context);
    }

    /// <summary>Rows from the Error List.</summary>
    public static Attachment ForErrors(IReadOnlyList<ErrorListItem> errors, string repoRoot)
    {
        var context = new StringBuilder("Errors from Visual Studio's Error List:\n");
        foreach (ErrorListItem error in errors)
        {
            string where = error.File is null ? string.Empty : $"{DisplayPath(error.File, repoRoot)}{(error.Line > 0 ? $":{error.Line}" : string.Empty)}: ";
            string code = error.Code.Length > 0 ? $"{error.Code} " : string.Empty;
            string project = string.IsNullOrEmpty(error.Project) ? string.Empty : $" [{error.Project}]";
            context.Append($"- {where}{error.Severity} {code}{error.Description}{project}\n");
        }

        int errorCount = errors.Count(e => e.Severity == "error");
        string label = errorCount == errors.Count
            ? $"{errors.Count} {(errors.Count == 1 ? "error" : "errors")}"
            : $"{errors.Count} Error List {(errors.Count == 1 ? "item" : "items")}";
        string tip = string.Join("\n", errors.Take(8).Select(e => $"{e.Code} {e.Description}".Trim())) + (errors.Count > 8 ? $"\n… and {errors.Count - 8} more" : string.Empty);
        return new Attachment(AttachmentKind.Errors, label, tip, context.ToString().TrimEnd());
    }

    public static Attachment ForImage(PromptImage image, ImageSource? thumbnail, string name) =>
        new(AttachmentKind.Image, name, $"{name} · {image.Data.Length / 1024:N0} KB", string.Empty) { Image = image, Thumbnail = thumbnail };

    /// <summary>
    /// Builds what's sent to Claude for a prompt: the user's text followed by the attached context, plus any images,
    /// and the note shown under the prompt in the transcript.
    /// </summary>
    public static (ChatPrompt Prompt, string Note) Compose(string text, IReadOnlyList<Attachment> attachments)
    {
        var full = new StringBuilder(text);
        List<Attachment> withContext = attachments.Where(a => a.Context.Length > 0).ToList();
        if (withContext.Count > 0)
        {
            full.Append("\n\n<context from Visual Studio>\n");
            full.Append(string.Join("\n\n", withContext.Select(a => a.Context)));
            full.Append("\n</context from Visual Studio>");
        }

        List<PromptImage> images = attachments.Where(a => a.Image != null).Select(a => a.Image!).ToList();
        int imageCount = images.Count;
        IEnumerable<string> notes = attachments.Where(a => a.Kind != AttachmentKind.Image).Select(a => a.Label);
        if (imageCount > 0)
        {
            notes = notes.Append(imageCount == 1 ? "1 image" : $"{imageCount} images");
        }

        return (new ChatPrompt(full.ToString(), images), string.Join(" · ", notes));
    }

    private static string LineRange(EditorContext editor) =>
        editor.EndLine > editor.StartLine ? $"{editor.StartLine}-{editor.EndLine}" : editor.StartLine.ToString();

    /// <summary>Repository-relative path with forward slashes, or the full path for files outside the repository.</summary>
    internal static string DisplayPath(string fullPath, string repoRoot)
    {
        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return root.Length > 1 && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).Replace('\\', '/')
            : fullPath;
    }

    private static string Fence(string code, string path)
    {
        if (code.Length > MaxSelectionChars)
        {
            code = code.Substring(0, MaxSelectionChars) + "\n… (selection truncated)";
        }

        // A fence longer than any run of backticks in the code can't be closed early by it.
        int longest = 0;
        int run = 0;
        foreach (char c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{SyntaxHighlighter.FenceLanguageForFile(path)}\n{code.TrimEnd('\r', '\n')}\n{fence}";
    }
}
