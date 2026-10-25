using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

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

    /// <summary>A small footer with the tokens used by the prompt above.</summary>
    Usage,

    /// <summary>
    /// The files the prompt above changed, with line counts. Saved as a <see cref="Tool"/> entry with
    /// <see cref="SavedEntry.Files"/> set, so versions without this kind show it as an ordinary tool row.
    /// </summary>
    Changes,
}

/// <summary>One file in a <see cref="ChatEntryKind.Changes"/> entry.</summary>
internal sealed class ChangedFileRow(ChatEntry entry, FileStat stat)
{
    public ChatEntry Entry { get; } = entry;

    public FileStat Stat { get; } = stat;

    public string Path => this.Stat.Path;

    public string StatusText => this.Stat.Status switch { 'A' => "Added", 'D' => "Deleted", _ => "Modified" };

    public string AddedText => this.Stat.Binary ? "binary" : $"+{this.Stat.Added:N0}";

    public string RemovedText => this.Stat.Binary ? string.Empty : $"−{this.Stat.Removed:N0}";

    /// <summary>Line-by-line diffs can only be shown for text files.</summary>
    public bool CanDiff => !this.Stat.Binary;
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
        // The agent's edit previews use "- " / "+ " prefixes, with "@@" hunk headers.
        ActivityKind.Edit when line.StartsWith("@@") => DiffLineKind.Hunk,
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
    private static readonly TimeSpan TextNotifyInterval = TimeSpan.FromMilliseconds(100);

    private readonly StringBuilder buffer = new();
    private string text;
    private bool textStale;
    private DateTime lastTextNotify;
    private DispatcherTimer? textTimer;
    private string? snapshotBefore;
    private string? snapshotAfter;
    private IReadOnlyList<FileStat> files = [];

    public ChatEntry(ChatEntryKind kind, string header, string text = "", string detail = "", ActivityKind activity = ActivityKind.Git, FileChange? change = null)
    {
        this.Kind = kind;
        this.Header = header;
        this.Activity = activity;
        this.buffer.Append(text);
        this.text = text;
        this.DetailLines = DiffLine.Parse(detail, activity);
        this.change = change is null ? null : new CompressedChange(change);
    }

    /// <summary>Header of replies written by the extension itself (/help, /cost, /context) rather than by Claude.</summary>
    public const string LocalReplyHeader = "Claude for VS";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatEntryKind Kind { get; }

    /// <summary>True for the extension's own replies, which aren't part of the conversation with Claude.</summary>
    public bool IsLocalReply => this.Kind == ChatEntryKind.Assistant && this.Header == LocalReplyHeader;

    public ActivityKind Activity { get; }

    public string Header { get; }

    private readonly CompressedChange? change;

    /// <summary>
    /// For file edits: the whole file before and after, for Visual Studio's diff viewer. Not saved with the chat.
    /// Kept compressed, as a chat holds two copies of the file for every edit for as long as it's open, so this
    /// expands them again on each call.
    /// </summary>
    public FileChange? Change => this.change?.Expand();

    public bool HasChange => this.change != null;

    private sealed class CompressedChange(FileChange change)
    {
        private readonly string fullPath = change.FullPath;
        private readonly byte[] before = Compress(change.Before);
        private readonly byte[] after = Compress(change.After);

        public FileChange Expand() => new(this.fullPath, Decompress(this.before), Decompress(this.after));

        private static byte[] Compress(string text)
        {
            if (text.Length == 0)
            {
                return [];
            }

            using var buffer = new System.IO.MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                gzip.Write(bytes, 0, bytes.Length);
            }

            return buffer.ToArray();
        }

