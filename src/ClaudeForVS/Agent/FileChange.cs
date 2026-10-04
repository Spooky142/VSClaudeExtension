using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeForVS.Agent;

/// <summary>A whole-file before/after for one edit, so it can be opened in Visual Studio's diff viewer.</summary>
/// <param name="FullPath">The file's absolute path.</param>
/// <param name="Before">The file's text before the edit; empty for a new file.</param>
/// <param name="After">The file's text after the edit.</param>
internal sealed record FileChange(string FullPath, string Before, string After)
{
    /// <summary>The file's current text, or empty if it doesn't exist or can't be read.</summary>
    public static string ReadOrEmpty(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Works out what one of Claude Code's edit tools will do to a file, given the file's text beforehand.
    /// Returns null for tools that don't edit files or input that can't be applied.
    /// </summary>
    public static FileChange? ForClaudeCodeTool(string tool, JsonElement input, string fullPath, string before)
    {
        string? after = tool switch
        {
            "Write" => Str(input, "content"),
            "Edit" => Apply(before, [input]),
            "MultiEdit" => input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out JsonElement edits) && edits.ValueKind == JsonValueKind.Array
                ? Apply(before, edits.EnumerateArray())
                : null,
            _ => null,
        };

        return after is null ? null : new FileChange(fullPath, before, after);
    }

    /// <summary>Applies old_string/new_string edits in order. Matches either line-ending style, as the tools do.</summary>
    private static string? Apply(string text, IEnumerable<JsonElement> edits)
    {
        foreach (JsonElement edit in edits)
        {
            string? oldString = Str(edit, "old_string");
            string newString = Str(edit, "new_string") ?? string.Empty;
            if (string.IsNullOrEmpty(oldString))
            {
                return null;
            }

            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string find = RepoFiles.NormalizeNewlines(oldString!, newline);
            string replace = RepoFiles.NormalizeNewlines(newString, newline);
            int at = text.IndexOf(find, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            bool all = edit.TryGetProperty("replace_all", out JsonElement ra) && ra.ValueKind == JsonValueKind.True;
            text = all ? text.Replace(find, replace) : text.Substring(0, at) + replace + text.Substring(at + find.Length);
        }

        return text;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
