using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeForVS.Agent;

/// <summary>
/// File access confined to one repository. Every path is resolved and checked to be inside the
/// repository root, outside .git, and not routed through a symlink or junction that could point elsewhere.
/// </summary>
internal sealed class RepoFiles
{
    private readonly string root;

    public RepoFiles(string repoRoot)
    {
        this.root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Resolves a repository-relative path, throwing <see cref="ToolInputException"/> if it escapes the repository.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ToolInputException("`path` must not be empty.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(this.root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolInputException($"Invalid path: {relativePath}");
        }

        if (!full.StartsWith(this.root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolInputException($"Access denied: {relativePath} is outside the repository.");
        }

        string relative = this.Relative(full);
        if (relative.Split(Path.DirectorySeparatorChar).Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolInputException("Access denied: files under .git can't be accessed directly; use the git tool.");
        }

        // Reject any existing component that is a reparse point (symlink/junction): it could lead outside the repo.
        string current = this.root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ToolInputException($"Access denied: {relativePath} goes through a symbolic link or junction.");
            }
        }

        return full;
    }

    public string Relative(string fullPath)
        => fullPath.Length > this.root.Length ? fullPath.Substring(this.root.Length + 1) : string.Empty;

    public TextFile Read(string relativePath)
    {
        string full = this.Resolve(relativePath);
        if (!File.Exists(full))
        {
            throw new ToolInputException($"File not found: {relativePath}");
        }

        long length = new FileInfo(full).Length;
        if (length > MaxReadBytes)
        {
            throw new ToolInputException(
                $"{relativePath} is {length / (1024.0 * 1024):0.#} MB, too large to read or edit here (the limit is {MaxReadBytes / (1024 * 1024)} MB). "
                + "Use grep to find what you need in it, or run_command for anything else.");
        }

        (string text, Encoding encoding) = Decode(File.ReadAllBytes(full), relativePath);

        // A file being edited in Visual Studio is read as the editor has it, which is what an edit to it changes.
        bool unsaved = false;
        if (OpenDocuments.UnsavedText(full) is string editorText)
        {
            text = editorText;
            unsaved = true;
        }

        return new TextFile(full, text, encoding, text.Contains("\r\n") ? "\r\n" : "\n", unsaved);
    }

    // Beyond this a file is generated or data, not something to read whole into memory (and the conversation).
    internal const long MaxReadBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Decodes a text file, returning the encoding to write it back with. A BOM decides the encoding (UTF-16 files
    /// are full of zero bytes, so this comes before the binary check). Without one, valid UTF-8 is read as UTF-8
    /// without a BOM, and anything else as the system's ANSI code page (e.g. Windows-1252), so non-ASCII characters
    /// survive an edit rather than being replaced with U+FFFD.
    /// </summary>
    internal static (string Text, Encoding Encoding) Decode(byte[] bytes, string displayPath)
    {
        Encoding? withBom =
            StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00) ? new UTF32Encoding(bigEndian: false, byteOrderMark: true)
            : StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF) ? new UTF32Encoding(bigEndian: true, byteOrderMark: true)
            : StartsWith(bytes, 0xEF, 0xBB, 0xBF) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            : StartsWith(bytes, 0xFF, 0xFE) ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
            : StartsWith(bytes, 0xFE, 0xFF) ? new UnicodeEncoding(bigEndian: true, byteOrderMark: true)
            : null;
        if (withBom != null)
        {
            int bom = withBom.GetPreamble().Length;
            return (withBom.GetString(bytes, bom, bytes.Length - bom), withBom);
        }

        if (bytes.Take(8000).Contains((byte)0))
        {
            throw new ToolInputException($"{displayPath} looks like a binary file.");
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (utf8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Default.GetString(bytes), Encoding.Default);
        }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
        => bytes.Length >= prefix.Length && prefix.Select((b, i) => bytes[i] == b).All(match => match);

    /// <summary>
    /// Writes a file. One open in Visual Studio is changed in its editor (see <see cref="OpenDocuments.WriteToEditor"/>),
    /// so the user can undo it there and isn't asked to reload it; otherwise it's written through <see cref="AtomicFile"/>,
    /// with the encoding's BOM if it has one, as the file was read.
    /// </summary>
    public EditorWrite Write(string fullPath, string text, Encoding encoding)
    {
        EditorWrite written = File.Exists(fullPath) ? OpenDocuments.WriteToEditor(fullPath, text) : EditorWrite.NotOpen;
        if (written == EditorWrite.NotOpen)
        {
            AtomicFile.WriteAllBytes(fullPath, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
        }

        return written;
    }

    /// <summary>What Claude is told when its change went into an editor with unsaved changes rather than to disk.</summary>
    public static string LeftUnsavedNote(string relative) =>
        $" {relative} had unsaved changes in Visual Studio, so the change is in its editor, not yet saved: builds, tests and git see the saved copy until the user saves it.";

    /// <summary>Converts \n line endings from Claude to the file's own convention.</summary>
    public static string NormalizeNewlines(string text, string newline)
    {
        string lf = text.Replace("\r\n", "\n");
        return newline == "\n" ? lf : lf.Replace("\n", newline);
    }

    /// <param name="Unsaved">True if <paramref name="Text"/> is the editor's, with changes not yet saved to disk.</param>
    internal sealed record TextFile(string FullPath, string Text, Encoding Encoding, string Newline, bool Unsaved = false);
}
