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

        byte[] bytes = File.ReadAllBytes(full);
        if (bytes.Take(8000).Contains((byte)0))
        {
            throw new ToolInputException($"{relativePath} looks like a binary file.");
        }

        // Detect the encoding from the BOM so it can be preserved on write; default to UTF-8 without BOM.
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true))
        {
            string text = reader.ReadToEnd();
            Encoding detected = reader.CurrentEncoding;
            bool hasBom = detected.GetPreamble().Length > 0 && bytes.Take(detected.GetPreamble().Length).SequenceEqual(detected.GetPreamble());
            if (detected is UTF8Encoding && !hasBom)
            {
                detected = encoding;
            }

            return new TextFile(full, text, detected, text.Contains("\r\n") ? "\r\n" : "\n");
        }
    }

    public void Write(string fullPath, string text, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text, encoding);
    }

    /// <summary>Converts \n line endings from Claude to the file's own convention.</summary>
    public static string NormalizeNewlines(string text, string newline)
    {
        string lf = text.Replace("\r\n", "\n");
        return newline == "\n" ? lf : lf.Replace("\n", newline);
    }

    internal sealed record TextFile(string FullPath, string Text, Encoding Encoding, string Newline);
}
