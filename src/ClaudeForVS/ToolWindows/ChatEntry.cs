using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

internal enum ChatEntryKind
{
    User,
    Assistant,
    Thinking,
    Tool,
    Error,

    /// <summary>A small centred note, e.g. "New conversation".</summary>
    Notice,

    /// <summary>Only in chats saved before 0.3.12, which kept the tokens as a footer below the reply; now shown on the user entry.</summary>
    Usage,
}

internal enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
}

/// <summary>One line of tool output or a diff preview.</summary>
internal sealed record DiffLine(string Text, DiffLineKind Kind)
{
    /// <summary>Splits detail text into lines, classifying them by the activity that produced it.</summary>
    public static IReadOnlyList<DiffLine> Parse(string detail, ActivityKind activity)
    {
        if (detail.Length == 0)
        {
            return [];
        }

        return detail.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => new DiffLine(line, Classify(line, activity))).ToList();
    }

    private static DiffLineKind Classify(string line, ActivityKind activity) => activity switch
    {
        // The agent's edit previews use "- " / "+ " prefixes.
        ActivityKind.Edit when line.StartsWith("+ ") => DiffLineKind.Added,
        ActivityKind.Edit when line.StartsWith("- ") => DiffLineKind.Removed,
        ActivityKind.Create => DiffLineKind.Added,

        // Unified diff output from git diff/show/log -p.
        ActivityKind.Git when line.StartsWith("@@") => DiffLineKind.Hunk,
        ActivityKind.Git when line.StartsWith("+") && !line.StartsWith("+++") => DiffLineKind.Added,
        ActivityKind.Git when line.StartsWith("-") && !line.StartsWith("---") => DiffLineKind.Removed,
        _ => DiffLineKind.Context,
    };
}

/// <summary>One item in the chat transcript.</summary>
internal sealed class ChatEntry : INotifyPropertyChanged
{
    private readonly StringBuilder buffer = new();
    private string text;
    private string usage = string.Empty;

    public ChatEntry(ChatEntryKind kind, string header, string text = "", string detail = "", ActivityKind activity = ActivityKind.Git)
    {
        this.Kind = kind;
        this.Header = header;
        this.Activity = activity;
        this.buffer.Append(text);
        this.text = text;
        this.DetailLines = DiffLine.Parse(detail, activity);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatEntryKind Kind { get; }

    public ActivityKind Activity { get; }

    public string Header { get; }

    /// <summary>Markdown body (rendered for user/assistant entries, plain for thinking and errors).</summary>
    public string Text
    {
        get => this.text;
        private set
        {
            this.text = value;
            this.OnPropertyChanged();
        }
    }

    /// <summary>For a user entry: the tokens its prompt used, shown beside the message. Empty until the prompt reports any.</summary>
    public string Usage
    {
        get => this.usage;
        set
        {
            this.usage = value;
            this.OnPropertyChanged();
        }
    }

    /// <summary>Expandable monospace detail, e.g. tool output or a diff.</summary>
    public IReadOnlyList<DiffLine> DetailLines { get; }

    public bool HasDetail => this.DetailLines.Count > 0;

    public void Append(string chunk)
    {
        this.buffer.Append(chunk);
        this.Text = this.buffer.ToString();
    }

    public void SetText(string value)
    {
        this.buffer.Clear().Append(value);
        this.Text = value;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
