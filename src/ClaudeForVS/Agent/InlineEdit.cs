using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>
/// Editing code in place from the editor (Ctrl+Alt+/): Claude rewrites some lines of a file as the user asks, and its
/// reply replaces them. One request with no tools, so it's quick and never touches other files.
/// </summary>
internal static class InlineEdit
{
    // Past this, only the lines around the ones being edited are sent.
    private const int MaxFileChars = 60_000;
    private const int ContextLines = 150;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Asks Claude for the new text of lines <paramref name="startLine"/> to <paramref name="endLine"/> (1-based) of a file.</summary>
    /// <returns>The lines to put in their place, with \n line endings and no final line break.</returns>
    public static async Task<string> RequestAsync(
        AgentSettings settings, string repoRoot, string displayPath, string fileText, int startLine, int endLine, string instruction, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        string prompt = BuildPrompt(displayPath, fileText, startLine, endLine, instruction);
        string reply = await OneShot.AskAsync(settings, repoRoot, settings.Model, prompt, () => ProjectInstructions.Load(repoRoot).Text, timeout.Token).ConfigureAwait(false);
        return ExtractCode(reply) ?? throw new InvalidOperationException("Claude didn't reply with code.");
    }

    /// <summary>The request: the file (or the part around the lines), with the lines to rewrite marked. Internal for tests.</summary>
    internal static string BuildPrompt(string displayPath, string fileText, int startLine, int endLine, string instruction)
    {
        string[] lines = fileText.Replace("\r\n", "\n").Split('\n');
        int first = 1, last = lines.Length;
        if (fileText.Length > MaxFileChars)
        {
            first = Math.Max(1, startLine - ContextLines);
            last = Math.Min(lines.Length, endLine + ContextLines);
        }

        var sb = new StringBuilder();
        sb.Append("You're editing code in place in the user's editor in Visual Studio. Rewrite the lines of ").Append(displayPath)
          .Append(" between <rewrite> and </rewrite> as the instruction below says.\n\n");
        sb.Append("Reply with only the new code for those lines, in a single fenced code block: no explanation before or after it. ");
        sb.Append("It replaces exactly the marked lines, so keep any of them that should stay, and match the file's indentation and style. ");
        sb.Append("If the marked lines are empty, write the code that belongs there. Don't change anything outside the marked lines.\n\n");
        sb.Append("<instruction>\n").Append(instruction.Trim()).Append("\n</instruction>\n\n");
        sb.Append($"<file path=\"{displayPath}\"{(first > 1 || last < lines.Length ? $" lines=\"{first}-{last}\"" : string.Empty)}>\n");
        for (int i = first; i <= last; i++)
        {
            if (i == startLine)
            {
                sb.Append("<rewrite>\n");
            }

            sb.Append(lines[i - 1]).Append('\n');
            if (i == endLine)
            {
                sb.Append("</rewrite>\n");
            }
        }

        sb.Append("</file>\n");
        return sb.ToString();
    }

    /// <summary>The code in a reply: its first fenced block, or the whole reply if it has none; null if there's nothing. Internal for tests.</summary>
    internal static string? ExtractCode(string reply)
    {
        string text = reply.Replace("\r\n", "\n");
        Match fenced = Regex.Match(text, @"(?m)^[ \t]*(`{3,}|~{3,})[^\n]*\n(.*?)\n?^[ \t]*\1[ \t]*$", RegexOptions.Singleline);
        string code = fenced.Success ? fenced.Groups[2].Value : text.Trim('\n');
        code = code.TrimEnd('\n', ' ', '\t');

        // Strip the markers if Claude echoed them back.
        code = Regex.Replace(code, @"(?m)^[ \t]*</?rewrite>[ \t]*\n?", string.Empty).TrimEnd('\n');
        return code.Trim().Length == 0 && !fenced.Success ? null : code;
    }

    /// <summary>Claude's code (\n line endings) in the file's own line endings.</summary>
    public static string ToFileNewlines(string code, string newline) => newline == "\n" ? code : code.Replace("\n", newline);

    /// <summary>A short label for the box: "lines 4-12" or "line 4".</summary>
    public static string Describe(int startLine, int endLine) => startLine == endLine ? $"line {startLine}" : $"lines {startLine}-{endLine}";
}
