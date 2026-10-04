using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>Writes a chat out as a Markdown document.</summary>
internal static class ChatExporter
{
    public static string ToMarkdown(SessionInfo info, IReadOnlyList<SavedEntry> entries)
    {
        var md = new StringBuilder();
        md.Append("# ").Append(info.Title.Length > 0 ? info.Title : "Claude chat").Append("\n\n");
        string via = info.Engine == ChatEngineKind.ClaudeCode ? " via Claude Code" : string.Empty;
        md.Append($"_{Path.GetFileName(info.RepoPath.TrimEnd('\\', '/'))} · {AgentSettings.ModelName(info.Model)}{via} · {info.Created.LocalDateTime:d MMM yyyy HH:mm}_\n\n");

        foreach (SavedEntry entry in entries)
        {
            switch (entry.Kind)
            {
                case ChatEntryKind.User:
                    md.Append("## You\n\n").Append(entry.Text.Trim()).Append("\n\n");
                    if (!string.IsNullOrEmpty(entry.Note))
                    {
                        md.Append("_Attached: ").Append(entry.Note).Append("_\n\n");
                    }

                    break;

                // The extension's own replies to /help, /cost and /context aren't part of the conversation.
                case ChatEntryKind.Assistant when entry.Header == ChatEntry.LocalReplyHeader:
                    break;

                case ChatEntryKind.Assistant:
                    md.Append("## Claude\n\n").Append(entry.Text.Trim()).Append("\n\n");
                    break;

                case ChatEntryKind.Tool when entry.Files is { Count: > 0 } files:
                    md.Append("_").Append(ChatEntry.Summarize(files)).Append("_\n\n");
                    foreach (FileStat file in files)
                    {
                        string counts = file.Binary ? "binary" : $"+{file.Added} −{file.Removed}";
                        md.Append("- `").Append(file.Path.Replace("`", "'")).Append("` ").Append(counts).Append('\n');
                    }

                    md.Append('\n');
                    break;

                case ChatEntryKind.Tool:
                    md.Append("- ").Append(ToolPrefix(entry.Activity)).Append('`').Append(entry.Header.Replace("`", "'")).Append("`\n\n");
                    break;

                case ChatEntryKind.Error:
                    md.Append("> **").Append(entry.Header).Append("**: ").Append(entry.Text.Trim()).Append("\n\n");
                    break;

                case ChatEntryKind.Notice:
                    md.Append("---\n\n_").Append(entry.Header).Append("_\n\n");
                    break;

                case ChatEntryKind.Usage:
                    md.Append("<sub>").Append(entry.Text).Append("</sub>\n\n");
                    break;

                // Thinking is left out: it's Claude's working, not part of the conversation.
            }
        }

        return md.ToString().TrimEnd() + "\n";
    }

    /// <summary>A file name for the export, made from the chat's title.</summary>
    public static string SuggestedFileName(SessionInfo info)
    {
        string title = info.Title.Length > 0 ? info.Title : "Claude chat";
        string safe = new(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim().TrimEnd('.', '…');
        return (safe.Length > 60 ? safe.Substring(0, 60).Trim() : safe) + ".md";
    }

    private static string ToolPrefix(ActivityKind activity) => activity switch
    {
        ActivityKind.Declined => "Declined: ",
        ActivityKind.Failed => "Failed: ",
        _ => string.Empty,
    };
}
