using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.Extensibility.UI;

namespace ClaudeForVS.ToolWindows;

/// <summary>One item in the chat transcript.</summary>
[DataContract]
internal class ChatEntry : NotifyPropertyChangedObject
{
    private readonly StringBuilder buffer = new();
    private string text = string.Empty;
    private string detail = string.Empty;
    private DateTime lastFlush = DateTime.MinValue;

    public ChatEntry(string kind, string header, string text = "")
    {
        this.Kind = kind;
        this.Header = header;
        this.buffer.Append(text);
        this.text = text;
    }

    /// <summary>user | assistant | thinking | tool | error. The XAML styles on this.</summary>
    [DataMember]
    public string Kind { get; }

    [DataMember]
    public string Header { get; }

    [DataMember]
    public string Text
    {
        get => this.text;
        private set => this.SetProperty(ref this.text, value);
    }

    [DataMember]
    public string Detail
    {
        get => this.detail;
        set
        {
            this.SetProperty(ref this.detail, value);
            this.RaiseNotifyPropertyChangedEvent(nameof(this.HasDetail));
        }
    }

    [DataMember]
    public bool HasDetail => this.detail.Length > 0;

    /// <summary>Appends streamed text, pushing it to the UI at most every 60 ms to limit RPC traffic.</summary>
    public void Append(string chunk)
    {
        lock (this.buffer)
        {
            this.buffer.Append(chunk);
            if ((DateTime.UtcNow - this.lastFlush).TotalMilliseconds >= 60)
            {
                this.FlushLocked();
            }
        }
    }

    public void Flush()
    {
        lock (this.buffer)
        {
            this.FlushLocked();
        }
    }

    private void FlushLocked()
    {
        this.lastFlush = DateTime.UtcNow;
        this.Text = this.buffer.ToString();
    }
}
