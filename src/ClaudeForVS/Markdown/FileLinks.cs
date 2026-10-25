using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClaudeForVS.Markdown;

/// <summary>A file (and optionally lines) that a link in a reply points to.</summary>
internal sealed record FileTarget(string FullPath, int Line, int EndLine);

/// <summary>A path-like piece of text found in a reply, e.g. "src/Foo.cs:42".</summary>
internal readonly record struct PathMention(int Start, int Length, string Path, int Line, int EndLine);

/// <summary>
/// What a chat window supplies for the links in its replies: <see cref="Resolve"/> (which only accepts files that exist)
/// and <see cref="Open"/>. Each window has its own, so paths resolve against that window's repository.
/// </summary>
internal sealed class FileLinkHandler(Func<string, string?> resolve, Action<FileTarget> open)
{
    // What Resolve said about each path lately. A streaming reply is rendered again several times a second, and each
    // time every path in its last paragraph would otherwise go to the disk. Forgotten after a few seconds, so a file
    // that's created or deleted soon gets its link (or loses it).
    private static readonly TimeSpan ResolvedLifetime = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, string?> resolved = new(StringComparer.Ordinal);
    private DateTime resolvedSince = DateTime.MinValue;

    /// <summary>Maps a path as written in a reply to an existing file, or null.</summary>
    public Func<string, string?> Resolve { get; } = resolve;

    /// <summary>Opens a file in the editor at the given lines.</summary>
    public Action<FileTarget> Open { get; } = open;

    /// <summary>The file a mention refers to, if <see cref="Resolve"/> finds it.</summary>
    public FileTarget? Target(PathMention mention)
    {
        lock (this.resolved)
        {
            if (DateTime.UtcNow - this.resolvedSince > ResolvedLifetime)
            {
                this.resolved.Clear();
                this.resolvedSince = DateTime.UtcNow;
            }

            if (!this.resolved.TryGetValue(mention.Path, out string? full))
            {
                try
                {
                    full = this.Resolve(mention.Path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
                {
                    full = null;
                }

                this.resolved[mention.Path] = full;
            }

            return full is null ? null : new FileTarget(full, mention.Line, mention.EndLine);
        }
    }

    /// <summary>Forgets what paths resolved to, e.g. once the repository's file list has loaded.</summary>
    public void Forget()
    {
        lock (this.resolved)
        {
            this.resolved.Clear();
        }
    }
}

/// <summary>Turns file paths in Claude's replies into links that open the file in the editor.</summary>
internal static class FileLinks
{
    /// <summary>The links for the chat window whose Markdown is being rendered on this thread, if any (see <see cref="Using"/>).</summary>
    [ThreadStatic]
    private static FileLinkHandler? current;

    public static FileLinkHandler? Current => current;

    /// <summary>The chat window a <see cref="MarkdownView"/> is in, inherited from the window down to its messages.</summary>
    public static readonly System.Windows.DependencyProperty HandlerProperty = System.Windows.DependencyProperty.RegisterAttached(
        "Handler", typeof(FileLinkHandler), typeof(FileLinks),
        new System.Windows.FrameworkPropertyMetadata(null, System.Windows.FrameworkPropertyMetadataOptions.Inherits));

    public static FileLinkHandler? GetHandler(System.Windows.DependencyObject element) => (FileLinkHandler?)element.GetValue(HandlerProperty);

    public static void SetHandler(System.Windows.DependencyObject element, FileLinkHandler? value) => element.SetValue(HandlerProperty, value);

    /// <summary>Makes <paramref name="handler"/> the one links use until the result is disposed.</summary>
    public static IDisposable Using(FileLinkHandler? handler)
    {
        FileLinkHandler? previous = current;
        current = handler;
        return new Restore(previous);
    }

    private sealed class Restore(FileLinkHandler? previous) : IDisposable
    {
        public void Dispose() => current = previous;
    }

    // A path ending in a file extension, optionally followed by a line or range: "src/Foo.cs", "Foo.cs:12",
    // "Foo.cs:12-30", "Foo.cs#L12-L30", "C:\x\Foo.cs(12)". Not preceded by characters that would make it part of
    // a URL, a longer word or an e-mail address.
    private static readonly Regex Candidate = new(
        @"(?<![\w/\\.:@%~-])((?:[A-Za-z]:[\\/])?(?:[\w.-]+[\\/])*[\w-]+(?:\.[\w-]+)*\.[A-Za-z][A-Za-z0-9]{0,9})(?:(?::|#L|\()(\d+)(?:[-–]L?(\d+))?\)?)?(?![\w/\\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Path-like text in a run of plain text (not checked against the disk).</summary>
    public static IEnumerable<PathMention> Find(string text)
    {
        foreach (Match match in Candidate.Matches(text))
        {
            int line = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int l) ? l : 0;
            int endLine = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int e) ? e : 0;
            yield return new PathMention(match.Index, match.Length, match.Groups[1].Value, line, endLine);
        }
    }

    /// <summary>Parses text that should be entirely a path (inline code or a link target), e.g. "src/Foo.cs:12".</summary>
    public static PathMention? ParseWhole(string text)
    {
        text = text.Trim();
        foreach (PathMention mention in Find(text))
        {
            if (mention.Start == 0 && mention.Length == text.Length)
            {
                return mention;
            }
        }

        return null;
    }

    /// <summary>The file a mention refers to, if the current chat window's links find it.</summary>
    public static FileTarget? Target(PathMention mention) => current?.Target(mention);
}
