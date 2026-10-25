using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>What <see cref="RepoSearch.Grep"/> returns.</summary>
internal enum GrepOutput
{
    /// <summary>Matching lines, with any context lines, as "path:line:text".</summary>
    Content,

    /// <summary>Only the paths of files with a match.</summary>
    FilesWithMatches,

    /// <summary>The number of matching lines in each file.</summary>
    Count,
}

/// <summary>
/// The Claude API engine's <c>glob</c> and <c>grep</c> tools. Both search the files git lists for the repository
/// (tracked and untracked, minus ignored ones), so build output and dependencies stay out of the results.
/// </summary>
internal static class RepoSearch
{
    public const int MaxGlobResults = 500;
    public const int MaxOutputChars = 30_000;
    private const int MaxFileBytes = 2_000_000;
    private const int MaxLineChars = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // A grep pattern with any of these (a group with options or a lookaround, or \A, \z, \Z or \G) may match a line on
    // its own yet not within the whole file, so files aren't checked whole first.
    private static readonly Regex UnsafeForWholeText = new(@"\(\?(?!:)|\\[AzZG]", RegexOptions.CultureInvariant);

    /// <summary>The repository's files as repository-relative paths with forward slashes, sorted, skipping any deleted from disk.</summary>
    public static async Task<IReadOnlyList<string>> ListFilesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // git lists the tracked files deleted from disk too, rather than each file being looked for.
        Task<(int ExitCode, string Output)> listing = GitRunner.RunAsync(
            repoRoot, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken, includeStderr: false);
        Task<(int ExitCode, string Output)> deleting = GitRunner.RunAsync(repoRoot, ["ls-files", "--deleted", "-z"], cancellationToken, includeStderr: false);
        await Task.WhenAll(listing, deleting).ConfigureAwait(false);
        (int code, string output) = await listing.ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException("git couldn't list the repository's files.");
        }

        (int deletedCode, string deletedOutput) = await deleting.ConfigureAwait(false);
        var deleted = new HashSet<string>(deletedCode == 0 ? deletedOutput.Split(['\0'], StringSplitOptions.RemoveEmptyEntries) : [], StringComparer.Ordinal);
        return output.Split(['\0'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Where(f => !deleted.Contains(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Converts a glob to a case-insensitive regex over forward-slash paths: <c>**</c> matches any number of folders,
    /// <c>*</c> and <c>?</c> stay within one, <c>{a,b}</c> is either and <c>[...]</c> is a character class.
    /// </summary>
    public static Regex GlobToRegex(string glob)
    {
        string g = glob.Replace('\\', '/');
        var sb = new StringBuilder("^");
        int braces = 0;
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            switch (c)
            {
                case '*' when i + 1 < g.Length && g[i + 1] == '*':
                    bool slashAfter = i + 2 < g.Length && g[i + 2] == '/';
                    sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                    i += slashAfter ? 2 : 1;
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    sb.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    sb.Append(')');
                    break;
                case ',' when braces > 0:
                    sb.Append('|');
                    break;
                case '[':
                    int close = g.IndexOf(']', i + 2);
                    if (close < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }

                    string set = g.Substring(i + 1, close - i - 1);
                    if (set.StartsWith("!"))
                    {
                        set = "^" + set.Substring(1);
                    }

                    sb.Append('[').Append(set.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        if (braces > 0)
        {
            throw new ToolInputException($"Unbalanced braces in the pattern: {glob}");
        }

        try
        {
            return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            throw new ToolInputException($"Invalid pattern: {glob}");
        }
    }

    /// <summary>
    /// The files matching a glob, relative to <paramref name="folder"/> (a repository-relative folder, or empty for the
    /// root). A pattern without a "/" matches file names at any depth, so "*.cs" finds every C# file.
    /// </summary>
    public static IReadOnlyList<string> Glob(IReadOnlyList<string> files, string pattern, string folder)
    {
        Regex regex = GlobToRegex(pattern);
        bool nameOnly = !pattern.Replace('\\', '/').Contains('/');
        return Under(files, folder)
            .Where(f =>
            {
                string relative = folder.Length == 0 ? f : f.Substring(folder.Length + 1);
                return regex.IsMatch(nameOnly ? relative.Substring(relative.LastIndexOf('/') + 1) : relative);
            })
            .ToList();
    }

    /// <summary>The files inside a repository-relative folder or equal to a file path; every file when it's empty.</summary>
    public static IEnumerable<string> Under(IReadOnlyList<string> files, string folderOrFile)
    {
        string prefix = folderOrFile.Replace('\\', '/').Trim('/');
        return prefix.Length == 0
            ? files
            : files.Where(f => f.Equals(prefix, StringComparison.OrdinalIgnoreCase) || f.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Searches file contents line by line with a .NET regular expression.</summary>
    /// <param name="files">Repository-relative paths to search, from <see cref="ListFilesAsync"/>.</param>
    /// <param name="context">Lines to show before and after each match, for <see cref="GrepOutput.Content"/>.</param>
    /// <param name="maxResults">Most lines (or files, or counts) to return.</param>
    public static string Grep(
        string repoRoot,
        IEnumerable<string> files,
        string pattern,
        bool ignoreCase,
        GrepOutput mode,
        int context,
        int maxResults,
        CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ToolInputException($"Invalid regular expression: {ex.Message}");
        }

        // Most files have no match, so each is first checked whole, which is much quicker than line by line. That's only
        // done when a match within a line is sure to be a match within the whole text too: not with lookarounds, inline
        // options or anchors to the start or end of the text, which see past a line's ends there.
        Regex? wholeText = UnsafeForWholeText.IsMatch(pattern)
            ? null
            : new Regex(pattern, regex.Options | RegexOptions.Multiline, RegexTimeout);

        var output = new StringBuilder();
        var linkFree = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int results = 0;
        int matchedFiles = 0;
        int totalMatches = 0;
        bool truncated = false;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
            if (!IsFreeOfLinks(repoRoot, file, linkFree) || ReadText(full) is not string text)
            {
                continue;
            }

            text = text.Replace("\r\n", "\n");
            var hits = new List<int>();
            string[] lines;
            try
            {
                if (wholeText?.IsMatch(text) == false)
                {
                    continue;
                }

                lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        hits.Add(i);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ToolInputException($"The regular expression took too long on {file}. Simplify it (avoid nested quantifiers).");
            }

            if (hits.Count == 0)
            {
                continue;
            }

            matchedFiles++;
            totalMatches += hits.Count;
            switch (mode)
            {
                case GrepOutput.FilesWithMatches:
                    output.Append(file).Append('\n');
                    results++;
                    break;

                case GrepOutput.Count:
                    output.Append(file).Append(':').Append(hits.Count).Append('\n');
                    results++;
                    break;

                default:
                    int shownUntil = -1;
                    foreach (int hit in hits)
                    {
                        int from = Math.Max(Math.Max(0, hit - context), shownUntil + 1);
                        int to = Math.Min(lines.Length - 1, hit + context);
                        if (context > 0 && shownUntil >= 0 && from > shownUntil + 1)
                        {
                            output.Append("--\n");
                        }

                        for (int i = from; i <= to; i++)
                        {
                            bool isHit = i == hit || hits.BinarySearch(i) >= 0;
                            string line = lines[i].Length > MaxLineChars ? lines[i].Substring(0, MaxLineChars) + "…" : lines[i];
                            output.Append(file).Append(isHit ? ':' : '-').Append(i + 1).Append(isHit ? ':' : '-').Append(line).Append('\n');
                            if (isHit)
                            {
                                results++;
                            }
                        }

                        shownUntil = Math.Max(shownUntil, to);
                        if (results >= maxResults)
                        {
                            break;
                        }
                    }

                    if (context > 0)
                    {
                        output.Append("--\n");
                    }

                    break;
            }

            // The rest of the files aren't read just to count their matches.
            if (results >= maxResults || output.Length > MaxOutputChars)
            {
                truncated = true;
                break;
            }
        }

        if (matchedFiles == 0)
        {
            return "No matches.";
        }

        string result = output.Length > MaxOutputChars ? output.ToString(0, MaxOutputChars) + "…\n" : output.ToString();
        string atLeast = truncated ? "at least " : string.Empty;
        string summary = mode == GrepOutput.Content
            ? $"{atLeast}{totalMatches} matching line{(totalMatches == 1 ? "" : "s")} in {atLeast}{matchedFiles} file{(matchedFiles == 1 ? "" : "s")}"
            : $"{atLeast}{matchedFiles} file{(matchedFiles == 1 ? "" : "s")}";
        return truncated
            ? result + $"[{summary}; results were cut off. Narrow the search with path or glob, or raise max_results.]"
            : result + $"[{summary}]";
    }

    /// <summary>A file's text, or null if it's too large, binary or unreadable.</summary>
    private static string? ReadText(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
            {
                return null;
            }

            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True if none of the folders on the way to a file is a symbolic link or junction, which could lead outside the
    /// repository. Results are cached per folder.
    /// </summary>
    private static bool IsFreeOfLinks(string repoRoot, string file, Dictionary<string, bool> cache)
    {
        int slash = file.LastIndexOf('/');
        if (slash < 0)
        {
            return true;
        }

        string folder = file.Substring(0, slash);
        if (cache.TryGetValue(folder, out bool known))
        {
            return known;
        }

        bool free = IsFreeOfLinks(repoRoot, folder, cache);
        if (free)
        {
            try
            {
                free = (File.GetAttributes(Path.Combine(repoRoot, folder.Replace('/', Path.DirectorySeparatorChar))) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                free = false;
            }
        }

        cache[folder] = free;
        return free;
    }
}
