using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

internal enum Approval
{
    Deny,
    Allow,

    /// <summary>Allow this and every later request of the same kind in this conversation.</summary>
    AllowAll,
}

/// <summary>How the agent reports progress back to the UI. Called on the UI thread.</summary>
internal interface IChatSink
{
    /// <summary>Streams a chunk of Claude's visible answer.</summary>
    void AppendText(string text);

    /// <summary>Streams a chunk of Claude's summarized reasoning.</summary>
    void AppendThinking(string text);

    /// <summary>Shows a completed tool call and its (possibly truncated) output.</summary>
    void ToolActivity(string title, string detail);

    void SetStatus(string? status);

    /// <summary>Asks the user to allow an action that changes the repository.</summary>
    /// <param name="title">One-line summary, e.g. the git command line or "Edit src/Foo.cs".</param>
    /// <param name="detail">Optional longer preview, e.g. a diff.</param>
    /// <param name="allowAllLabel">If not null, offers a third button that approves all similar requests.</param>
    Task<Approval> RequestApprovalAsync(string title, string detail, string? allowAllLabel, CancellationToken cancellationToken);
}