        private static string Decompress(byte[] data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            using var gzip = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new System.IO.StreamReader(gzip, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false); // keeps a leading BOM character
            return reader.ReadToEnd();
        }
    }

    // ===== Changes entries only =====

    /// <summary>The files a prompt changed, between <see cref="SnapshotBefore"/> and <see cref="SnapshotAfter"/>.</summary>
    public IReadOnlyList<FileStat> Files
    {
        get => this.files;
        init
        {
            this.files = value;
            this.FileRows = value.Select(f => new ChangedFileRow(this, f)).ToList();
        }
    }

    public IReadOnlyList<ChangedFileRow> FileRows { get; private init; } = [];

    /// <summary>"3 files changed · +42 −10", for exports and older versions.</summary>
    public static string Summarize(IReadOnlyList<FileStat> files) =>
        $"{(files.Count == 1 ? "1 file" : $"{files.Count:N0} files")} changed · +{files.Sum(f => f.Added):N0} −{files.Sum(f => f.Removed):N0}";

    public string FilesChangedText => this.files.Count == 1 ? "1 file changed" : $"{this.files.Count:N0} files changed";

    public string AddedText => $"+{this.files.Sum(f => f.Added):N0}";

    public string RemovedText => $"−{this.files.Sum(f => f.Removed):N0}";

    // ===== Starred messages (prompts and Claude's replies) =====

    private bool isStarred;

    /// <summary>True if the user starred this message to find it again from the header's star menu. Saved with the chat.</summary>
    public bool IsStarred
    {
        get => this.isStarred;
        set
        {
            if (this.isStarred != value)
            {
                this.isStarred = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.StarText));
            }
        }
    }

    public string StarText => this.isStarred ? "Unstar" : "Star";

    /// <summary>Prompts and Claude's replies can be starred; the extension's own replies and notes can't.</summary>
    public bool CanStar => this.Kind == ChatEntryKind.User || (this.Kind == ChatEntryKind.Assistant && !this.IsLocalReply);

    /// <summary>The message's first line of text, cut short, for lists.</summary>
    public string Excerpt
    {
        get
        {
            string first = this.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return first.Length > 70 ? first.Substring(0, 70) + "…" : first;
        }
    }

    // ===== User prompts only =====

    /// <summary>What was attached to a prompt, e.g. "Foo.cs:12-30 · 1 image"; shown under the bubble.</summary>
    public string Note { get; init; } = string.Empty;

    public bool HasNote => this.Note.Length > 0;

    /// <summary>The engine's checkpoint from just before this prompt, for editing or retrying it.</summary>
    public string? Checkpoint { get; set; }

    /// <summary>Exactly what was sent (attached context and images included), for retrying it. Not saved with the chat.</summary>
    public ChatPrompt? SentPrompt { get; set; }

    /// <summary>Working tree snapshot taken just before this prompt ran (or, on a Changes entry, before the prompt above it).</summary>
    public string? SnapshotBefore
    {
        get => this.snapshotBefore;
        set
        {
            this.snapshotBefore = value;
            this.OnPropertyChanged(nameof(this.CanUndo));
            this.OnPropertyChanged(nameof(this.IsRestorePoint));
        }
    }

    /// <summary>Working tree snapshot taken when this prompt finished.</summary>
    public string? SnapshotAfter
    {
        get => this.snapshotAfter;
        set
        {
            this.snapshotAfter = value;
            this.OnPropertyChanged(nameof(this.CanUndo));
        }
    }

    /// <summary>
    /// True for a notice made by /checkpoint, whose <see cref="SnapshotBefore"/> is the working tree as it was then, so
    /// the files can be put back to it.
    /// </summary>
    public bool IsRestorePoint => this.Kind == ChatEntryKind.Notice && this.snapshotBefore != null;

    /// <summary>True when files changed while this prompt ran, so its changes can be undone.</summary>
    public bool CanUndo => this.Kind == ChatEntryKind.User && this.snapshotBefore != null && this.snapshotAfter != null && this.snapshotBefore != this.snapshotAfter;

    /// <summary>Markdown body (rendered for user/assistant entries, plain for thinking and errors).</summary>
    public string Text
    {
        get
        {
            if (this.textStale)
            {
                this.text = this.buffer.ToString();
                this.textStale = false;
            }

            return this.text;
        }
    }

    /// <summary>Expandable monospace detail, e.g. tool output or a diff.</summary>
    public IReadOnlyList<DiffLine> DetailLines { get; private set; }

    public bool HasDetail => this.DetailLines.Count > 0;

    /// <summary>Replaces the detail, e.g. a running command's output so far.</summary>
    public void SetDetail(string detail)
    {
        this.DetailLines = DiffLine.Parse(detail, this.Activity);
        this.OnPropertyChanged(nameof(this.DetailLines));
        this.OnPropertyChanged(nameof(this.HasDetail));
    }

    // ===== Running tool calls only =====

    /// <summary>The task this row stands for while it runs; its steps or output are shown in the Claude Tasks window.</summary>
    public ToolTask? LiveTask { get; init; }

    public bool IsLive => this.LiveTask != null;

    /// <summary>
    /// Adds streamed text. Copying the whole reply out of the buffer (and re-rendering it) for every token gets slow
    /// as replies grow, so the view hears about new text at most every <see cref="TextNotifyInterval"/>; reading
    /// <see cref="Text"/> always gives everything so far.
    /// </summary>
    public void Append(string chunk)
    {
        this.buffer.Append(chunk);
        this.textStale = true;
        TimeSpan since = DateTime.UtcNow - this.lastTextNotify;
        if (since >= TextNotifyInterval)
        {
            this.NotifyText();
        }
        else if (this.textTimer is not { IsEnabled: true })
        {
            if (this.textTimer is null)
            {
                this.textTimer = new DispatcherTimer(DispatcherPriority.Background);
                this.textTimer.Tick += (_, _) => this.NotifyText();
            }

            this.textTimer.Interval = TextNotifyInterval - since;
            this.textTimer.Start();
        }
    }

    public void SetText(string value)
    {
        this.buffer.Clear().Append(value);
        this.text = value;
        this.textStale = false;
        this.NotifyText();
    }

    private void NotifyText()
    {
        this.textTimer?.Stop();
        this.lastTextNotify = DateTime.UtcNow;
        this.OnPropertyChanged(nameof(this.Text));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
