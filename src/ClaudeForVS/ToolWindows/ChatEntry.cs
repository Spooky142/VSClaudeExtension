using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClaudeForVS.ToolWindows;

internal enum ChatEntryKind
{
    User,
    Assistant,
    Thinking,
    Tool,
    Error,
}

/// <summary>One item in the chat transcript.</summary>
internal sealed class ChatEntry : INotifyPropertyChanged
{
    private readonly StringBuilder buffer = new();
    private string text;

    public ChatEntry(ChatEntryKind kind, string header, string text = "", string detail = "")
    {
        this.Kind = kind;
        this.Header = header;
        this.buffer.Append(text);
        this.text = text;
        this.Detail = detail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatEntryKind Kind { get; }

    public string Header { get; }

    /// <summary>Markdown body (rendered for user/assistant entries, plain for thinking).</summary>
    public string Text
    {
        get => this.text;
        private set
        {
            this.text = value;
            this.OnPropertyChanged();
        }
    }

    /// <summary>Monospace detail shown in an expander, e.g. tool output or a diff.</summary>
    public string Detail { get; }

    public bool HasDetail => this.Detail.Length > 0;

    public void Append(string chunk)
    {
        this.buffer.Append(chunk);
        this.Text = this.buffer.ToString();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
