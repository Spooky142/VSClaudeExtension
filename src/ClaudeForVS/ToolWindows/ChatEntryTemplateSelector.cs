using System.Windows;
using System.Windows.Controls;

namespace ClaudeForVS.ToolWindows;

/// <summary>Picks the transcript template for each kind of <see cref="ChatEntry"/>.</summary>
internal sealed class ChatEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }

    public DataTemplate? Assistant { get; set; }

    public DataTemplate? Thinking { get; set; }

    public DataTemplate? Tool { get; set; }

    public DataTemplate? Error { get; set; }

    public DataTemplate? Notice { get; set; }

    public DataTemplate? Usage { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as ChatEntry)?.Kind switch
    {
        ChatEntryKind.User => this.User,
        ChatEntryKind.Assistant => this.Assistant,
        ChatEntryKind.Thinking => this.Thinking,
        ChatEntryKind.Tool => this.Tool,
        ChatEntryKind.Error => this.Error,
        ChatEntryKind.Notice => this.Notice,
        ChatEntryKind.Usage => this.Usage,
        _ => null,
    };
}
